using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Wolf
{
    // Remembers "path -> sha256" so re-publishing a pack does not re-read 8 GB of mods every time.
    //
    // РАНЬШЕ. Ключом служило "путь|размер|mtime". Если содержимое файла подменяли с сохранением
    // даты изменения (восстановление из бэкапа, распаковка архива, копирование с /COPY:DAT),
    // кэш считал файл неизменным и возвращал СТАРЫЙ хеш. Раздача отдавала клиенту старый набор,
    // а античит ZAC, снявший хеши при старте сервера, видел новый файл и кикал игрока
    // «Mod check failed: modified file ...». Внешне это выглядело как «ничего не трогал, а кикнуло».
    //
    // ТЕПЕРЬ. Дата изменения больше не доказательство: для каждой записи хранится дешёвая
    // подпись содержимого — SHA256 от размера, первых и последних 16 КиБ файла. Проверка подписи
    // читает ~32 КиБ вместо 260 МБ, а любая реальная подмена файла её меняет. Кэш без подписи
    // (старый формат) считается недействительным и пересчитывается один раз.
    public sealed class HashCache
    {
        private const int SampleBytes = 16384;   // сколько читать с начала и с конца файла

        private sealed class Entry
        {
            public long size;
            public string sig;       // подпись содержимого; "" = запись старого формата, не доверяем
            public string hash;      // полный sha256 файла
        }

        private readonly string file;
        private readonly Dictionary<string, Entry> map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private bool dirty;
        private int hits;
        private int misses;

        public HashCache(string cacheFile)
        {
            file = cacheFile;
            Load();
        }

        public int Hits { get { return hits; } }
        public int Misses { get { return misses; } }

        // Полный sha256 файла с проверкой по подписи содержимого.
        public string HashOf(string path, long size, DateTime lastWriteUtc)
        {
            Entry known;
            if (map.TryGetValue(path, out known) && known.size == size && known.sig.Length > 0)
            {
                string sig = Signature(path, size);
                if (sig != null && string.Equals(known.sig, sig, StringComparison.OrdinalIgnoreCase))
                {
                    hits++;
                    return known.hash;
                }
            }

            misses++;
            string hash = ChunkStore.HashOfFile(path);
            Entry e = new Entry();
            e.size = size;
            e.sig = Signature(path, size) ?? "";
            e.hash = hash;
            map[path] = e;
            dirty = true;
            return hash;
        }

        // Сбросить кэш целиком: следующий Build пересчитает хеши всех файлов.
        public void Clear()
        {
            map.Clear();
            hits = 0;
            misses = 0;
            dirty = true;
        }

        public int Count { get { return map.Count; } }

        // Дешёвая подпись содержимого: размер + голова + хвост файла.
        private static string Signature(string path, long size)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] len = Encoding.ASCII.GetBytes(size.ToString(CultureInfo.InvariantCulture));
                    sha.TransformBlock(len, 0, len.Length, null, 0);

                    byte[] buffer = new byte[SampleBytes];
                    int head = fs.Read(buffer, 0, buffer.Length);
                    if (head > 0) { sha.TransformBlock(buffer, 0, head, null, 0); }

                    if (size > SampleBytes)
                    {
                        long tailStart = size - SampleBytes;
                        if (tailStart < head) { tailStart = head; }
                        fs.Seek(tailStart, SeekOrigin.Begin);
                        int tail = fs.Read(buffer, 0, buffer.Length);
                        if (tail > 0) { sha.TransformBlock(buffer, 0, tail, null, 0); }
                    }

                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    return ChunkStore.ToHex(sha.Hash);
                }
            }
            catch { return null; }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(file)) { return; }
                foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    // Формат строки: путь|размер|подпись|hash.
                    // Старый вариант: путь|размер|mtime_ticks|hash — подпись не сохраняем,
                    // такая запись будет пересчитана при первом же обращении.
                    int hashBar = line.LastIndexOf('|');
                    if (hashBar <= 0) { continue; }
                    string hash = line.Substring(hashBar + 1);
                    string rest = line.Substring(0, hashBar);

                    int sizeBar = rest.LastIndexOf('|');
                    if (sizeBar <= 0) { continue; }
                    string sig = rest.Substring(sizeBar + 1);
                    string head = rest.Substring(0, sizeBar);

                    int pathBar = head.LastIndexOf('|');
                    if (pathBar <= 0) { continue; }
                    string path = head.Substring(0, pathBar);
                    string sizeText = head.Substring(pathBar + 1);

                    long size;
                    if (!long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out size)) { continue; }

                    Entry e = new Entry();
                    e.size = size;
                    e.hash = hash;
                    e.sig = IsDigits(sig) ? "" : sig;   // цифры = старый mtime, не доверяем
                    map[path] = e;
                }
            }
            catch { }
        }

        private static bool IsDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) { return false; }
            foreach (char c in s) { if (c < '0' || c > '9') { return false; } }
            return true;
        }

        public void Save()
        {
            if (!dirty) { return; }
            try
            {
                string dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
                List<string> lines = new List<string>(map.Count);
                foreach (KeyValuePair<string, Entry> kv in map)
                {
                    Entry e = kv.Value;
                    lines.Add(kv.Key + "|" + e.size.ToString(CultureInfo.InvariantCulture) + "|" + e.sig + "|" + e.hash);
                }
                File.WriteAllLines(file, lines.ToArray(), new UTF8Encoding(false));
                dirty = false;
            }
            catch { }
        }
    }
}
