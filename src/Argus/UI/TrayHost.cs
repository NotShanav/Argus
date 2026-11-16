using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace PresenceGuard
{
    public sealed class TrayHost : IDisposable
    {
        private readonly IGuardController _guard;
        private readonly IConfigStore _cfg;
        private readonly IActivityLog _log;
        private readonly IEnrollmentService _enroll;
        private readonly IEnrollmentStore _store;
        private readonly Action _exit;

        private WF.NotifyIcon? _ni;
        private Icon? _icon;
        private IntPtr _hIcon;
        private GuardState? _shown;
        private WF.ToolStripMenuItem _status = null!, _pause = null!, _resume = null!;
        private DispatcherTimer? _timer;
        private SettingsWindow? _win;
        private bool _disposed;

        private static readonly Color Fg = Color.FromArgb(0xE6, 0xE8, 0xEB);
        private static readonly Color Bg = Color.FromArgb(0x18, 0x1B, 0x21);

        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

        public TrayHost(IGuardController guard, IConfigStore cfg, IActivityLog log,
                        IEnrollmentService enroll, IEnrollmentStore store, Action exitApp)
        {
            _guard = guard; _cfg = cfg; _log = log; _enroll = enroll; _store = store; _exit = exitApp;
        }

        public void Show() => Theme.UI(ShowCore);

        private void ShowCore()
        {
            if (_ni != null || _disposed) return;
            _ni = new WF.NotifyIcon { Visible = false, Text = "Argus" };
            ApplyIcon(_guard.State);
            _ni.ContextMenuStrip = BuildMenu();
            _ni.MouseUp += (_, e) => { if (e.Button == WF.MouseButtons.Left) Safe(() => Win().TogglePopup()); };
            _ni.Visible = true;
            _guard.StateChanged += OnState;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();
            Refresh();
            if (!_store.HasEnrollment)
            {
                _ni.ShowBalloonTip(4000, "Argus", "Enroll your face to start", WF.ToolTipIcon.Info);
                Win().OpenPopup();
            }
        }

        private SettingsWindow Win() => _win ??= new SettingsWindow(_guard, _cfg, _log, _enroll, _store);

        private static void Safe(Action a) { try { a(); } catch { } }

        // ---------------- menu ----------------
        private WF.ToolStripMenuItem Item(string text, Action act)
        {
            var i = new WF.ToolStripMenuItem(text) { ForeColor = Fg, BackColor = Bg };
            i.Click += (_, _) => Safe(act);
            return i;
        }

        private WF.ContextMenuStrip BuildMenu()
        {
            var m = new WF.ContextMenuStrip { ForeColor = Fg, BackColor = Bg, Renderer = new WF.ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false } };
            _status = new WF.ToolStripMenuItem("Argus") { Enabled = false };
            _pause = new WF.ToolStripMenuItem("Pause") { ForeColor = Fg, BackColor = Bg };
            _pause.DropDownItems.Add(Item("15 minutes", () => _guard.PauseFor(TimeSpan.FromMinutes(15))));
            _pause.DropDownItems.Add(Item("1 hour", () => _guard.PauseFor(TimeSpan.FromHours(1))));
            _pause.DropDownItems.Add(Item("Until I resume", () => _guard.PauseFor(null)));
            _resume = Item("Resume", () => _guard.Resume());
            m.Items.Add(_status);
            m.Items.Add(Item("Open Argus", () => Win().OpenPopup()));
            m.Items.Add(Item("Check now", async () => { try { await _guard.CheckNowAsync(); } catch { } }));
            m.Items.Add(_pause);
            m.Items.Add(_resume);
            m.Items.Add(new WF.ToolStripSeparator());
            m.Items.Add(Item("Exit", _exit));
            m.Opening += (_, _) => Safe(() =>
            {
                _status.Text = _guard.StatusText;
                bool paused = _guard.State == GuardState.Paused;
                _resume.Visible = paused; _pause.Visible = !paused;
            });
            return m;
        }

        private sealed class DarkColors : WF.ProfessionalColorTable
        {
            private static readonly Color Hi = Color.FromArgb(0x2A, 0x2F, 0x38);
            public override Color ToolStripDropDownBackground => Bg;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
            public override Color MenuBorder => Hi;
            public override Color MenuItemBorder => Hi;
            public override Color MenuItemSelected => Hi;
            public override Color MenuItemSelectedGradientBegin => Hi;
            public override Color MenuItemSelectedGradientEnd => Hi;
            public override Color MenuItemPressedGradientBegin => Hi;
            public override Color MenuItemPressedGradientMiddle => Hi;
            public override Color MenuItemPressedGradientEnd => Hi;
            public override Color SeparatorDark => Hi;
            public override Color SeparatorLight => Hi;
        }

        // ---------------- state / icon ----------------
        private void OnState(GuardState s) => Theme.UI(Refresh);

        private void Refresh()
        {
            try
            {
                if (_ni == null || _disposed) return;
                var st = _guard.State;
                if (_shown != st) ApplyIcon(st);
                _ni.Text = Cut(_guard.StatusText);
            }
            catch { }
        }

        private static string Cut(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Argus";
            return s.Length <= 63 ? s : s.Substring(0, 60) + "...";
        }

        private void ApplyIcon(GuardState st)
        {
            if (_ni == null) return;
            var h = Draw(st, out var ic);
            var oldIcon = _icon; var oldH = _hIcon;
            _icon = ic; _hIcon = h; _shown = st;
            _ni.Icon = ic;
            oldIcon?.Dispose();
            if (oldH != IntPtr.Zero) DestroyIcon(oldH);
        }

        private static Bitmap? _base;
        private static Bitmap? Base()
        {
            if (_base != null) return _base;
            try
            {
                using var st = typeof(TrayHost).Assembly.GetManifestResourceStream("Argus.tray.png");
                if (st != null) { using var tmp = new Bitmap(st); _base = new Bitmap(tmp); }
            }
            catch { }
            return _base;
        }

        private static IntPtr Draw(GuardState st, out Icon icon)
        {
            int s = Math.Max(16, WF.SystemInformation.SmallIconSize.Width);
            using var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                var b = Base();
                if (b != null) g.DrawImage(b, new Rectangle(0, 0, s, s));
                else using (var f = new SolidBrush(Color.FromArgb(0x18, 0x1B, 0x21))) g.FillEllipse(f, 1, 1, s - 2, s - 2);
                // status dot, bottom-right
                float d = s * 0.46f, x = s - d, y = s - d;
                using var ring = new SolidBrush(Color.FromArgb(0xFF, 0x0F, 0x11, 0x15));
                g.FillEllipse(ring, x - 1f, y - 1f, d + 2f, d + 2f);
                using var dot = new SolidBrush(ColorTranslator.FromHtml(Theme.StateHex(st)));
                g.FillEllipse(dot, x + 0.5f, y + 0.5f, d - 1f, d - 1f);
            }
            IntPtr h = bmp.GetHicon();
            icon = Icon.FromHandle(h);
            return h;
        }

        // ---------------- dispose ----------------
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                var d = System.Windows.Application.Current?.Dispatcher;
                if (d == null || d.CheckAccess()) DisposeCore(); else d.Invoke(new Action(DisposeCore));
            }
            catch { }
        }

        private void DisposeCore()
        {
            Safe(() => _guard.StateChanged -= OnState);
            Safe(() => { _timer?.Stop(); _timer = null; });
            Safe(() => { _win?.Shutdown(); _win = null; });
            Safe(() =>
            {
                if (_ni != null) { _ni.Visible = false; _ni.ContextMenuStrip?.Dispose(); _ni.Dispose(); _ni = null; }
                _icon?.Dispose(); _icon = null;
                if (_hIcon != IntPtr.Zero) { DestroyIcon(_hIcon); _hIcon = IntPtr.Zero; }
            });
        }
    }
}
