using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

namespace PresenceGuard
{
    public sealed class IrCamera : IIrCamera
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        private sealed class Session : IAsyncDisposable
        {
            public MediaCapture Cap = null!;
            public MediaFrameReader Reader = null!;
            public Channel<IrFrame> Ch = null!;
            public TypedEventHandler<MediaFrameReader, MediaFrameArrivedEventArgs> Handler = null!;
            public async ValueTask DisposeAsync()
            {
                try { Reader.FrameArrived -= Handler; } catch { }
                try { await Reader.StopAsync(); } catch { }
                try { Reader.Dispose(); } catch { }
                try { Cap.Dispose(); } catch { }
                Ch.Writer.TryComplete();
            }
        }

        private static bool IsIr(string? name) =>
            name != null && (name.Contains("IR", StringComparison.OrdinalIgnoreCase) || name.Contains("infrared", StringComparison.OrdinalIgnoreCase));

        private static async Task<(Session? S, IrCameraStatus St, string? D)> OpenAsync()
        {
            MediaCapture? cap = null;
            try
            {
                var groups = await MediaFrameSourceGroup.FindAllAsync();
                MediaFrameSourceGroup? grp = null;
                MediaFrameSourceInfo? info = null;
                foreach (var g in groups)
                {
                    var i = g.SourceInfos.FirstOrDefault(x => x.SourceKind == MediaFrameSourceKind.Infrared);
                    if (i != null) { grp = g; info = i; break; }
                }
                if (grp == null)
                    foreach (var g in groups)
                    {
                        var i = g.SourceInfos.FirstOrDefault(x => x.SourceKind == MediaFrameSourceKind.Custom
                                                                   && (IsIr(g.DisplayName) || IsIr(x.DeviceInformation?.Name)));
                        if (i != null) { grp = g; info = i; break; }
                    }
                if (grp == null || info == null) return (null, IrCameraStatus.NotFound, "No infrared source found");

                cap = new MediaCapture();
                await cap.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    SourceGroup = grp,
                    SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                    StreamingCaptureMode = StreamingCaptureMode.Video
                });
                if (!cap.FrameSources.TryGetValue(info.Id, out var src))
                { cap.Dispose(); return (null, IrCameraStatus.NotFound, "IR frame source missing"); }

                var reader = await cap.CreateFrameReaderAsync(src);
                reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
                var ch = Channel.CreateBounded<IrFrame>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
                TypedEventHandler<MediaFrameReader, MediaFrameArrivedEventArgs> h = (r, _) => OnFrame(r, ch);
                reader.FrameArrived += h;
                var s = new Session { Cap = cap, Reader = reader, Ch = ch, Handler = h };
                var st = await reader.StartAsync();
                if (st != MediaFrameReaderStartStatus.Success)
                {
                    await s.DisposeAsync();
                    return (null, st == MediaFrameReaderStartStatus.DeviceNotAvailable
                                  || st == MediaFrameReaderStartStatus.ExclusiveControlNotAvailable
                                      ? IrCameraStatus.Busy : IrCameraStatus.Error, st.ToString());

                }
                return (s, IrCameraStatus.Ok, null);
            }
            catch (Exception ex)
            {
                try { cap?.Dispose(); } catch { }
                return (null, Map(ex), ex.Message);
            }
        }

        private static IrCameraStatus Map(Exception ex)
        {
            if (ex is UnauthorizedAccessException) return IrCameraStatus.AccessDenied;
            uint hr = (uint)ex.HResult;
            if (hr == 0x80070020 || hr == 0xC00D3704 || hr == 0xC00D3EA3 || hr == 0x800700AA
                || ex.Message.Contains("in use", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("exclusive", StringComparison.OrdinalIgnoreCase)) return IrCameraStatus.Busy;
            return IrCameraStatus.Error;
        }

        private static void OnFrame(MediaFrameReader r, Channel<IrFrame> ch)
        {
            try
            {
                using var fr = r.TryAcquireLatestFrame();
                var bmp = fr?.VideoMediaFrame?.SoftwareBitmap;
                if (bmp == null) return;
                try
                {
                    var f = Convert(bmp);
                    if (f != null) ch.Writer.TryWrite(f);
                }
                finally { bmp.Dispose(); }
            }
            catch { }
        }

        /// <summary>Optional diagnostics sink (wired to the activity log in Program).</summary>
        public static Action<string>? Diag;
        private static int _diagLeft = 6;

        private static IrFrame? Convert(SoftwareBitmap bmp, bool allowFallback = true)
        {
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            if (w <= 0 || h <= 0) return null;

            // Real row stride (rows can be padded, which shears/garbles the image if ignored)
            int stride;
            using (var lk = bmp.LockBuffer(BitmapBufferAccessMode.Read))
                stride = lk.GetPlaneDescription(0).Stride;

            var gray = new byte[w * h];
            var fmt = bmp.BitmapPixelFormat;
            switch (fmt)
            {
                case BitmapPixelFormat.Gray8:
                case BitmapPixelFormat.Nv12:
                case BitmapPixelFormat.Yuy2:
                    {
                        int need = fmt == BitmapPixelFormat.Nv12 ? stride * h * 3 / 2 : stride * h;
                        var b = new byte[need];
                        bmp.CopyToBuffer(b.AsBuffer());
                        if (fmt == BitmapPixelFormat.Yuy2)
                        {
                            for (int y = 0; y < h; y++)
                                for (int x = 0; x < w; x++) gray[y * w + x] = b[y * stride + 2 * x]; // Y samples
                        }
                        else
                        {
                            for (int y = 0; y < h; y++) Buffer.BlockCopy(b, y * stride, gray, y * w, w);
                        }
                        break;
                    }
                case BitmapPixelFormat.Gray16:
                    {
                        var b = new byte[stride * h];
                        bmp.CopyToBuffer(b.AsBuffer());
                        // IR sensors usually deliver 10/12-bit values inside 16 bits: pick the shift from the frame's peak
                        int max = 0;
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                            {
                                int v = b[y * stride + 2 * x] | (b[y * stride + 2 * x + 1] << 8);
                                if (v > max) max = v;
                            }
                        int shift = max > 4095 ? 8 : max > 1023 ? 4 : max > 255 ? 2 : 0;
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                            {
                                int v = (b[y * stride + 2 * x] | (b[y * stride + 2 * x + 1] << 8)) >> shift;
                                gray[y * w + x] = (byte)(v > 255 ? 255 : v);
                            }
                        break;
                    }
                case BitmapPixelFormat.Bgra8:
                    {
                        var b = new byte[stride * h];
                        bmp.CopyToBuffer(b.AsBuffer());
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                            {
                                int i = y * stride + 4 * x;
                                gray[y * w + x] = (byte)((114 * b[i] + 587 * b[i + 1] + 299 * b[i + 2]) / 1000);
                            }
                        break;
                    }
                default:
                    if (allowFallback)
                    {
                        try
                        {
                            using var c = SoftwareBitmap.Convert(bmp, BitmapPixelFormat.Gray8);
                            return Convert(c, false);
                        }
                        catch { }
                    }
                    if (_diagLeft > 0) { _diagLeft--; Diag?.Invoke("IR frame: unsupported pixel format " + fmt); }
                    return null;
            }

            if (_diagLeft > 0)
            {
                _diagLeft--;
                long sum = 0; int mx = 0, mn = 255;
                foreach (var v in gray) { sum += v; if (v > mx) mx = v; if (v < mn) mn = v; }
                Diag?.Invoke($"IR frame: {fmt} {w}x{h} stride={stride} min={mn} mean={sum / gray.Length} max={mx}");
            }
            return new IrFrame(w, h, gray, DateTime.UtcNow);
        }

        public async Task<BurstResult> CaptureBurstAsync(int count, TimeSpan timeout, CancellationToken ct)
        {
            var frames = new List<IrFrame>();
            try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { return new BurstResult(IrCameraStatus.Error, frames, ex.Message); }
            try
            {
                var (s, st, detail) = await OpenAsync().ConfigureAwait(false);
                if (s == null) return new BurstResult(st, frames, detail);
                await using (s)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(timeout);
                    try
                    {
                        while (frames.Count < count && await s.Ch.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false))
                            while (frames.Count < count && s.Ch.Reader.TryRead(out var f)) frames.Add(f);
                    }
                    catch (OperationCanceledException) { }
                }
                return frames.Count > 0 ? new BurstResult(IrCameraStatus.Ok, frames, null)
                                        : new BurstResult(IrCameraStatus.Error, frames, "No frames received before timeout");
            }
            catch (Exception ex) { return new BurstResult(Map(ex), frames, ex.Message); }
            finally { _gate.Release(); }
        }

        public async IAsyncEnumerable<IrFrame> StreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            bool got = false;
            try { await _gate.WaitAsync(ct).ConfigureAwait(false); got = true; } catch { }
            if (!got) yield break;
            try
            {
                var (s, _, _) = await OpenAsync().ConfigureAwait(false);
                if (s == null) yield break;
                await using (s)
                {
                    long lastYield = 0;
                    while (true)
                    {
                        bool ok;
                        try { ok = await s.Ch.Reader.WaitToReadAsync(ct).ConfigureAwait(false); } catch { break; }
                        if (!ok) break;
                        IrFrame? last = null;
                        while (s.Ch.Reader.TryRead(out var f)) last = f;
                        if (last == null) continue;
                        long now = Environment.TickCount64;
                        if (now - lastYield < 100) continue;
                        lastYield = now;
                        yield return last;
                    }
                }
            }
            finally { _gate.Release(); }
        }
    }
}
