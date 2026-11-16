using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace PresenceGuard
{
    public sealed class FaceEngine : IFaceEngine
    {
        // Standard 112x112 template: image-left eye, image-right eye, nose, left mouth corner, right mouth corner.
        private static readonly double[,] Dst =
        {
            { 38.2946, 51.6963 }, { 73.5318, 51.5014 }, { 56.0252, 71.7366 }, { 41.5493, 92.3655 }, { 70.7299, 92.2041 }
        };

        private readonly object _lock = new();
        private FaceDetectorYN? _det;
        private string _detPath = "";
        private int _detW = 320, _detH = 320;
        private Net? _net;
        public bool ModelsLoaded { get; }
        public string? LoadError { get; }

        public FaceEngine(string modelsDir)
        {
            try
            {
                var dp = Path.Combine(modelsDir, "face_detection_yunet_2023mar.onnx");
                var rp = Path.Combine(modelsDir, "face_recognition_sface_2021dec.onnx");
                if (!File.Exists(dp)) { LoadError = "Missing model: " + dp; return; }
                if (!File.Exists(rp)) { LoadError = "Missing model: " + rp; return; }
                _detPath = dp;
                _det = FaceDetectorYN.Create(dp, "", new Size(_detW, _detH), 0.6f, 0.3f, 5000);
                _net = CvDnn.ReadNetFromOnnx(rp);
                if (_net == null || _net.Empty()) { LoadError = "Could not load SFace model"; _net = null; _det = null; return; }
                ModelsLoaded = true;
            }
            catch (Exception ex) { LoadError = ex.Message; _det = null; _net = null; }
        }

        internal static float Cosine(float[] a, float[] b)
        {
            if (a.Length != b.Length || a.Length == 0) return 0;
            double d = 0, na = 0, nb = 0;
            for (int i = 0; i < a.Length; i++) { d += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
            return na <= 0 || nb <= 0 ? 0 : (float)(d / Math.Sqrt(na * nb));
        }

        // Least-squares similarity transform (rotation + uniform scale + translation) mapping p -> Dst.
        private static bool Similarity(double[,] p, out double[] m)
        {
            m = new double[6];
            double pxm = 0, pym = 0, qxm = 0, qym = 0;
            for (int k = 0; k < 5; k++) { pxm += p[k, 0]; pym += p[k, 1]; qxm += Dst[k, 0]; qym += Dst[k, 1]; }
            pxm /= 5; pym /= 5; qxm /= 5; qym /= 5;
            double nr = 0, ni = 0, den = 0;
            for (int k = 0; k < 5; k++)
            {
                double px = p[k, 0] - pxm, py = p[k, 1] - pym, qx = Dst[k, 0] - qxm, qy = Dst[k, 1] - qym;
                nr += px * qx + py * qy;
                ni += px * qy - py * qx;
                den += px * px + py * py;
            }
            if (den < 1e-6 || double.IsNaN(den)) return false;
            double ar = nr / den, ai = ni / den;
            double br = qxm - (ar * pxm - ai * pym);
            double bi = qym - (ai * pxm + ar * pym);
            m = new[] { ar, -ai, br, ai, ar, bi };
            return true;
        }

        private float[]? Embed(Mat bgr, Mat faces, int row)
        {
            var pts = new double[5, 2];
            for (int k = 0; k < 5; k++)
            {
                pts[k, 0] = faces.Get<float>(row, 4 + 2 * k);
                pts[k, 1] = faces.Get<float>(row, 5 + 2 * k);
            }
            if (!Similarity(pts, out var m)) return null;
            using var warp = new Mat(2, 3, MatType.CV_64FC1);
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 3; j++) warp.Set<double>(i, j, m[i * 3 + j]);
            using var aligned = new Mat();
            Cv2.WarpAffine(bgr, aligned, warp, new Size(112, 112), InterpolationFlags.Linear, BorderTypes.Constant);
            using var blob = CvDnn.BlobFromImage(aligned, 1.0, new Size(112, 112), new Scalar(0, 0, 0), true, false);
            _net!.SetInput(blob);
            using var outp = _net.Forward();
            using var c = outp.Clone();
            var arr = new float[(int)c.Total()];
            Marshal.Copy(c.Data, arr, 0, arr.Length);
            return arr;
        }

        private float[]? Process(IrFrame f, out int faceCount, out float quality)
        {
            faceCount = 0; quality = 0;
            int n = f.Width * f.Height;
            if (!ModelsLoaded || n <= 0 || f.Gray8.Length < n) return null;
            lock (_lock)
            {
                using var gray = new Mat(f.Height, f.Width, MatType.CV_8UC1);
                Marshal.Copy(f.Gray8, 0, gray.Data, n);
                using var work = new Mat();
                if (f.Width < 480) Cv2.Resize(gray, work, new Size(), 2, 2, InterpolationFlags.Linear);
                else gray.CopyTo(work);
                using var norm = new Mat();
                Cv2.Normalize(work, norm, 0, 255, NormTypes.MinMax);
                Cv2.MeanStdDev(norm, out _, out var sd);
                using var bgr = new Mat();
                Cv2.CvtColor(norm, bgr, ColorConversionCodes.GRAY2BGR);
                // This OpenCvSharp build has no SetInputSize: re-create the detector when the frame size changes.
                if (_det == null || _detW != bgr.Width || _detH != bgr.Height)
                {
                    _det?.Dispose();
                    _det = FaceDetectorYN.Create(_detPath, "", new Size(bgr.Width, bgr.Height), 0.6f, 0.3f, 5000);
                    _detW = bgr.Width; _detH = bgr.Height;
                }
                var faces = new Mat();
                _det.Detect(bgr, faces);
                using (faces)
                {
                    if (faces == null || faces.Empty() || faces.Rows == 0) return null;
                    faceCount = faces.Rows;
                    int best = 0; float bestArea = -1;
                    for (int i = 0; i < faces.Rows; i++)
                    {
                        float a = faces.Get<float>(i, 2) * faces.Get<float>(i, 3);
                        if (a > bestArea) { bestArea = a; best = i; }
                    }
                    float fw = faces.Get<float>(best, 2);
                    float sizeF = Math.Min(1f, fw / (0.15f * bgr.Width));
                    float contF = Math.Min(1f, (float)sd.Val0 / 40f);
                    quality = Math.Clamp(sizeF * contF, 0f, 1f);
                    return Embed(bgr, faces, best);
                }
            }
        }

        public FaceResult Analyze(IrFrame frame, IReadOnlyList<float[]> templates)
        {
            try
            {
                var e = Process(frame, out int n, out float q);
                if (n == 0) return new FaceResult(false, 0, 0f, 0f);
                if (e == null || templates == null || templates.Count == 0) return new FaceResult(true, n, 0f, q);
                var sims = templates.Where(t => t != null && t.Length == e.Length).Select(t => Cosine(e, t)).OrderByDescending(x => x).ToList();
                if (sims.Count == 0) return new FaceResult(true, n, 0f, q);
                float score = sims.Count < 3 ? sims[0] : sims.Take(3).Average();
                return new FaceResult(true, n, Math.Clamp(score, 0f, 1f), q);
            }
            catch { return new FaceResult(false, 0, 0f, 0f); }
        }

        public static Action<string>? Diag;
        private int _rejects;

        public float[]? ExtractEmbedding(IrFrame frame)
        {
            try
            {
                var e = Process(frame, out int n, out float q);
                if (e != null && q >= 0.25f) { _rejects = 0; return e; }
                if (++_rejects % 20 == 1)
                    Diag?.Invoke(n == 0 ? "Enroll: no face detected in IR frame" : $"Enroll: face rejected (quality {q:0.00})");
                return null;
            }
            catch (Exception ex) { Diag?.Invoke("Enroll: engine error: " + ex.Message); return null; }
        }
    }
}
