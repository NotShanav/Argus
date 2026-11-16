using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace PresenceGuard
{
    public sealed class JsonConfigStore : IConfigStore
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

        readonly string dir;
        readonly string path;
        readonly object gate = new();
        AppConfig cur = new();

        public AppConfig Current => cur;
        public event Action? Changed;

        public JsonConfigStore()
        {
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Argus");
            path = Path.Combine(dir, "config.json");
            try
            {
                if (File.Exists(path))
                    cur = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
            }
            catch { cur = new AppConfig(); }
            Clamp(cur);
            ApplyStartup(cur.StartWithWindows);
        }

        public void Save(AppConfig cfg)
        {
            if (cfg == null) return;
            lock (gate)
            {
                Clamp(cfg);
                cur = cfg;
                try
                {
                    Directory.CreateDirectory(dir);
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Opts));
                    File.Move(tmp, path, true);
                }
                catch { /* config stays in memory */ }
                ApplyStartup(cfg.StartWithWindows);
            }
            try { Changed?.Invoke(); } catch { }
        }

        static void Clamp(AppConfig c)
        {
            int min = Math.Clamp(c.MinIntervalSec, 5, 3600);
            int max = Math.Clamp(c.MaxIntervalSec, 5, 3600);
            if (min > max) (min, max) = (max, min);
            c.MinIntervalSec = min;
            c.MaxIntervalSec = max;
            c.BurstFrames = Math.Clamp(c.BurstFrames, 1, 10);
            c.MatchThreshold = float.IsNaN(c.MatchThreshold) ? 0.363f : Math.Clamp(c.MatchThreshold, 0.2f, 0.7f);
            c.RetriesBeforeLock = Math.Clamp(c.RetriesBeforeLock, 0, 10);
            c.RetryDelaySec = Math.Clamp(c.RetryDelaySec, 1, 60);
            c.MismatchChecksBeforeLock = Math.Clamp(c.MismatchChecksBeforeLock, 1, 5);
            c.GraceAfterUnlockSec = Math.Clamp(c.GraceAfterUnlockSec, 0, 600);
            c.AnimationMs = Math.Clamp(c.AnimationMs, 300, 5000);
        }

        static void ApplyStartup(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
                if (key == null) return;
                if (enable)
                {
                    var exe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exe)) key.SetValue("Argus", "\"" + exe + "\"");
                }
                else if (key.GetValue("Argus") != null)
                {
                    key.DeleteValue("Argus", false);
                }
            }
            catch { }
        }
    }
}
