using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Wolf
{
    // Backups of Saves\<World> as zip archives with retention (keep last N, keep daily N).
    public sealed class BackupManager
    {
        private readonly string backupRoot;

        public BackupManager(string backupRoot)
        {
            this.backupRoot = backupRoot;
        }

        public string Root { get { return backupRoot; } }

        public int KeepLast = 20;
        public int KeepDays = 7;

        public event Action<string> OnEvent;

        public sealed class BackupItem
        {
            public string World;
            public string File;
            public DateTime CreatedUtc;
            public long Size;
            public string Note;

            public override string ToString()
            {
                return CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) +
                       "  " + GamePaths.HumanSize(Size) + (string.IsNullOrEmpty(Note) ? "" : "  " + Note);
            }
        }

        // Zips every world folder under savesDir into <root>\<World>\<timestamp>.zip.
        public List<string> BackupAll(string savesDir, string note, Action<string> log)
        {
            List<string> created = new List<string>();
            if (!Directory.Exists(savesDir))
            {
                if (log != null) { log("Папка сохранений не найдена: " + savesDir); }
                return created;
            }
            foreach (string world in GamePaths.Worlds(savesDir))
            {
                string zip = BackupWorld(savesDir, world, note, log);
                if (zip != null) { created.Add(zip); }
            }
            return created;
        }

        public string BackupWorld(string savesDir, string world, string note, Action<string> log)
        {
            string source = Path.Combine(savesDir, world);
            if (!Directory.Exists(source)) { return null; }

            string dir = Path.Combine(backupRoot, GamePaths.SafeName(world, "world"));
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            string zip = Path.Combine(dir, stamp + ".zip");
            int dup = 1;
            while (File.Exists(zip))
            {
                zip = Path.Combine(dir, stamp + "_" + dup.ToString(CultureInfo.InvariantCulture) + ".zip");
                dup++;
            }

            try
            {
                if (File.Exists(zip)) { File.Delete(zip); }
                using (FileStream fs = File.Create(zip))
                using (ZipArchive archive = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    // The game holds file handles on the live save; open with sharing and copy what we can.
                    foreach (string file in GamePaths.SafeFiles(source, "*", SearchOption.AllDirectories))
                    {
                        string rel = file.Substring(source.Length).TrimStart('\\', '/').Replace('\\', '/');
                        try
                        {
                            ZipArchiveEntry entry = archive.CreateEntry(rel, CompressionLevel.Optimal);
                            using (Stream dst = entry.Open())
                            using (FileStream src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            {
                                src.CopyTo(dst);
                            }
                        }
                        catch (IOException)
                        {
                            if (log != null) { log("пропущен занятый файл: " + rel); }
                        }
                    }
                    if (!string.IsNullOrEmpty(note))
                    {
                        ZipArchiveEntry noteEntry = archive.CreateEntry("_wolfbackup.txt");
                        using (StreamWriter w = new StreamWriter(noteEntry.Open(), new UTF8Encoding(false)))
                        {
                            w.WriteLine("world=" + world);
                            w.WriteLine("created=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                            w.WriteLine("note=" + note);
                        }
                    }
                }
                if (log != null) { log("бэкап: " + Path.GetFileName(zip) + " (" + GamePaths.HumanSize(new FileInfo(zip).Length) + ")"); }
                Prune();
                return zip;
            }
            catch (Exception ex)
            {
                if (log != null) { log("ошибка бэкапа " + world + ": " + ex.Message); }
                try { if (File.Exists(zip)) { File.Delete(zip); } } catch { }
                return null;
            }
        }

        public List<BackupItem> List(string world)
        {
            List<BackupItem> result = new List<BackupItem>();
            string dir = string.IsNullOrEmpty(world) ? backupRoot : Path.Combine(backupRoot, GamePaths.SafeName(world, "world"));
            if (!Directory.Exists(dir)) { return result; }

            foreach (string f in GamePaths.SafeFiles(dir, "*.zip", SearchOption.AllDirectories))
            {
                try
                {
                    BackupItem item = new BackupItem();
                    item.File = f;
                    // Folder names are sanitised, so the real world name comes from the note
                    // inside the archive; the folder name is only a fallback for old archives.
                    item.World = ReadWorldName(f) ?? Path.GetFileName(Path.GetDirectoryName(f));
                    item.Size = new FileInfo(f).Length;
                    item.CreatedUtc = File.GetLastWriteTimeUtc(f);
                    item.Note = ReadNoteField(f, "note");
                    result.Add(item);
                }
                catch { }
            }
            result.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
            return result;
        }

        private static string ReadWorldName(string zipPath)
        {
            return ReadNoteField(zipPath, "world");
        }

        // Reads one "key=value" line out of the _wolfbackup.txt entry we put into every archive.
        private static string ReadNoteField(string zipPath, string key)
        {
            try
            {
                using (ZipArchive archive = ZipFile.OpenRead(zipPath))
                {
                    ZipArchiveEntry entry = archive.GetEntry("_wolfbackup.txt");
                    if (entry == null) { return null; }
                    using (StreamReader r = new StreamReader(entry.Open(), Encoding.UTF8))
                    {
                        string line;
                        while ((line = r.ReadLine()) != null)
                        {
                            int eq = line.IndexOf('=');
                            if (eq <= 0) { continue; }
                            if (line.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                            {
                                return line.Substring(eq + 1).Trim();
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        // Retention: newest KeepLast per world, plus the newest archive of each of the last KeepDays days.
        public void Prune()
        {
            if (!Directory.Exists(backupRoot)) { return; }
            foreach (string worldDir in GamePaths.SafeDirs(backupRoot))
            {
                List<BackupItem> all = new List<BackupItem>();
                foreach (string f in GamePaths.SafeFiles(worldDir, "*.zip", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        BackupItem i = new BackupItem();
                        i.File = f;
                        i.Size = new FileInfo(f).Length;
                        i.CreatedUtc = File.GetLastWriteTimeUtc(f);
                        all.Add(i);
                    }
                    catch { }
                }
                all.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));

                HashSet<string> keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < all.Count && i < KeepLast; i++) { keep.Add(all[i].File); }

                HashSet<string> days = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (BackupItem item in all)
                {
                    string day = item.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (days.Contains(day)) { continue; }
                    if (days.Count >= KeepDays) { continue; }
                    days.Add(day);
                    keep.Add(item.File);
                }

                foreach (BackupItem item in all)
                {
                    if (keep.Contains(item.File)) { continue; }
                    try
                    {
                        File.Delete(item.File);
                        if (OnEvent != null) { OnEvent("удалён старый бэкап: " + Path.GetFileName(item.File)); }
                    }
                    catch { }
                }
            }
        }

        // Restores a zip over the live save folder. The server must be stopped first.
        public bool Restore(BackupItem item, string savesDir, out string error)
        {
            error = null;
            if (item == null || !File.Exists(item.File)) { error = "архив не найден"; return false; }
            string target = Path.Combine(savesDir, item.World);
            try
            {
                Directory.CreateDirectory(target);
                using (ZipArchive archive = ZipFile.OpenRead(item.File))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.FullName.Equals("_wolfbackup.txt", StringComparison.OrdinalIgnoreCase)) { continue; }
                        string full = Path.GetFullPath(Path.Combine(target, entry.FullName.Replace('/', '\\')));
                        if (!full.StartsWith(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) { continue; }
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(full);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(full));
                        entry.ExtractToFile(full, true);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public void Delete(BackupItem item)
        {
            try { if (item != null && File.Exists(item.File)) { File.Delete(item.File); } } catch { }
        }

        public long TotalSize()
        {
            long total = 0;
            foreach (string f in GamePaths.SafeFiles(backupRoot, "*.zip", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; }
                catch { }
            }
            return total;
        }
    }
}
