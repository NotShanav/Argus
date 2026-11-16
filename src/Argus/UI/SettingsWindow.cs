using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PresenceGuard
{
    internal sealed class SettingsWindow : Window
    {
        private readonly IGuardController _guard;
        private readonly IConfigStore _cfg;
        private readonly IActivityLog _log;
        private readonly IEnrollmentService _enroll;
        private readonly IEnrollmentStore _store;
        private readonly List<Action> _refresh = new();
        private readonly DispatcherTimer _debounce, _timer;
        private readonly TranslateTransform _slide = new();
        private readonly Ellipse _dot = new() { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _enrollText = new() { FontSize = 13, FontWeight = FontWeights.SemiBold };
        private readonly StackPanel _logPanel = new();
        private Button _checkBtn = null!, _previewBtn = null!;
        private EnrollmentWindow? _enrollWin;
        private AppConfig _c;
        private bool _loading, _dirty, _suppress, _hiding;
        private DateTime _lastHide = DateTime.MinValue;

        public SettingsWindow(IGuardController guard, IConfigStore cfg, IActivityLog log, IEnrollmentService enroll, IEnrollmentStore store)
        {
            _guard = guard; _cfg = cfg; _log = log; _enroll = enroll; _store = store;
            _c = Clone(cfg.Current);
            Resources = Theme.Styles();
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
            Width = 400; SizeToContent = SizeToContent.Height; MaxHeight = 660;
            FontFamily = Theme.Font; FontSize = 12.5; Foreground = Theme.Br("#E6E8EB"); UseLayoutRounding = true;
            Content = BuildUi();

            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _debounce.Tick += (_, _) => { _debounce.Stop(); Flush(); };
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => UpdateStatus();

            Deactivated += (_, _) => { if (!_suppress) HideAnimated(); };
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) HideAnimated(); };
            SizeChanged += (_, _) => { if (IsVisible) Place(); };
            _guard.StateChanged += OnState; _log.Added += OnLog; _cfg.Changed += OnCfg;
            Reload();
        }

        // ---------------- public API ----------------
        public void TogglePopup()
        {
            if ((DateTime.UtcNow - _lastHide).TotalMilliseconds < 300) return; // the click that deactivated us
            if (IsVisible && !_hiding) HideAnimated(); else OpenPopup();
        }

        public void OpenPopup()
        {
            try
            {
                if (IsVisible && !_hiding) { Activate(); return; }
                if (!_dirty) { _c = Clone(_cfg.Current); Reload(); }
                RefreshEnroll(); RebuildLog(); UpdateStatus();
                _hiding = false; _timer.Start();
                BeginAnimation(OpacityProperty, null);
                if (!IsVisible) Show();
                UpdateLayout(); Place(); Activate();
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
                _slide.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            catch { }
        }

        public void Shutdown()
        {
            try
            {
                _guard.StateChanged -= OnState; _log.Added -= OnLog; _cfg.Changed -= OnCfg;
                _timer.Stop(); _debounce.Stop();
                if (_dirty) Flush();
                _enrollWin?.Close();
                _suppress = false;
                Close();
            }
            catch { }
        }

        private void HideAnimated()
        {
            if (_hiding || !IsVisible) return;
            _hiding = true; _lastHide = DateTime.UtcNow; _timer.Stop();
            if (_dirty) { _debounce.Stop(); Flush(); }
            var a = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(110));
            a.Completed += (_, _) => { if (_hiding) { _hiding = false; Hide(); } };
            BeginAnimation(OpacityProperty, a);
        }

        private void Place()
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 4;
            Top = wa.Bottom - ActualHeight - 4;
        }

        // ---------------- events ----------------
        private void OnState(GuardState s) => Theme.UI(UpdateStatus);
        private void OnLog(LogEntry e) => Theme.UI(() => { if (IsVisible) RebuildLog(); });
        private void OnCfg() => Theme.UI(() => { if (!_dirty) { _c = Clone(_cfg.Current); Reload(); } });

        private void UpdateStatus()
        {
            try { _status.Text = _guard.StatusText; _dot.Fill = Theme.Br(Theme.StateHex(_guard.State)); } catch { }
        }

        private void RefreshEnroll()
        {
            bool e = false; try { e = _store.HasEnrollment; } catch { }
            _enrollText.Text = e ? "Enrolled" : "Not enrolled";
            _enrollText.Foreground = Theme.Br(e ? "#34D399" : "#8B93A1");
        }

        private static string KindHex(LogKind k) => k switch
        {
            LogKind.CheckOk => "#34D399",
            LogKind.CheckMiss => "#FBBF24",
            LogKind.CheckMismatch => "#FB923C",
            LogKind.Lock => "#FF4D4D",
            LogKind.Warning => "#FBBF24",
            LogKind.Error => "#EF4444",
            _ => "#B4BAC5"
        };

        private void RebuildLog()
        {
            try
            {
                _logPanel.Children.Clear();
                IReadOnlyList<LogEntry> items;
                try { items = _log.Recent(8); } catch { items = Array.Empty<LogEntry>(); }
                if (items.Count == 0) { _logPanel.Children.Add(Tb("No activity yet", "#8B93A1")); return; }
                foreach (var e in items)
                {
                    var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var t = Tb(e.Local.ToString("HH:mm:ss"), "#8B93A1", 11); t.FontFamily = new FontFamily("Consolas");
                    var m = Tb(e.Message, KindHex(e.Kind), 12); Grid.SetColumn(m, 1);
                    g.Children.Add(t); g.Children.Add(m);
                    _logPanel.Children.Add(g);
                }
            }
            catch { }
        }

        // ---------------- config plumbing ----------------
        private static AppConfig Clone(AppConfig c) => new()
        {
            Enabled = c.Enabled, MinIntervalSec = c.MinIntervalSec, MaxIntervalSec = c.MaxIntervalSec,
            BurstFrames = c.BurstFrames, MatchThreshold = c.MatchThreshold, RetriesBeforeLock = c.RetriesBeforeLock,
            RetryDelaySec = c.RetryDelaySec, MismatchChecksBeforeLock = c.MismatchChecksBeforeLock,
            GraceAfterUnlockSec = c.GraceAfterUnlockSec, UseTier0 = c.UseTier0, TypingDefersMissLock = c.TypingDefersMissLock,
            SlowerOnBattery = c.SlowerOnBattery, ShowAnimation = c.ShowAnimation, AnimationMs = c.AnimationMs,
            StartWithWindows = c.StartWithWindows
        };

        private void Touch() { _dirty = true; _debounce.Stop(); _debounce.Start(); }
        private void Flush() { try { _dirty = false; _cfg.Save(Clone(_c)); } catch { } }
        private void Reload()
        {
            _loading = true;
            try { foreach (var r in _refresh) r(); } catch { } finally { _loading = false; }
        }

        // ---------------- UI building ----------------
        private static TextBlock Tb(string t, string hex = "#E6E8EB", double size = 12.5) =>
            new() { Text = t, Foreground = Theme.Br(hex), FontSize = size, TextWrapping = TextWrapping.Wrap };

        private Button Btn(string t, string style) =>
            new() { Content = t, Style = (Style)Resources[style], Padding = new Thickness(12, 6, 12, 6) };

        private CheckBox Sw(string text, Func<bool> get, Action<bool> set)
        {
            var cb = new CheckBox { Content = text, Style = (Style)Resources["Toggle"] };
            cb.Click += (_, _) => { if (_loading) return; set(cb.IsChecked == true); Touch(); };
            _refresh.Add(() => cb.IsChecked = get());
            return cb;
        }

        private FrameworkElement SliderRow(string name, double min, double max, double step, Func<double> get,
            Action<double> set, Func<double, string> fmt, string? lo = null, string? hi = null)
        {
            var g = new Grid();
            for (int i = 0; i < 3; i++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var val = Tb("", "#22D3EE"); val.HorizontalAlignment = HorizontalAlignment.Right;
            var sl = new Slider
            {
                Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true,
                Style = (Style)Resources["Slim"], Margin = new Thickness(0, 6, 0, 0)
            };
            Grid.SetRow(sl, 1);
            g.Children.Add(Tb(name)); g.Children.Add(val); g.Children.Add(sl);
            if (lo != null)
            {
                var l = Tb(lo, "#8B93A1", 10.5); var h = Tb(hi ?? "", "#8B93A1", 10.5);
                h.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetRow(l, 2); Grid.SetRow(h, 2); g.Children.Add(l); g.Children.Add(h);
            }
            sl.ValueChanged += (_, _) => { val.Text = fmt(sl.Value); if (_loading) return; set(sl.Value); Touch(); };
            _refresh.Add(() => { sl.Value = get(); val.Text = fmt(sl.Value); });
            return g;
        }

        private static Border Card(string title, params FrameworkElement[] items)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Br("#8B93A1") });
            foreach (var i in items)
            {
                i.Margin = new Thickness(i.Margin.Left, i.Margin.Top + 11, i.Margin.Right, i.Margin.Bottom);
                sp.Children.Add(i);
            }
            return new Border { Background = Theme.Br("#181B21"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 14), Margin = new Thickness(0, 0, 0, 10), Child = sp };
        }

        private UIElement BuildUi()
        {
            _checkBtn = Btn("Check now", "Accent");
            _checkBtn.Click += async (_, _) =>
            {
                _checkBtn.IsEnabled = false; _checkBtn.Content = "Checking...";
                try { await _guard.CheckNowAsync(); } catch { }
                finally { _checkBtn.Content = "Check now"; _checkBtn.IsEnabled = true; UpdateStatus(); }
            };
            var head = new DockPanel { Margin = new Thickness(16, 14, 16, 10), LastChildFill = true };
            DockPanel.SetDock(_dot, Dock.Left); DockPanel.SetDock(_checkBtn, Dock.Right);
            var logo = Theme.Logo();
            if (logo != null)
            {
                var img = new Image { Source = logo, Width = 26, Height = 26, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                DockPanel.SetDock(img, Dock.Left); head.Children.Add(img);
            }
            head.Children.Add(_dot); head.Children.Add(_checkBtn); head.Children.Add(_status);

            var body = new StackPanel { Margin = new Thickness(14, 2, 14, 4) };

            body.Children.Add(Card("General",
                Sw("Enabled", () => _c.Enabled, v => _c.Enabled = v),
                Sw("Start with Windows", () => _c.StartWithWindows, v => _c.StartWithWindows = v)));

            body.Children.Add(Card("Checking",
                SliderRow("Min interval", 5, 300, 1, () => _c.MinIntervalSec, v =>
                {
                    _c.MinIntervalSec = (int)v;
                    if (_c.MaxIntervalSec < _c.MinIntervalSec) _c.MaxIntervalSec = _c.MinIntervalSec;
                    Reload();
                }, v => $"{v:0} s"),
                SliderRow("Max interval", 5, 300, 1, () => _c.MaxIntervalSec, v =>
                {
                    _c.MaxIntervalSec = (int)v;
                    if (_c.MinIntervalSec > _c.MaxIntervalSec) _c.MinIntervalSec = _c.MaxIntervalSec;
                    Reload();
                }, v => $"{v:0} s"),
                Sw("Use presence sensor (Tier 0)", () => _c.UseTier0, v => _c.UseTier0 = v),
                Sw("Slower on battery", () => _c.SlowerOnBattery, v => _c.SlowerOnBattery = v)));

            body.Children.Add(Card("Smart failure handling",
                SliderRow("Match strictness", 0.25, 0.55, 0.01, () => _c.MatchThreshold,
                    v => _c.MatchThreshold = (float)Math.Round(v, 2), v => v.ToString("0.00"), "Relaxed", "Strict"),
                SliderRow("Retries before lock", 1, 6, 1, () => _c.RetriesBeforeLock, v => _c.RetriesBeforeLock = (int)v, v => $"{v:0}"),
                SliderRow("Retry delay", 2, 15, 1, () => _c.RetryDelaySec, v => _c.RetryDelaySec = (int)v, v => $"{v:0} s"),
                SliderRow("Mismatch checks before lock", 1, 3, 1, () => _c.MismatchChecksBeforeLock, v => _c.MismatchChecksBeforeLock = (int)v, v => $"{v:0}"),
                SliderRow("Grace after unlock", 0, 180, 5, () => _c.GraceAfterUnlockSec, v => _c.GraceAfterUnlockSec = (int)v, v => $"{v:0} s"),
                Sw("Typing delays no-face lock", () => _c.TypingDefersMissLock, v => _c.TypingDefersMissLock = v)));

            _previewBtn = Btn("Preview", "Flat");
            _previewBtn.HorizontalAlignment = HorizontalAlignment.Left;
            _previewBtn.Click += async (_, _) =>
            {
                _suppress = true; _previewBtn.IsEnabled = false;
                try { await new SealOverlay().PlayAsync(LockReason.UnknownFace, _c.AnimationMs, CancellationToken.None); }
                catch { }
                finally { _previewBtn.IsEnabled = true; _suppress = false; }
            };
            body.Children.Add(Card("Lock animation",
                Sw("Show animation", () => _c.ShowAnimation, v => _c.ShowAnimation = v),
                SliderRow("Duration", 600, 2500, 50, () => _c.AnimationMs, v => _c.AnimationMs = (int)v, v => $"{v:0} ms"),
                _previewBtn));

            var enrollBtn = Btn("Enroll / Re-enroll", "Accent");
            enrollBtn.Click += (_, _) => OpenEnroll();
            var delBtn = Btn("Delete face data", "Flat");
            delBtn.Foreground = Theme.Br("#FF8A8A"); delBtn.Margin = new Thickness(8, 0, 0, 0);
            delBtn.Click += (_, _) => DeleteFace();
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(enrollBtn); row.Children.Add(delBtn);
            body.Children.Add(Card("Face enrollment", _enrollText, row));

            body.Children.Add(Card("Activity", _logPanel));

            var sv = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = body
            };
            var dock = new DockPanel();
            DockPanel.SetDock(head, Dock.Top);
            dock.Children.Add(head); dock.Children.Add(sv);

            return new Border
            {
                Margin = new Thickness(10), CornerRadius = new CornerRadius(14),
                Background = Theme.Br("#0F1115"), BorderBrush = Theme.Br("#262B33"), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.5, Color = Colors.Black },
                RenderTransform = _slide, Child = dock
            };
        }

        // ---------------- actions ----------------
        private void OpenEnroll()
        {
            try
            {
                if (_enrollWin != null) { _enrollWin.Activate(); return; }
                bool wasPaused = _guard.State == GuardState.Paused;
                if (!wasPaused) _guard.PauseFor(null);   // free the IR camera while enrolling
                _suppress = true;
                var w = new EnrollmentWindow(_enroll);
                _enrollWin = w;
                w.Closed += (_, _) =>
                {
                    _enrollWin = null; _suppress = false;
                    if (!wasPaused) { try { _guard.Resume(); } catch { } }
                    RefreshEnroll();
                    if (IsVisible) Activate();
                };
                w.Show();
            }
            catch { _suppress = false; }
        }

        private void DeleteFace()
        {
            _suppress = true;
            try
            {
                var r = System.Windows.MessageBox.Show(this, "Delete your enrolled face data? You will need to enroll again.",
                    "Argus", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r == MessageBoxResult.Yes) _store.Clear();
            }
            catch { }
            finally { _suppress = false; RefreshEnroll(); Activate(); }
        }
    }
}
