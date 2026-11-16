using System;
using System.Collections.Generic;
using System.IO;

namespace Wolf.Launcher
{
    // Клиентский кэш хешей: относительный путь -> размер, время изменения и sha256.
    // Глубокая сверка иначе перечитывала бы все моды (около 9 ГБ) при каждом запуске игры;
    // с кэшем хешируются только файлы, у которых изменился размер или дата изменения.
    public sealed class LocalHashCache
    {
        private sealed class Entry
        {
            public long size;
            public long ticks;
            public string hash = "";
        }

        private readonly string file;
        private readonly Dictionary<string, Entry> map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private bool dirty;

        public LocalHashCache(string cacheFile)
        {
            file = cacheFile;
            Load();
        }

        public int Count { get { return map.Count; } }

        public string HashOf(string rel, string fullPath, long size, DateTime mtimeUtc)
        {
            long ticks = mtimeUtc.ToUniversalTime().Ticks;
            Entry known;
            if (map.TryGetValue(rel, out known) && known.size == size && known.ticks == ticks &&
                !string.IsNullOrEmpty(known.hash))
            {
                return known.hash;
            }
            string hash = ChunkStore.HashOfFile(fullPath);
            Set(rel, size, ticks, hash);
            return hash;
        }

        public void Set(string rel, long size, long ticks, string hash)
        {
            Entry e = new Entry();
            e.size = size;
            e.ticks = ticks;
            e.hash = hash ?? "";
            map[rel] = e;
            dirty = true;
        }

        // Запоминаем хеш файла, который только что собрали из хранилища чанков.
        public void Remember(string rel, string fullPath, string hash)
        {
            try
            {
                FileInfo fi = new FileInfo(fullPath);
                if (!fi.Exists) { return; }
                Set(rel, fi.Length, fi.LastWriteTimeUtc.Ticks, hash);
            }
            catch { }
        }

        // Забыть файл (например, после его удаления): следующий раз он будет хеширован заново.
        public void Forget(string rel)
        {
            if (map.Remove(rel)) { dirty = true; }
        }

        // Выкидываем записи о файлах, которых больше нет в наборе сервера.
        public void Prune(HashSet<string> keep)
        {
            List<string> dead = new List<string>();
            foreach (string key in map.Keys)
            {
                if (!keep.Contains(key)) { dead.Add(key); }
            }
            foreach (string key in dead) { map.Remove(key); dirty = true; }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(file)) { return; }
                Dictionary<string, object> root = Json.ReadFile(file);
                foreach (KeyValuePair<string, object> kv in Json.Obj(root, "files"))
                {
                    Dictionary<string, object> d = kv.Value as Dictionary<string, object>;
                    if (d == null) { continue; }
                    Entry e = new Entry();
                    e.size = Json.Long(d, "s", 0);
                    e.ticks = Json.Long(d, "t", 0);
                    e.hash = Json.Str(d, "h", "");
                    if (e.hash.Length > 0) { map[kv.Key] = e; }
                }
            }
            catch { }
        }

        public void Save()
        {
            if (!dirty) { return; }
            try
            {
                Dictionary<string, object> files = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, Entry> kv in map)
                {
                    Dictionary<string, object> d = new Dictionary<string, object>();
                    d["s"] = kv.Value.size;
                    d["t"] = kv.Value.ticks;
                    d["h"] = kv.Value.hash;
                    files[kv.Key] = d;
                }
                Dictionary<string, object> root = new Dictionary<string, object>();
                root["files"] = files;
                Json.WriteFile(file, root, false);
                dirty = false;
            }
            catch { }
        }
    }
}
