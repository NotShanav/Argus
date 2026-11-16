using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PresenceGuard
{
    public sealed class GuardController : IGuardController
    {
        enum Verdict { Match, Mismatch, Miss, Unavailable }

        const float MinQuality = 0.35f;
        static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

        readonly IIrCamera cam;
        readonly IFaceEngine engine;
        readonly IEnrollmentStore store;
        readonly IPresenceSensor presence;
        readonly ILockAnimation anim;
        readonly IConfigStore cfg;
        readonly IActivityLog log;

        readonly Random rng = new();
        readonly object gate = new();
        readonly SemaphoreSlim wake = new(0);
        readonly SemaphoreSlim checkLock = new(1, 1);

        CancellationTokenSource? cts;
        volatile GuardState state = GuardState.Starting;
        volatile bool paused, sessionLocked, suspended, camFault;
        volatile string problemText = "";
        string lastRawDetail = "";
        // DateTime values stored as UTC ticks (0 = none) so they can be read from any thread.
        long nextTicks, lastOkTicks, graceTicks, pauseTicks, lastCheckEndTicks;
        int earlyFlag;
        int okStreak, missCount, mismatchCount, unavailCount, deferrals;
        bool warned;

        public event Action<GuardState>? StateChanged;

        public GuardController(IIrCamera cam, IFaceEngine engine, IEnrollmentStore store, IPresenceSensor presence,
                               ILockAnimation anim, IConfigStore cfg, IActivityLog log)
        {
            this.cam = cam; this.engine = engine; this.store = store; this.presence = presence;
            this.anim = anim; this.cfg = cfg; this.log = log;
        }

        // ------------------------------------------------------------ public surface
        public GuardState State => state;

        public DateTime? NextCheckLocal
        {
            get
            {
                var n = Get(ref nextTicks);
                bool show = state is GuardState.Idle or GuardState.Checking or GuardState.CameraProblem;
                return n != null && show ? n.Value.ToLocalTime() : null;
            }
        }

        public string StatusText => state switch
        {
            GuardState.Starting => "Starting...",
            GuardState.Checking => "Checking...",
            GuardState.Paused => PauseText(),
            GuardState.Locked => "Locked",
            GuardState.NeedsEnrollment => "Not enrolled - enroll from the tray menu",
            GuardState.CameraProblem => CamText(),
            _ => IdleText(),
        };

        public void Start()
        {
            CancellationToken token;
            lock (gate)
            {
                if (cts != null) return;
                cts = new CancellationTokenSource();
                token = cts.Token;
            }
            SystemEvents.SessionSwitch += OnSession;
            SystemEvents.PowerModeChanged += OnPower;
            presence.Changed += OnPresence;
            cfg.Changed += OnConfigChanged;
            log.Add(LogKind.Info, "Guard started");
            Task.Run(() => LoopAsync(token));
        }

        public void Stop()
        {
            CancellationTokenSource? c;
            lock (gate) { c = cts; cts = null; }
            if (c == null) return;
            SystemEvents.SessionSwitch -= OnSession;
            SystemEvents.PowerModeChanged -= OnPower;
            presence.Changed -= OnPresence;
            cfg.Changed -= OnConfigChanged;
            try { c.Cancel(); } catch { }
            Wake();
            log.Add(LogKind.Info, "Guard stopped");
        }

        public void PauseFor(TimeSpan? duration)
        {
            paused = true;
            if (duration.HasValue && duration.Value > TimeSpan.Zero) Set(ref pauseTicks, DateTime.UtcNow + duration.Value);
            else Set(ref pauseTicks, null);
            log.Add(LogKind.Info, duration.HasValue ? "Paused for " + Dur(duration.Value) : "Paused until resumed");
            if (!sessionLocked) SetState(GuardState.Paused);
            Wake();
        }

        public void Resume()
        {
            if (!paused) return;
            ResumeInternal("Resumed");
            Wake();
        }

        public async Task CheckNowAsync()
        {
            try
            {
                CancellationToken ct = cts?.Token ?? CancellationToken.None;
                if (sessionLocked || suspended) return;
                if (!engine.ModelsLoaded) { log.Add(LogKind.Warning, "Check skipped: face models missing"); return; }
                if (!store.HasEnrollment) { log.Add(LogKind.Warning, "Check skipped: not enrolled"); return; }
                await RunCheckAsync(true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log.Add(LogKind.Error, "Manual check failed: " + ex.Message); }
        }

        // ------------------------------------------------------------ main loop
        async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var hold = Gate();
                    if (hold != null)
                    {
                        await WaitAsync(hold.Value, ct).ConfigureAwait(false);
                        Interlocked.Exchange(ref earlyFlag, 0);
                        continue;
                    }

                    var now = DateTime.UtcNow;
                    var due = Get(ref nextTicks);
                    if (due == null) { due = now + ComputeInterval(); Set(ref nextTicks, due); }

                    bool early = Interlocked.Exchange(ref earlyFlag, 0) == 1;
                    if (!early)
                    {
                        var wait = due.Value - now;
                        if (wait > TimeSpan.Zero)
                        {
                            SetIdle();
                            if (await WaitAsync(wait, ct).ConfigureAwait(false))
                            {
                                // woken: only an early-check request skips straight to a check; otherwise re-evaluate gates
                                if (Interlocked.Exchange(ref earlyFlag, 0) != 1) continue;
                            }
                            else if (ct.IsCancellationRequested) break;
                        }
                    }
                    await RunCheckAsync(false, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    log.Add(LogKind.Error, "Guard loop: " + ex.Message);
                    await WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Returns how long to hold off (checks must be skipped), or null when a check may proceed.</summary>
        TimeSpan? Gate()
        {
            var now = DateTime.UtcNow;
            if (sessionLocked) { SetState(GuardState.Locked); return Minute; }
            if (suspended) return Minute;

            if (paused)
            {
                var pu = Get(ref pauseTicks);
                if (pu != null && now >= pu) ResumeInternal("Pause ended");
                else
                {
                    SetState(GuardState.Paused);
                    return pu != null ? Cap(pu.Value - now + TimeSpan.FromMilliseconds(50), Minute) : Minute;
                }
            }
            if (!engine.ModelsLoaded)
            {
                problemText = "Face models missing - see README";
                SetState(GuardState.CameraProblem);
                return Minute;
            }
            if (!store.HasEnrollment)
            {
                Set(ref nextTicks, null);
                SetState(GuardState.NeedsEnrollment);
                return TimeSpan.FromSeconds(3);
            }
            if (!cfg.Current.Enabled)
            {
                Set(ref nextTicks, null);
                SetIdle();
                return Minute;
            }
            var g = Get(ref graceTicks);
            if (g != null && now < g && mismatchCount == 0) // a pending mismatch re-check ignores the grace window
            {
                SetIdle();
                return Cap(g.Value - now + TimeSpan.FromMilliseconds(50), Minute);
            }
            return null;
        }

        TimeSpan ComputeInterval()
        {
            var c = cfg.Current;
            double min = c.MinIntervalSec, max = Math.Max(min, c.MaxIntervalSec);
            // lower bound climbs from Min to Max with the OK streak; pick randomly between it and Max
            double lower = min + (max - min) * Math.Min(1.0, okStreak / 5.0);
            double s = lower + rng.NextDouble() * (max - lower);
            s *= 1 + (rng.NextDouble() * 0.3 - 0.15);
            if (c.SlowerOnBattery && Native.OnBattery()) s *= 1.5;
            if (Tier0(c)?.State == PresenceState.Present && okStreak >= 2) s = max;
            return TimeSpan.FromSeconds(Math.Max(5, s));
        }

        // ------------------------------------------------------------ one check
        async Task RunCheckAsync(bool manual, CancellationToken ct)
        {
            await checkLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (state is GuardState.Idle or GuardState.CameraProblem) SetState(GuardState.Checking);
                var c = cfg.Current;
                var (verdict, score, detail) = await EvaluateAsync(c, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (sessionLocked || suspended) return;
                var now = DateTime.UtcNow;
                switch (verdict)
                {
                    case Verdict.Match: OnMatch(score, manual, now); break;
                    case Verdict.Unavailable: OnUnavailable(detail, manual, now, c); break;
                    case Verdict.Miss: await OnMissAsync(detail, manual, now, c, ct).ConfigureAwait(false); break;
                    default: await OnMismatchAsync(score, manual, now, c, ct).ConfigureAwait(false); break;
                }
            }
            finally
            {
                Volatile.Write(ref lastCheckEndTicks, DateTime.UtcNow.Ticks);
                Interlocked.Exchange(ref earlyFlag, 0);
                checkLock.Release();
                if (state == GuardState.Checking) SetIdle();
            }
        }

        async Task<(Verdict, float, string)> EvaluateAsync(AppConfig c, CancellationToken ct)
        {
            int n = Math.Max(1, c.BurstFrames);
            if (okStreak >= 3) n = Math.Max(2, n / 2); // light check
            BurstResult r;
            try { r = await cam.CaptureBurstAsync(n, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastRawDetail = ex.Message; return (Verdict.Unavailable, 0, "IR camera error"); }

            lastRawDetail = r.Detail ?? "";
            if (r.Status != IrCameraStatus.Ok) return (Verdict.Unavailable, 0, Describe(r.Status));
            if (r.Frames.Count == 0) return (Verdict.Unavailable, 0, "IR camera returned no frames");

            System.Collections.Generic.IReadOnlyList<float[]> templates;
            try { templates = store.Load(); } catch { templates = Array.Empty<float[]>(); }
            if (templates.Count == 0) return (Verdict.Unavailable, 0, "Enrollment data unreadable - re-enroll");

            var means = new double[r.Frames.Count];
            double max = 0;
            for (int i = 0; i < means.Length; i++) { means[i] = Mean(r.Frames[i]); max = Math.Max(max, means[i]); }

            int usable = 0, faces = 0;
            float best = 0;
            for (int i = 0; i < means.Length; i++)
            {
                if (means[i] < 0.6 * max) continue; // IR emitter-off frame
                usable++;
                FaceResult fr;
                try { fr = engine.Analyze(r.Frames[i], templates); } catch { continue; }
                if (!fr.FaceFound || fr.Quality < MinQuality) continue;
                faces++;
                if (fr.Score > best) best = fr.Score;
                if (fr.Score >= c.MatchThreshold) return (Verdict.Match, fr.Score, "");
            }
            if (usable > 0 && faces > 0 && faces * 2 >= usable) return (Verdict.Mismatch, best, "");
            return (Verdict.Miss, best, faces == 0 ? "no usable face" : "face unclear");
        }

        // ------------------------------------------------------------ decisions
        void OnMatch(float score, bool manual, DateTime now)
        {
            missCount = mismatchCount = unavailCount = deferrals = 0;
            okStreak++;
            warned = false;
            Set(ref lastOkTicks, now);
            if (camFault) { camFault = false; log.Add(LogKind.Info, "Camera recovered"); }
            log.Add(LogKind.CheckOk, (manual ? "Manual check: " : "") + $"Verified (score {score:0.00})");
            if (!manual) Set(ref nextTicks, now + ComputeInterval());
        }

        void OnUnavailable(string detail, bool manual, DateTime now, AppConfig c)
        {
            if (manual) { log.Add(LogKind.Warning, $"Manual check: {detail}"); return; }
            unavailCount++;
            if (unavailCount >= 3)
            {
                camFault = true;
                problemText = detail;
                SetState(GuardState.CameraProblem);
                if (!warned)
                {
                    warned = true;
                    log.Add(LogKind.Warning, $"{detail}{(lastRawDetail.Length > 0 ? " (" + lastRawDetail + ")" : "")} - checks paused, retrying every 5 min");
                }
                Set(ref nextTicks, now.AddMinutes(5));
            }
            else Set(ref nextTicks, now.AddSeconds(c.RetryDelaySec));
        }

        async Task OnMissAsync(string detail, bool manual, DateTime now, AppConfig c, CancellationToken ct)
        {
            mismatchCount = 0;
            if (manual) { log.Add(LogKind.CheckMiss, $"Manual check: owner not seen ({detail})"); return; }
            if (c.TypingDefersMissLock && Native.IdleSeconds() < 5 && deferrals < 2)
            {
                deferrals++;
                log.Add(LogKind.CheckMiss, $"Not seen ({detail}) while typing - not counted, retrying");
                Set(ref nextTicks, now.AddSeconds(c.RetryDelaySec));
                return;
            }
            deferrals = 0;
            missCount++;
            bool absent = Tier0(c)?.State == PresenceState.Absent;
            int limit = absent ? Math.Min(1, c.RetriesBeforeLock) : c.RetriesBeforeLock;
            log.Add(LogKind.CheckMiss, $"Owner not seen ({detail}), miss {missCount}/{limit + 1}");
            if (missCount > limit) await LockAsync(LockReason.NoFaceRepeated, ct).ConfigureAwait(false);
            else Set(ref nextTicks, now.AddSeconds(c.RetryDelaySec));
        }

        async Task OnMismatchAsync(float best, bool manual, DateTime now, AppConfig c, CancellationToken ct)
        {
            log.Add(LogKind.CheckMismatch, (manual ? "Manual check: " : "") + $"Different face detected (best score {best:0.00})");
            if (manual) return;
            missCount = deferrals = 0;
            mismatchCount++;
            if (mismatchCount >= Math.Max(1, c.MismatchChecksBeforeLock)) await LockAsync(LockReason.UnknownFace, ct).ConfigureAwait(false);
            else Set(ref nextTicks, now.AddSeconds(2));
        }

        async Task LockAsync(LockReason reason, CancellationToken ct)
        {
            SetState(GuardState.Locked);
            log.Add(LogKind.Lock, "Locking PC: " + reason switch
            {
                LockReason.UnknownFace => "unknown face",
                LockReason.NoFaceRepeated => "owner not seen",
                _ => "manual",
            });
            var c = cfg.Current;
            if (c.ShowAnimation)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                try
                {
                    var play = anim.PlayAsync(reason, c.AnimationMs, linked.Token);
                    var done = await Task.WhenAny(play, Task.Delay(c.AnimationMs + 1500, ct)).ConfigureAwait(false);
                    if (done != play) linked.Cancel();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { log.Add(LogKind.Warning, "Animation failed: " + ex.Message); }
            }
            if (ct.IsCancellationRequested) return; // app is exiting: don't lock

            bool ok = Native.Lock();
            ResetCounters();
            Set(ref nextTicks, null);
            if (ok) sessionLocked = true; // cleared by the SessionUnlock event
            else
            {
                log.Add(LogKind.Error, "LockWorkStation failed");
                SetIdle();
                Set(ref nextTicks, DateTime.UtcNow.AddSeconds(10));
            }
        }

        // ------------------------------------------------------------ events
        void OnSession(object? sender, SessionSwitchEventArgs e)
        {
            try
            {
                if (e.Reason == SessionSwitchReason.SessionLock)
                {
                    sessionLocked = true;
                    ResetCounters();
                    SetState(GuardState.Locked);
                    Wake();
                }
                else if (e.Reason == SessionSwitchReason.SessionUnlock)
                {
                    sessionLocked = false;
                    StartGrace();
                    Wake();
                }
            }
            catch { }
        }

        void OnPower(object? sender, PowerModeChangedEventArgs e)
        {
            try
            {
                if (e.Mode == PowerModes.Suspend) { suspended = true; Wake(); }
                else if (e.Mode == PowerModes.Resume) { suspended = false; StartGrace(); Wake(); }
            }
            catch { }
        }

        void OnPresence(PresenceReading r)
        {
            try
            {
                var c = cfg.Current;
                if (!c.UseTier0 || !presence.Available) return;
                if (r.State != PresenceState.Absent && r.OnlookerPresent != true) return;
                var since = DateTime.UtcNow - new DateTime(Volatile.Read(ref lastCheckEndTicks), DateTimeKind.Utc);
                if (since.TotalSeconds < 8) return; // avoid camera flapping
                Interlocked.Exchange(ref earlyFlag, 1);
                Wake();
            }
            catch { }
        }

        void OnConfigChanged()
        {
            try
            {
                var n = Get(ref nextTicks);
                var cap = DateTime.UtcNow.AddSeconds(cfg.Current.MaxIntervalSec * 1.6 + 5);
                if (n != null && n > cap) Set(ref nextTicks, cap);
            }
            catch { }
            Wake();
        }

        // ------------------------------------------------------------ helpers
        void StartGrace()
        {
            var g = DateTime.UtcNow.AddSeconds(cfg.Current.GraceAfterUnlockSec);
            Set(ref graceTicks, g);
            Set(ref nextTicks, g.AddSeconds(3));
            ResetCounters();
        }

        void ResumeInternal(string why)
        {
            paused = false;
            Set(ref pauseTicks, null);
            Set(ref nextTicks, DateTime.UtcNow.AddSeconds(5));
            ResetCounters();
            log.Add(LogKind.Info, why);
            if (state == GuardState.Paused) SetIdle();
        }

        void ResetCounters() => okStreak = missCount = mismatchCount = unavailCount = deferrals = 0;

        PresenceReading? Tier0(AppConfig c)
        {
            try { return c.UseTier0 && presence.Available ? presence.Current : null; }
            catch { return null; }
        }

        void SetIdle() => SetState(camFault ? GuardState.CameraProblem : GuardState.Idle);

        void SetState(GuardState s)
        {
            lock (gate) { if (state == s) return; state = s; }
            try { StateChanged?.Invoke(s); } catch { }
        }

        void Wake() { try { wake.Release(); } catch (SemaphoreFullException) { } }

        async Task<bool> WaitAsync(TimeSpan t, CancellationToken ct)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            try
            {
                bool signaled = await wake.WaitAsync(t, ct).ConfigureAwait(false);
                if (signaled) while (wake.Wait(0)) { }
                return signaled;
            }
            catch (OperationCanceledException) { return false; }
        }

        static TimeSpan Cap(TimeSpan t, TimeSpan max) => t > max ? max : t;

        static DateTime? Ticks(long t) => t == 0 ? null : new DateTime(t, DateTimeKind.Utc);
        static DateTime? Get(ref long f) => Ticks(Volatile.Read(ref f));
        static void Set(ref long f, DateTime? v) => Volatile.Write(ref f, v?.Ticks ?? 0);

        static double Mean(IrFrame f)
        {
            var g = f.Gray8;
            if (g.Length == 0) return 0;
            long sum = 0; int n = 0;
            for (int i = 0; i < g.Length; i += 7) { sum += g[i]; n++; }
            return (double)sum / n;
        }

        static string Describe(IrCameraStatus s) => s switch
        {
            IrCameraStatus.NotFound => "IR camera not found",
            IrCameraStatus.AccessDenied => "IR camera blocked - check Windows camera privacy",
            IrCameraStatus.Busy => "IR camera busy (used by another app)",
            _ => "IR camera error",
        };

        static string Dur(TimeSpan t)
        {
            int s = (int)Math.Round(Math.Max(0, t.TotalSeconds));
            return s < 90 ? s + "s" : s < 5400 ? s / 60 + "m" : s / 3600 + "h";
        }

        string PauseText()
        {
            var p = Get(ref pauseTicks);
            return p != null ? "Paused until " + p.Value.ToLocalTime().ToString("HH:mm") : "Paused until resumed";
        }

        string CamText()
        {
            var n = Get(ref nextTicks);
            return camFault && n != null ? $"{problemText} - retry in ~{Dur(n.Value - DateTime.UtcNow)}" : problemText;
        }

        string IdleText()
        {
            if (!cfg.Current.Enabled) return "Disabled in settings";
            var now = DateTime.UtcNow;
            var g = Get(ref graceTicks);
            if (g != null && now < g) return "Just unlocked - checks resume in ~" + Dur(g.Value - now);
            if (suspended) return "System asleep";
            var ok = Get(ref lastOkTicks);
            var next = Get(ref nextTicks);
            string head = ok != null ? $"Verified {Dur(now - ok.Value)} ago" : "Not verified yet";
            return next != null ? $"{head} - next check in ~{Dur(next.Value - now)}" : head;
        }
    }
}
