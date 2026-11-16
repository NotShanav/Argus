using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PresenceGuard
{
    public sealed class DpapiEnrollmentStore : IEnrollmentStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PresenceGuard.v1");
        private readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Argus", "face.dat");
        private readonly object _lock = new();

        public bool HasEnrollment => Load().Count >= 3;

        public IReadOnlyList<float[]> Load()
        {
            lock (_lock)
            {
                var list = new List<float[]>();
                try
                {
                    if (!File.Exists(_path)) return list;
                    var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
                    using var br = new BinaryReader(new MemoryStream(plain));
                    int count = br.ReadInt32(), dims = br.ReadInt32();
                    if (count < 0 || count > 1000 || dims <= 0 || dims > 4096) return new List<float[]>();
                    if (plain.Length != 8 + (long)count * dims * 4) return new List<float[]>();
                    for (int i = 0; i < count; i++)
                    {
                        var v = new float[dims];
                        for (int j = 0; j < dims; j++) v[j] = br.ReadSingle();
                        list.Add(v);
                    }
                    return list;
                }
                catch { return new List<float[]>(); }
            }
        }

        public void Save(IReadOnlyList<float[]> templates)
        {
            lock (_lock)
            {
                try
                {
                    if (templates == null || templates.Count == 0) { Clear(); return; }
                    int dims = templates[0].Length;
                    using var ms = new MemoryStream();
                    using (var bw = new BinaryWriter(ms, Encoding.UTF8, true))
                    {
                        var good = new List<float[]>();
                        foreach (var t in templates) if (t != null && t.Length == dims) good.Add(t);
                        bw.Write(good.Count);
                        bw.Write(dims);
                        foreach (var t in good) foreach (var f in t) bw.Write(f);
                    }
                    var enc = ProtectedData.Protect(ms.ToArray(), Entropy, DataProtectionScope.CurrentUser);
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    var tmp = _path + ".tmp";
                    File.WriteAllBytes(tmp, enc);
                    File.Move(tmp, _path, true);
                }
                catch { }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                try { if (File.Exists(_path)) File.Delete(_path); } catch { }
            }
        }
    }
}
