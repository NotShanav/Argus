using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace PresenceGuard
{
    internal sealed class EnrollmentWindow : Window
    {
        private const int Target = 20;
        private readonly IEnrollmentService _svc;
        private readonly Image _img = new() { Stretch = Stretch.UniformToFill, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(-1, 1) };
        private readonly TextBlock _hint, _count;
        private readonly ScaleTransform _bar = new(0, 1);
        private readonly Button _cancel, _retry;
        private CancellationTokenSource? _cts;
        private bool _closed, _running;

        public EnrollmentWindow(IEnrollmentService svc)
        {
            _svc = svc;
            Resources = Theme.Styles();
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize; Topmost = true; Width = 440; Height = 540;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Theme.Font; FontSize = 13; Foreground = Theme.Br("#E6E8EB"); UseLayoutRounding = true;

            var accent = Color.FromArgb(0xFF, 0x22, 0xD3, 0xEE);
            Color Ac(byte a) => Color.FromArgb(a, accent.R, accent.G, accent.B);

            // --- preview frame ---
            var lines = new DrawingBrush(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(46, 0, 0, 0)), null, new RectangleGeometry(new Rect(0, 0, 4, 1.5))))
            {
                TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 4, 3), ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, 4, 3), ViewboxUnits = BrushMappingMode.Absolute
            };
            var sweepTt = new TranslateTransform(0, -60);
            var sweep = new System.Windows.Shapes.Rectangle
            {
                Height = 60, VerticalAlignment = VerticalAlignment.Top, RenderTransform = sweepTt, IsHitTestVisible = false,
                Fill = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Ac(0), 0), new GradientStop(Ac(0x40), 0.5), new GradientStop(Ac(0), 1) }, 90)
            };
            sweepTt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-60, 270, TimeSpan.FromSeconds(2.4)) { RepeatBehavior = RepeatBehavior.Forever });
            var inner = new Grid();
            inner.Children.Add(_img);
            inner.Children.Add(new System.Windows.Shapes.Rectangle { Fill = lines, IsHitTestVisible = false });
            inner.Children.Add(sweep);
            inner.Children.Add(new Border { BorderBrush = new SolidColorBrush(Ac(0x70)), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(16), IsHitTestVisible = false });
            var frame = new Border
            {
                Width = 372, Height = 270, CornerRadius = new CornerRadius(16), Background = Theme.Br("#181B21"),
                Clip = new RectangleGeometry(new Rect(0, 0, 372, 270), 16, 16), Child = inner
            };

            // --- text / progress ---
            _hint = new TextBlock { Text = "Starting IR camera...", FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MinHeight = 52, Margin = new Thickness(0, 18, 0, 0) };
            _count = new TextBlock { Text = $"0 / {Target}", FontSize = 11.5, Foreground = Theme.Br("#8B93A1"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            var track = new Border
            {
                Height = 8, CornerRadius = new CornerRadius(4), Background = Theme.Br("#2A2F38"), ClipToBounds = true, Margin = new Thickness(0, 6, 0, 0),
                Child = new Border { Background = new SolidColorBrush(accent), CornerRadius = new CornerRadius(4), RenderTransformOrigin = new Point(0, 0.5), RenderTransform = _bar }
            };

            // --- buttons ---
            _cancel = new Button { Content = "Cancel", Style = (Style)Resources["Flat"], Padding = new Thickness(16, 7, 16, 7) };
            _retry = new Button { Content = "Retry", Style = (Style)Resources["Accent"], Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
            _cancel.Click += (_, _) =>
            {
                if (_running) { _cancel.IsEnabled = false; _hint.Text = "Stopping..."; try { _cts?.Cancel(); } catch { } }
                else Close();
            };
            _retry.Click += (_, _) => Begin();
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            btns.Children.Add(_retry); btns.Children.Add(_cancel);

            var col = new StackPanel { Margin = new Thickness(24, 22, 24, 22) };
            col.Children.Add(new TextBlock { Text = "Enroll your face", FontSize = 20, FontWeight = FontWeights.SemiBold });
            col.Children.Add(new TextBlock { Text = "Look straight at the laptop, then turn your head slightly.", FontSize = 12, Foreground = Theme.Br("#8B93A1"), Margin = new Thickness(0, 2, 0, 16), TextWrapping = TextWrapping.Wrap });
            col.Children.Add(frame); col.Children.Add(_hint); col.Children.Add(_count); col.Children.Add(track); col.Children.Add(btns);

            var shell = new Border
            {
                Margin = new Thickness(10), CornerRadius = new CornerRadius(14), Background = Theme.Br("#0F1115"),
                BorderBrush = Theme.Br("#262B33"), BorderThickness = new Thickness(1), Child = col,
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.5, Color = Colors.Black }
            };
            shell.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
            Content = shell;

            Loaded += (_, _) => Begin();
        }

        private async void Begin()
        {
            try
            {
                _cts?.Dispose();
                var cts = _cts = new CancellationTokenSource();
                _running = true;
                _retry.Visibility = Visibility.Collapsed; _cancel.Content = "Cancel"; _cancel.IsEnabled = true;
                _hint.Foreground = Theme.Br("#E6E8EB"); _hint.Text = "Starting IR camera..."; _count.Text = $"0 / {Target}";
                _bar.BeginAnimation(ScaleTransform.ScaleXProperty, null); _bar.ScaleX = 0;

                bool ok;
                try { ok = await _svc.EnrollAsync(Target, new Progress<EnrollProgress>(OnProgress), cts.Token); }
                catch { ok = false; }
                _running = false;
                if (_closed) return;

                if (ok)
                {
                    _hint.Text = "All set"; _hint.Foreground = Theme.Br("#34D399"); _cancel.IsEnabled = false;
                    _bar.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
                    await Task.Delay(1200);
                    if (!_closed) Close();
                }
                else if (cts.IsCancellationRequested) Close();
                else
                {
                    _hint.Text = "Couldn't read the IR camera - close other camera apps and try again";
                    _hint.Foreground = Theme.Br("#FF8A8A");
                    _retry.Visibility = Visibility.Visible; _cancel.Content = "Close"; _cancel.IsEnabled = true;
                }
            }
            catch { }
        }

        private void OnProgress(EnrollProgress p)
        {
            try
            {
                if (_closed || !_running) return;
                _hint.Text = p.Hint;
                _count.Text = $"{p.Collected} / {p.Target}";
                double r = p.Target > 0 ? Math.Clamp((double)p.Collected / p.Target, 0, 1) : 0;
                _bar.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(r, TimeSpan.FromMilliseconds(220)));
                var f = p.Preview;
                if (f != null && f.Width > 0 && f.Height > 0 && f.Gray8.Length >= f.Width * f.Height)
                {
                    var bs = BitmapSource.Create(f.Width, f.Height, 96, 96, PixelFormats.Gray8, null, f.Gray8, f.Width);
                    bs.Freeze();
                    _img.Source = bs;
                }
            }
            catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            try { _cts?.Cancel(); _cts?.Dispose(); } catch { }
            base.OnClosed(e);
        }
    }
}
