using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PresenceGuard
{
    public sealed class EnrollmentService : IEnrollmentService
    {
        private static readonly string[] Hints =
        {
            "Look straight at the screen", "Turn slightly left, then right", "Tilt up a little, then down"
        };
        private readonly IIrCamera _cam;
        private readonly IFaceEngine _engine;
        private readonly IEnrollmentStore _store;

        public EnrollmentService(IIrCamera cam, IFaceEngine engine, IEnrollmentStore store)
        { _cam = cam; _engine = engine; _store = store; }

        private static float[] Mean(List<float[]> s)
        {
            var m = new float[s[0].Length];
            foreach (var v in s) for (int i = 0; i < m.Length; i++) m[i] += v[i];
            return m;
        }

        public async Task<bool> EnrollAsync(int targetSamples, IProgress<EnrollProgress> progress, CancellationToken ct)
        {
            try
            {
                int target = Math.Clamp(targetSamples, 3, 30);
                var samples = new List<float[]>();
                var sw = Stopwatch.StartNew();
                long lastTry = -1000, lastAccept = -1000, lastPrev = -1000;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(40));

                await foreach (var f in _cam.StreamAsync(cts.Token))
                {
                    long now = sw.ElapsedMilliseconds;
                    string hint = Hints[Math.Min(2, samples.Count * 3 / target)];
                    if (now - lastTry >= 250)
                    {
                        lastTry = now;
                        var emb = await Task.Run(() => _engine.ExtractEmbedding(f), cts.Token);
                        bool ok = emb != null && now - lastAccept >= 150;
                        if (ok && samples.Count >= 3 && FaceEngine.Cosine(emb!, Mean(samples)) < 0.5f) ok = false;
                        if (ok)
                        {
                            samples.Add(emb!);
                            lastAccept = now; lastPrev = now;
                            hint = Hints[Math.Min(2, samples.Count * 3 / target)];
                            progress.Report(new EnrollProgress(samples.Count, target, hint, f));
                            if (samples.Count >= target)
                            {
                                _store.Save(samples);
                                return true;
                            }
                            continue;
                        }
                    }
                    if (now - lastPrev >= 250)
                    {
                        lastPrev = now;
                        progress.Report(new EnrollProgress(samples.Count, target, hint, f));
                    }
                }
                return false;
            }
            catch { return false; }
        }
    }
}
