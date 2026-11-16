using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Wolf
{
    // One published pack, kept so the admin can see history and republish an older manifest.
    public sealed class PackEntry
    {
        public string hash = "";
        public string version = "";
        public string note = "";
        public string built = "";
        public int files;
        public long bytes;
        public List<string> changes = new List<string>();

        public Dictionary<string, object> ToJson()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["hash"] = hash;
            d["version"] = version;
            d["note"] = note;
            d["built"] = built;
            d["files"] = files;
            d["bytes"] = bytes;
            List<object> list = new List<object>();
            foreach (string s in changes) { list.Add(s); }
            d["changes"] = list;
            return d;
        }

        public static PackEntry FromJson(Dictionary<string, object> d)
        {
            PackEntry e = new PackEntry();
            e.hash = Json.Str(d, "hash", "");
            e.version = Json.Str(d, "version", "");
            e.note = Json.Str(d, "note", "");
            e.built = Json.Str(d, "built", "");
            e.files = Json.Int(d, "files", 0);
            e.bytes = Json.Long(d, "bytes", 0);
            foreach (object o in Json.Arr(d, "changes")) { e.changes.Add(Convert.ToString(o)); }
            return e;
        }

        public string Describe()
        {
            return (string.IsNullOrEmpty(version) ? "?" : version) + "   " + built +
                   "   файлов: " + files.ToString(CultureInfo.InvariantCulture) +
                   "   " + GamePaths.HumanSize(bytes) + "   " + ManifestDiff.Summary(changes);
        }
    }

    // History of published packs. Snapshots (manifest.json of each version) live next to the
    // history file, so an older pack can be served again without re-hashing the Mods folder.
    public sealed class PackHistory
    {
        public List<PackEntry> Entries = new List<PackEntry>();
        public int Keep = 30;

        public static PackHistory Load(string file)
        {
            PackHistory h = new PackHistory();
            Dictionary<string, object> root = Json.ReadFile(file);
            foreach (object o in Json.Arr(root, "entries"))
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d != null) { h.Entries.Add(PackEntry.FromJson(d)); }
            }
            return h;
        }

        public void Save(string file)
        {
            List<object> list = new List<object>();
            foreach (PackEntry e in Entries) { list.Add(e.ToJson()); }
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["entries"] = list;
            try { Json.WriteFile(file, root, true); } catch { }
        }

        // Newest first; publishing the same content again only refreshes its position.
        public PackEntry Add(ModManifest manifest, List<string> changes)
        {
            if (manifest == null) { return null; }
            string hash = manifest.ContentHash;
            PackEntry existing = Find(hash);
            Remove(hash);
            PackEntry e = new PackEntry();
            e.hash = hash;
            e.version = manifest.version;
            e.note = manifest.note;
            // Повторная публикация того же набора не должна «омолаживать» историю: время
            // первой публикации и список изменений остаются прежними, иначе по журналу
            // не понять, когда набор реально поменялся.
            e.built = existing != null && !string.IsNullOrEmpty(existing.built)
                ? existing.built
                : manifest.builtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            e.files = manifest.files.Count;
            e.bytes = manifest.TotalBytes;
            if (changes != null && changes.Count > 0) { e.changes.AddRange(changes); }
            else if (existing != null) { e.changes.AddRange(existing.changes); }
            Entries.Insert(0, e);
            Trim();
            return e;
        }

        public void Remove(string hash)
        {
            Entries.RemoveAll(x => string.Equals(x.hash, hash, StringComparison.OrdinalIgnoreCase));
        }

        public PackEntry Find(string hash)
        {
            return Entries.Find(x => string.Equals(x.hash, hash, StringComparison.OrdinalIgnoreCase));
        }

        public void Trim()
        {
            if (Keep < 1) { Keep = 1; }
            if (Entries.Count > Keep) { Entries.RemoveRange(Keep, Entries.Count - Keep); }
        }

        public static string SnapshotFile(string folder, string hash)
        {
            return Path.Combine(folder, hash + ".json");
        }
    }
}
