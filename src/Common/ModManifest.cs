using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Wolf
{
    // One file inside a mod pack: relative path + size + its chunks.
    public sealed class ManifestFile
    {
        public string rel;      // POSIX-style path relative to the Mods folder
        public long size;
        public string hash;     // sha256 of the whole file (quick integrity check)
        public List<ChunkRef> chunks = new List<ChunkRef>();

        public Dictionary<string, object> ToJson()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["p"] = rel;
            d["s"] = size;
            d["h"] = hash;
            List<object> cs = new List<object>();
            foreach (ChunkRef c in chunks) { cs.Add(c.ToJson()); }
            d["c"] = cs;
            return d;
        }

        public static ManifestFile FromJson(Dictionary<string, object> d)
        {
            ManifestFile f = new ManifestFile();
            f.rel = Json.Str(d, "p", "").Replace('\\', '/');
            f.size = Json.Long(d, "s", 0);
            f.hash = Json.Str(d, "h", "");
            foreach (object o in Json.Arr(d, "c"))
            {
                Dictionary<string, object> cd = o as Dictionary<string, object>;
                if (cd != null) { f.chunks.Add(ChunkRef.FromJson(cd)); }
            }
            return f;
        }
    }

    // Description of the mod pack a server distributes to clients.
    public sealed class ModManifest
    {
        public string serverName = "";
        public string version = "1";
        public string note = "";
        public DateTime builtUtc = DateTime.UtcNow;
        public List<string> loadOrder = new List<string>();      // mod folder names, in load order
        public List<ManifestFile> files = new List<ManifestFile>();
        public Dictionary<string, string> modVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public long TotalBytes
        {
            get { return files.Sum(f => f.size); }
        }

        public Dictionary<string, object> ToJson()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["name"] = serverName;
            d["version"] = version;
            d["note"] = note;
            d["built"] = builtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            d["order"] = loadOrder.Cast<object>().ToList();
            List<object> fs = new List<object>();
            foreach (ManifestFile f in files) { fs.Add(f.ToJson()); }
            d["files"] = fs;
            Dictionary<string, object> mv = new Dictionary<string, object>();
            foreach (KeyValuePair<string, string> kv in modVersions) { mv[kv.Key] = kv.Value; }
            d["modVersions"] = mv;
            d["total"] = TotalBytes;
            return d;
        }

        public static ModManifest FromJson(string text)
        {
            Dictionary<string, object> d = Json.ParseObject(text);
            return FromObject(d);
        }

        public static ModManifest FromObject(Dictionary<string, object> d)
        {
            ModManifest m = new ModManifest();
            m.serverName = Json.Str(d, "name", "");
            m.version = Json.Str(d, "version", "1");
            m.note = Json.Str(d, "note", "");
            m.loadOrder = Json.Arr(d, "order").Select(o => Convert.ToString(o)).Where(s => !string.IsNullOrEmpty(s)).ToList();
            foreach (object o in Json.Arr(d, "files"))
            {
                Dictionary<string, object> fd = o as Dictionary<string, object>;
                if (fd != null) { m.files.Add(ManifestFile.FromJson(fd)); }
            }
            foreach (KeyValuePair<string, object> kv in Json.Obj(d, "modVersions"))
            {
                m.modVersions[kv.Key] = Convert.ToString(kv.Value);
            }
            DateTime built;
            if (DateTime.TryParseExact(Json.Str(d, "built", ""), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out built))
            {
                m.builtUtc = built.ToUniversalTime();
            }
            return m;
        }

        // Short, stable id shown in the UI (same idea as ModSync's "def23678"):
        // depends only on file hashes, so cosmetic changes do not force a re-download.
        public string ContentHash
        {
            get
            {
                using (SHA256 sha = SHA256.Create())
                using (Stream s = new MemoryStream())
                {
                    using (StreamWriter w = new StreamWriter(s, new UTF8Encoding(false)))
                    {
                        foreach (ManifestFile f in files.OrderBy(f => f.rel, StringComparer.Ordinal))
                        {
                            w.Write(f.rel);
                            w.Write('|');
                            w.Write(f.size.ToString(CultureInfo.InvariantCulture));
                            w.Write('|');
                            w.Write(f.hash);
                            w.Write('\n');
                        }
                        w.Flush();
                        s.Position = 0;
                        return ChunkStore.ToHex(sha.ComputeHash(s)).Substring(0, 8);
                    }
                }
            }
        }

        public ManifestFile Find(string rel)
        {
            return files.FirstOrDefault(f => string.Equals(f.rel, rel, StringComparison.OrdinalIgnoreCase));
        }

        // ---------- building ----------

        public static ModManifest Build(string modsDir, ChunkStore store, string serverName, string version, string note, out string error)
        {
            return Build(modsDir, store, serverName, version, note, ModFilters.ForThisServer(modsDir), null, null, null, out error);
        }

        // Scans a Mods folder, imports every file into the chunk store and produces the manifest.
        // Only files the server actually hands out reach the manifest: the filter rules come
        // from ModSync's exclude_patterns and (optionally) the anti-cheat ServerOnlyMods list,
        // so the pack stays identical to what players already receive today.
        //
        // previous lets a re-publish reuse the chunk list of unchanged files, so watching a
        // 8 GB Mods folder does not re-read it from disk on every change.
        public static ModManifest Build(string modsDir, ChunkStore store, string serverName, string version, string note,
                                        ModFilters filters, HashCache cache, ModManifest previous,
                                        Action<string> onProgress, out string error)
        {
            error = null;
            ModManifest m = new ModManifest();
            m.serverName = serverName ?? "";
            m.version = version ?? "1";
            m.note = note ?? "";
            m.builtUtc = DateTime.UtcNow;

            if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir))
            {
                error = "Папка Mods не найдена: " + modsDir;
                return m;
            }
            if (filters == null) { filters = new ModFilters(); }

            foreach (string modDir in GamePaths.SafeDirs(modsDir).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal))
            {
                string modName = Path.GetFileName(modDir);
                if (filters.IsModExcluded(modName)) { continue; }
                m.loadOrder.Add(modName);
                m.modVersions[modName] = ReadModVersion(modDir);
                if (onProgress != null) { onProgress(modName); }

                foreach (string file in GamePaths.SafeFiles(modDir, "*", SearchOption.AllDirectories)
                                              .OrderBy(f => f, StringComparer.Ordinal))
                {
                    string rel = Combine(modName, file.Substring(modDir.Length).TrimStart('\\', '/'));
                    if (filters.IsFileExcluded(rel)) { continue; }
                    long size;
                    DateTime mtime;
                    try
                    {
                        FileInfo fi = new FileInfo(file);
                        size = fi.Length;
                        mtime = fi.LastWriteTimeUtc;
                    }
                    catch { continue; }
                    if (size == 0) { continue; }

                    ManifestFile mf = new ManifestFile();
                    mf.rel = rel;
                    mf.size = size;
                    mf.hash = cache != null ? cache.HashOf(file, size, mtime) : ChunkStore.HashOfFile(file);

                    ManifestFile known = previous == null ? null : previous.Find(rel);
                    if (known != null && known.size == size &&
                        string.Equals(known.hash, mf.hash, StringComparison.OrdinalIgnoreCase) &&
                        store.HasAll(known.chunks))
                    {
                        mf.chunks = known.chunks;
                    }
                    else
                    {
                        mf.chunks = store.ImportFile(file);
                    }
                    m.files.Add(mf);
                }
            }

            if (m.files.Count == 0) { error = "В папке Mods нет ни одного файла."; }
            return m;
        }

        // ModInfo.xml carries <Version> and <Name>; used to show what actually changed.
        private static string ReadModVersion(string modDir)
        {
            string info = Path.Combine(modDir, "ModInfo.xml");
            if (!File.Exists(info)) { return ""; }
            try
            {
                System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
                doc.Load(info);
                System.Xml.XmlNode v = doc.SelectSingleNode("//Version");
                if (v != null && !string.IsNullOrWhiteSpace(v.InnerText)) { return v.InnerText.Trim(); }
            }
            catch { }
            return "";
        }

        private static string Combine(string modName, string rest)
        {
            return modName + "/" + rest.Replace('\\', '/');
        }
    }
}
