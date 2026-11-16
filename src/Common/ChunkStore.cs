using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace Wolf
{
    // Content-addressed chunk storage. Every mod file is split into fixed-size chunks;
    // each chunk is stored once under its SHA-256 name, so identical files (and identical
    // parts of big archives) are transferred and stored only once across all versions.
    public sealed class ChunkStore
    {
        public const int ChunkSize = 1 << 20; // 1 MiB

        private readonly string root;

        public ChunkStore(string rootFolder)
        {
            root = Path.GetFullPath(rootFolder);
            Directory.CreateDirectory(root);
        }

        public string Root { get { return root; } }

        public static string HashOf(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(data));
            }
        }

        public static string HashOfFile(string path)
        {
            using (Stream s = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(s));
            }
        }

        public string ChunkPath(string sha)
        {
            if (string.IsNullOrEmpty(sha) || sha.Length < 6)
            {
                throw new ArgumentException("bad chunk hash: " + sha);
            }
            return Path.Combine(root, sha.Substring(0, 2), sha.Substring(2, 2), sha);
        }

        public bool Has(string sha, long expectedSize)
        {
            if (string.IsNullOrEmpty(sha)) { return false; }
            string p = ChunkPath(sha);
            if (!File.Exists(p)) { return false; }
            if (expectedSize > 0)
            {
                try { return new FileInfo(p).Length == expectedSize; }
                catch { return false; }
            }
            return true;
        }

        public byte[] Read(string sha)
        {
            string p = ChunkPath(sha);
            return File.Exists(p) ? File.ReadAllBytes(p) : null;
        }

        public bool HasAll(IList<ChunkRef> chunks)
        {
            if (chunks == null) { return false; }
            foreach (ChunkRef c in chunks)
            {
                if (!Has(c.sha, c.size)) { return false; }
            }
            return chunks.Count > 0;
        }

        // Verifies the payload against its hash before storing (never trusts a peer).
        public bool Put(string sha, byte[] data)
        {
            if (data == null) { return false; }
            string actual = HashOf(data);
            if (!string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase)) { return false; }
            string p = ChunkPath(actual);
            if (File.Exists(p) && new FileInfo(p).Length == data.Length) { return true; }
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            string tmp = p + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(p)) { File.Delete(p); }
            File.Move(tmp, p);
            return true;
        }

        public void PutRaw(string path, byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path)) { File.Delete(path); }
            File.Move(tmp, path);
        }

        // Splits a file into the store, returns chunk descriptors (hash + size).
        public List<ChunkRef> ImportFile(string filePath)
        {
            List<ChunkRef> refs = new List<ChunkRef>();
            using (FileStream fs = File.OpenRead(filePath))
            {
                byte[] buffer = new byte[ChunkSize];
                int read;
                while ((read = ReadFull(fs, buffer)) > 0)
                {
                    byte[] chunk = read == buffer.Length ? buffer : Sub(buffer, read);
                    string sha = HashOf(chunk);
                    if (!Has(sha, chunk.Length)) { PutRaw(ChunkPath(sha), chunk); }
                    refs.Add(new ChunkRef(sha, chunk.Length));
                    if (read < buffer.Length) { break; }
                    buffer = new byte[ChunkSize];
                }
            }
            return refs;
        }

        // Rebuilds a file from the store. Returns false when some chunks are missing.
        public bool ExportFile(string targetPath, IList<ChunkRef> chunks, Action<long> progress)
        {
            foreach (ChunkRef c in chunks)
            {
                if (!Has(c.sha, c.size)) { return false; }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
            string tmp = targetPath + ".wolf-tmp";
            using (FileStream outStream = File.Create(tmp))
            {
                long done = 0;
                foreach (ChunkRef c in chunks)
                {
                    using (FileStream cs = File.OpenRead(ChunkPath(c.sha)))
                    {
                        cs.CopyTo(outStream);
                    }
                    done += c.size;
                    if (progress != null) { progress(done); }
                }
            }
            if (File.Exists(targetPath)) { File.Delete(targetPath); }
            File.Move(tmp, targetPath);
            return true;
        }

        public long TotalSize()
        {
            long total = 0;
            foreach (string f in GamePaths.SafeFiles(root, "*", SearchOption.AllDirectories))
            {
                if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) { continue; }
                try { total += new FileInfo(f).Length; }
                catch { }
            }
            return total;
        }

        public static string ToHex(byte[] data)
        {
            StringBuilderHex sb = new StringBuilderHex(data.Length * 2);
            foreach (byte b in data) { sb.Append(b); }
            return sb.ToString();
        }

        private static int ReadFull(Stream s, byte[] buffer)
        {
            int total = 0;
            int n;
            while (total < buffer.Length && (n = s.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += n;
            }
            return total;
        }

        private static byte[] Sub(byte[] source, int count)
        {
            byte[] result = new byte[count];
            Array.Copy(source, result, count);
            return result;
        }

        // Tiny hex builder (avoids string.Format in a hot loop).
        private struct StringBuilderHex
        {
            private readonly char[] chars;
            private int pos;

            public StringBuilderHex(int length)
            {
                chars = new char[length];
                pos = 0;
            }

            public void Append(byte b)
            {
                chars[pos++] = HexChar(b >> 4);
                chars[pos++] = HexChar(b & 15);
            }

            public override string ToString() { return new string(chars, 0, pos); }

            private static char HexChar(int v) { return (char)(v < 10 ? '0' + v : 'a' + v - 10); }
        }
    }

    public struct ChunkRef
    {
        public string sha;
        public long size;

        public ChunkRef(string sha, long size)
        {
            this.sha = sha;
            this.size = size;
        }

        public Dictionary<string, object> ToJson()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["h"] = sha;
            d["s"] = size;
            return d;
        }

        public static ChunkRef FromJson(Dictionary<string, object> d)
        {
            return new ChunkRef(Json.Str(d, "h", ""), Json.Long(d, "s", 0));
        }
    }
}
