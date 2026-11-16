using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PresenceGuard
{
    public sealed class ActivityLog : IActivityLog
    {
        const int RingSize = 200;
        const int FileKeep = 500;

        readonly object gate = new();
        readonly Queue<LogEntry> ring = new();
        readonly string dir;
        readonly string path;
        int fileLines;

        public event Action<LogEntry>? Added;

        public ActivityLog()
        {
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Argus");
            path = Path.Combine(dir, "activity.log");
            try
            {
                Directory.CreateDirectory(dir);
                TrimFile(true);
            }
            catch { }
        }

        public void Add(LogKind kind, string message)
        {
            var e = new LogEntry(DateTime.Now, kind, message ?? "");
            lock (gate)
            {
                ring.Enqueue(e);
                while (ring.Count > RingSize) ring.Dequeue();
                try
                {
                    File.AppendAllText(path, Format(e) + Environment.NewLine);
                    if (++fileLines > FileKeep + 100) TrimFile(false);
                }
                catch { }
            }
            try { Added?.Invoke(e); } catch { }
        }

        public IReadOnlyList<LogEntry> Recent(int n)
        {
            LogEntry[] all;
            lock (gate) all = ring.ToArray();
            var res = new List<LogEntry>(Math.Min(Math.Max(n, 0), all.Length));
            for (int i = all.Length - 1; i >= 0 && res.Count < n; i--) res.Add(all[i]);
            return res;
        }

        static string Format(LogEntry e) =>
            e.Local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\t" + e.Kind + "\t" +
            e.Message.Replace('\r', ' ').Replace('\n', ' ');

        // Keeps the last FileKeep lines; at startup also preloads the in-memory ring.
        void TrimFile(bool preload)
        {
            if (!File.Exists(path)) { fileLines = 0; return; }
            var lines = File.ReadAllLines(path);
            if (lines.Length > FileKeep)
            {
                var keep = new string[FileKeep];
                Array.Copy(lines, lines.Length - FileKeep, keep, 0, FileKeep);
                lines = keep;
                File.WriteAllLines(path, lines);
            }
            fileLines = lines.Length;
            if (!preload) return;
            for (int i = Math.Max(0, lines.Length - RingSize); i < lines.Length; i++)
            {
                var p = lines[i].Split('\t', 3);
                if (p.Length == 3 &&
                    DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) &&
                    Enum.TryParse<LogKind>(p[1], out var k))
                    ring.Enqueue(new LogEntry(t, k, p[2]));
            }
        }
    }
}
