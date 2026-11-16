using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WF = System.Windows.Forms;

namespace PresenceGuard
{
    /// <summary>"Seal" lock animation: dim + hex grid, scan ring/line, shield+padlock draw-on, pulse, fade.</summary>
    public sealed class SealOverlay : ILockAnimation
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
        private const uint SWP_NOACTIVATE = 0x10;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);

        private static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };

        public async Task PlayAsync(LockReason reason, int durationMs, CancellationToken ct)
        {
            try
            {
                var disp = System.Windows.Application.Current?.Dispatcher;
                if (disp == null || ct.IsCancellationRequested) return;
                int d = Math.Clamp(durationMs, 300, 10000);
                var inner = await disp.InvokeAsync<Task>(() => Run(reason, d, ct)).Task;
                await inner;
            }
            catch { }
        }

        private static async Task Run(LockReason reason, int D, CancellationToken ct)
        {
            var wins = new List<Window>();
            var begins = new List<Action>();
            try
            {
                foreach (var s in WF.Screen.AllScreens)
                {
                    try { var (w, b) = Build(s.Bounds, reason, D); wins.Add(w); begins.Add(b); } catch { }
                }
                foreach (var b in begins) { try { b(); } catch { } }
                try { await Task.Delay(D + 40, ct); } catch (OperationCanceledException) { }
            }
            catch { }
            finally
            {
                foreach (var w in wins) { try { w.Close(); } catch { } }
            }
        }

        private static (Window, Action) Build(System.Drawing.Rectangle b, LockReason reason, int D)
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
                Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false, ShowActivated = false,
                Focusable = false, WindowStartupLocation = WindowStartupLocation.Manual
            };
            IntPtr hwnd = new WindowInteropHelper(w).EnsureHandle();
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED);
            SetWindowPos(hwnd, HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, SWP_NOACTIVATE);   // lands on the right monitor
            double k = GetDpiForWindow(hwnd) / 96.0; if (k < 0.5) k = 1;
            w.Left = b.X / k; w.Top = b.Y / k; w.Width = b.Width / k; w.Height = b.Height / k;
            var (root, begin) = Scene(b.Width / k, b.Height / k, reason, D);
            w.Content = root;
            w.Show();
            SetWindowPos(hwnd, HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, SWP_NOACTIVATE);
            return (w, begin);
        }

        private static Action Key(IAnimatable t, DependencyProperty p, int D, bool ease, (double f, double v)[] ks)
        {
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(D) };
            foreach (var (f, v) in ks)
            {
                var kt = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Math.Clamp(f, 0, 1) * D));
                a.KeyFrames.Add(ease ? (DoubleKeyFrame)new EasingDoubleKeyFrame(v, kt, Out) : new LinearDoubleKeyFrame(v, kt));
            }
            return () => t.BeginAnimation(p, a);
        }

        private static Brush Hex(Color c)
        {
            var g = Geometry.Parse("M0,16 L27.71,0 L55.43,16 L55.43,48 L27.71,64 L0,48 Z M27.71,64 L27.71,96");
            var d = new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(0x3A, c.R, c.G, c.B)), 1), g);
            return new DrawingBrush(d)
            {
                TileMode = TileMode.Tile,
                Viewbox = new Rect(0, 0, 55.43, 96), ViewboxUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 72.06, 124.8), ViewportUnits = BrushMappingMode.Absolute
            };
        }

        private static (Grid, Action) Scene(double W, double H, LockReason reason, int D)
        {
            Color acc = reason == LockReason.UnknownFace ? Theme.C("#FF4D4D") : Theme.C("#22D3EE");
            Color Ac(byte a) => Color.FromArgb(a, acc.R, acc.G, acc.B);
            string label = reason switch
            {
                LockReason.UnknownFace => "UNKNOWN FACE - LOCKING",
                LockReason.NoFaceRepeated => "NO ONE DETECTED - SECURING",
                _ => "LOCKING"
            };
            var ks = new List<Action>();
            void K(IAnimatable t, DependencyProperty p, bool ease, params (double f, double v)[] k) => ks.Add(Key(t, p, D, ease, k));
            double maxD = Math.Sqrt(W * W + H * H), m = Math.Min(W, H);
            var root = new Grid { Width = W, Height = H, IsHitTestVisible = false };

            // 1) 0-25%: dim + vignette + hex grid
            var bg = new Grid { Opacity = 0 };
            bg.Children.Add(new Rectangle { Fill = new RadialGradientBrush(Color.FromArgb(0xA8, 4, 6, 10), Color.FromArgb(0xE6, 0, 0, 0)) { RadiusX = 0.85, RadiusY = 0.85 } });
            bg.Children.Add(new Rectangle { Fill = Hex(acc) });
            K(bg, UIElement.OpacityProperty, true, (0, 0), (.25, 1));
            root.Children.Add(bg);

            Ellipse E(double th, byte a) => new Ellipse
            {
                Stroke = new SolidColorBrush(Ac(a)), StrokeThickness = th, Width = 0, Height = 0, Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
            };

            // 2) 15-60%: expanding scan ring (glow + core) and sweeping scan line
            foreach (var (th, a) in new[] { (12.0, (byte)0x22), (2.5, (byte)0xFF) })
            {
                var e = E(th, a); root.Children.Add(e);
                K(e, FrameworkElement.WidthProperty, true, (0, 0), (.15, 0), (.6, maxD));
                K(e, FrameworkElement.HeightProperty, true, (0, 0), (.15, 0), (.6, maxD));
                K(e, UIElement.OpacityProperty, false, (0, 0), (.15, 0), (.2, 1), (.45, 1), (.6, 0));
            }
            var tt = new TranslateTransform(0, -90);
            var scan = new Grid { Height = 90, VerticalAlignment = VerticalAlignment.Top, Opacity = 0, RenderTransform = tt, IsHitTestVisible = false };
            scan.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Ac(0), Ac(0x38), 90) });
            scan.Children.Add(new Rectangle
            {
                Height = 2, VerticalAlignment = VerticalAlignment.Bottom,
                Fill = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Ac(0), 0), new GradientStop(Ac(0xFF), 0.5), new GradientStop(Ac(0), 1) }, 0)
            });
            root.Children.Add(scan);
            K(tt, TranslateTransform.YProperty, false, (0, -90), (.15, -90), (.6, H));
            K(scan, UIElement.OpacityProperty, false, (0, 0), (.15, 0), (.2, 1), (.55, 1), (.6, 0));

            // 3) 55-85%: shield + padlock draw themselves, shackle slides down and snaps shut
            var accB = new SolidColorBrush(acc);
            Path P(string data, bool dash) => new Path
            {
                Data = Geometry.Parse(data), Stroke = accB, StrokeThickness = 3,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeDashArray = dash ? new DoubleCollection { 200, 200 } : null!, StrokeDashOffset = dash ? 205 : 0
            };
            var shield = P("M60,4 L112,22 L112,70 C112,102 88,124 60,136 C32,124 8,102 8,70 L8,22 Z", true);
            var body = P("M42,64 H78 A4,4 0 0 1 82,68 V92 A4,4 0 0 1 78,96 H42 A4,4 0 0 1 38,92 V68 A4,4 0 0 1 42,64 Z", true);
            var shackle = P("M47,64 V52 A13,13 0 0 1 73,52 V64", false);
            var shTt = new TranslateTransform(0, -14);
            shackle.RenderTransform = shTt; shackle.Opacity = 0;
            var key = new Ellipse { Width = 8, Height = 8, Fill = accB, Opacity = 0 };
            Canvas.SetLeft(key, 56); Canvas.SetTop(key, 76);
            var cv = new Canvas { Width = 120, Height = 140 };
            cv.Children.Add(shield); cv.Children.Add(body); cv.Children.Add(shackle); cv.Children.Add(key);
            var st = new ScaleTransform(.9, .9);
            var vb = new Viewbox { Height = m * 0.24, Child = cv, Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(.5, .5), RenderTransform = st };
            var lbl = new TextBlock
            {
                Text = label, Foreground = accB, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = Math.Clamp(m * 0.017, 13, 22), Margin = new Thickness(0, m * 0.025, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0
            };
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            stack.Children.Add(vb); stack.Children.Add(lbl);
            root.Children.Add(stack);
            K(shield, Shape.StrokeDashOffsetProperty, true, (0, 205), (.55, 205), (.73, 0));
            K(body, Shape.StrokeDashOffsetProperty, true, (0, 205), (.6, 205), (.76, 0));
            K(shackle, UIElement.OpacityProperty, false, (0, 0), (.68, 0), (.70, 1));
            K(shTt, TranslateTransform.YProperty, true, (0, -14), (.74, -14), (.82, 0), (.845, 2.5), (.87, 0));
            K(key, UIElement.OpacityProperty, false, (0, 0), (.84, 0), (.86, 1));
            K(st, ScaleTransform.ScaleXProperty, true, (0, .9), (.55, .9), (.8, 1));
            K(st, ScaleTransform.ScaleYProperty, true, (0, .9), (.55, .9), (.8, 1));
            K(lbl, UIElement.OpacityProperty, false, (0, 0), (.72, 0), (.8, 1));

            // 4) 85-100%: bright pulse ring, then everything fades out
            var pulse = E(4, 0xFF); root.Children.Add(pulse);
            K(pulse, FrameworkElement.WidthProperty, true, (0, m * .28), (.85, m * .28), (.97, maxD * .6));
            K(pulse, FrameworkElement.HeightProperty, true, (0, m * .28), (.85, m * .28), (.97, maxD * .6));
            K(pulse, UIElement.OpacityProperty, false, (0, 0), (.85, 0), (.86, 1), (.97, 0));
            K(root, UIElement.OpacityProperty, false, (0, 1), (.88, 1), (1, 0));

            return (root, () => { foreach (var a in ks) { try { a(); } catch { } } });
        }
    }
}
