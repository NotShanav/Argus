// Contracts.cs  (PresenceGuard shared contract - DO NOT CHANGE)
// Single namespace for the whole app. Events may fire on ANY thread (UI must marshal to its Dispatcher).
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PresenceGuard
{
    // ---------------- Camera (IR ONLY - the RGB camera must never be opened) ----------------
    public sealed record IrFrame(int Width, int Height, byte[] Gray8, DateTime Utc); // Gray8 = Width*Height bytes, row-major
    public enum IrCameraStatus { Ok, NotFound, AccessDenied, Busy, Error }
    public sealed record BurstResult(IrCameraStatus Status, IReadOnlyList<IrFrame> Frames, string? Detail);

    public interface IIrCamera
    {
        // Opens the IR stream, collects up to `count` frames or until `timeout`, then CLOSES the camera. Never throws.
        Task<BurstResult> CaptureBurstAsync(int count, TimeSpan timeout, CancellationToken ct);
        // Keeps the IR stream open while being enumerated (enrollment preview). Closes on cancel/dispose. Never throws; ends silently on error.
        IAsyncEnumerable<IrFrame> StreamAsync(CancellationToken ct);
    }

    // ---------------- Face engine ----------------
    public sealed record FaceResult(bool FaceFound, int FaceCount, float Score, float Quality); // Score: cosine similarity 0..1 vs templates (0 if no face); Quality 0..1
    public interface IFaceEngine
    {
        bool ModelsLoaded { get; }
        string? LoadError { get; }
        FaceResult Analyze(IrFrame frame, IReadOnlyList<float[]> templates); // largest face only; never throws
        float[]? ExtractEmbedding(IrFrame frame);                            // null if no face or Quality < 0.35; never throws
    }

    // ---------------- Enrollment ----------------
    public interface IEnrollmentStore
    {
        bool HasEnrollment { get; }
        IReadOnlyList<float[]> Load();
        void Save(IReadOnlyList<float[]> templates);
        void Clear();
    }
    public sealed record EnrollProgress(int Collected, int Target, string Hint, IrFrame? Preview);
    public interface IEnrollmentService
    {
        // Streams IR frames, collects `targetSamples` good embeddings, saves them. Returns false on cancel/failure. Never throws.
        Task<bool> EnrollAsync(int targetSamples, IProgress<EnrollProgress> progress, CancellationToken ct);
    }

    // ---------------- Tier 0: Windows presence sensor ----------------
    public enum PresenceState { Unknown, Present, Absent }
    public sealed record PresenceReading(PresenceState State, bool? OnlookerPresent);
    public interface IPresenceSensor : IDisposable
    {
        bool Available { get; }              // false if no sensor / API unavailable
        PresenceReading Current { get; }
        event Action<PresenceReading>? Changed;
    }

    // ---------------- Config ----------------
    public sealed class AppConfig
    {
        public bool Enabled { get; set; } = true;
        public int MinIntervalSec { get; set; } = 20;
        public int MaxIntervalSec { get; set; } = 90;
        public int BurstFrames { get; set; } = 4;
        public float MatchThreshold { get; set; } = 0.363f;   // cosine; higher = stricter
        public int RetriesBeforeLock { get; set; } = 3;        // consecutive "no face / bad scan" before lock
        public int RetryDelaySec { get; set; } = 4;
        public int MismatchChecksBeforeLock { get; set; } = 2; // consecutive "clearly a different face"
        public int GraceAfterUnlockSec { get; set; } = 45;
        public bool UseTier0 { get; set; } = true;
        public bool TypingDefersMissLock { get; set; } = true;
        public bool SlowerOnBattery { get; set; } = true;
        public bool ShowAnimation { get; set; } = true;
        public int AnimationMs { get; set; } = 1300;
        public bool StartWithWindows { get; set; } = false;
    }
    public interface IConfigStore
    {
        AppConfig Current { get; }
        void Save(AppConfig cfg);   // persists + raises Changed
        event Action? Changed;
    }

    // ---------------- Activity log (text only, never images) ----------------
    public enum LogKind { Info, CheckOk, CheckMiss, CheckMismatch, Lock, Warning, Error }
    public sealed record LogEntry(DateTime Local, LogKind Kind, string Message);
    public interface IActivityLog
    {
        void Add(LogKind kind, string message);
        IReadOnlyList<LogEntry> Recent(int n);   // newest first
        event Action<LogEntry>? Added;
    }

    // ---------------- Lock animation (plays on the normal desktop BEFORE the PC locks) ----------------
    public enum LockReason { UnknownFace, NoFaceRepeated, Manual }
    public interface ILockAnimation
    {
        Task PlayAsync(LockReason reason, int durationMs, CancellationToken ct); // completes when animation is fully closed; never throws
    }

    // ---------------- Guard controller (the brain) ----------------
    public enum GuardState { Starting, Idle, Checking, Paused, Locked, NeedsEnrollment, CameraProblem }
    public interface IGuardController
    {
        GuardState State { get; }
        string StatusText { get; }            // short human text, e.g. "Verified 12s ago - next check in ~40s"
        DateTime? NextCheckLocal { get; }
        event Action<GuardState>? StateChanged;
        void Start();
        void Stop();
        void PauseFor(TimeSpan? duration);    // null = until Resume()
        void Resume();
        Task CheckNowAsync();                 // runs one full check immediately (same decision rules)
    }
}
