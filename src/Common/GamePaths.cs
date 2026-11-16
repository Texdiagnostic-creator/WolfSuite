using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace Wolf
{
    // Locates Steam, the client game, the dedicated server and the per-user 7DTD folders.
    public static class GamePaths
    {
        public const int AppIdClient = 251570;
        public const int AppIdServer = 294420;

        public static string AppData7Dtd
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "7DaysToDie"); }
        }

        public static string SavesDir { get { return Path.Combine(AppData7Dtd, "Saves"); } }
        public static string PresetsDir { get { return Path.Combine(AppData7Dtd, "Presets"); } }
        public static string ClientLogsDir { get { return Path.Combine(AppData7Dtd, "logs"); } }

        public static string ClientExeName { get { return "7DaysToDie.exe"; } }
        public static string ServerExeName { get { return "7DaysToDieServer.exe"; } }

        // All Steam library roots (main folder + mounted libraries from libraryfolders.vdf).
        public static List<string> SteamLibraries()
        {
            List<string> roots = new List<string>();
            string steam = SteamFolder();
            if (!string.IsNullOrEmpty(steam)) { AddRoot(roots, steam); }

            foreach (string vdf in SteamLibraryVdfs(steam))
            {
                foreach (string path in ReadVdfPaths(vdf))
                {
                    string clean = path.Replace("\\\\", "\\").Replace(@"\\", @"\");
                    AddRoot(roots, Path.GetDirectoryName(clean.TrimEnd('\\')) ?? clean);
                    AddRoot(roots, clean);
                }
            }

            // Fallback: common drive letters, a very common place for extra libraries.
            foreach (string drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.RootDirectory.FullName))
            {
                AddRoot(roots, Path.Combine(drive, "SteamLibrary"));
                AddRoot(roots, Path.Combine(drive, "Steam"));
            }
            return roots;
        }

        public static string SteamFolder()
        {
            foreach (string key in new[] { @"SOFTWARE\WOW6432Node\Valve\Steam", @"SOFTWARE\Valve\Steam" })
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                        using (RegistryKey k = baseKey.OpenSubKey(key))
                        {
                            object v = k == null ? null : k.GetValue("InstallPath");
                            if (v != null)
                            {
                                string path = Convert.ToString(v);
                                if (Directory.Exists(path)) { return path; }
                            }
                        }
                    }
                    catch { }
                }
            }
            foreach (string candidate in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                @"C:\Steam",
            })
            {
                if (Directory.Exists(candidate)) { return candidate; }
            }
            return null;
        }

        public static string SteamExe()
        {
            string steam = SteamFolder();
            if (string.IsNullOrEmpty(steam)) { return null; }
            string exe = Path.Combine(steam, "steam.exe");
            return File.Exists(exe) ? exe : null;
        }

        // steamcmd.exe: next to the launcher, in the Steam folder, or anywhere on PATH-like known places.
        public static string FindSteamCmd(string hint)
        {
            List<string> candidates = new List<string>();
            if (!string.IsNullOrEmpty(hint)) { candidates.Add(hint); }
            candidates.Add(Path.Combine(AppDir, "steamcmd.exe"));
            string steam = SteamFolder();
            if (!string.IsNullOrEmpty(steam))
            {
                candidates.Add(Path.Combine(steam, "steamcmd.exe"));
                candidates.Add(Path.Combine(steam, "steamcmd", "steamcmd.exe"));
            }
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Сервер 7", "steamcmd.exe"));
            foreach (string drive in new[] { "C:", "D:", "E:" })
            {
                candidates.Add(Path.Combine(drive, @"SteamCMD\steamcmd.exe"));
                candidates.Add(Path.Combine(drive, @"steamcmd\steamcmd.exe"));
            }
            foreach (string c in candidates)
            {
                if (!string.IsNullOrEmpty(c) && File.Exists(c)) { return Path.GetFullPath(c); }
            }
            return null;
        }

        // Root folder of an installed app (client 251570 / server 294420), matched by folder name.
        public static string FindAppRoot(bool dedicated, string hint)
        {
            List<string> candidates = new List<string>();
            if (!string.IsNullOrEmpty(hint)) { candidates.Add(hint); }

            string clientName = "7 Days To Die";
            string serverName = "7 Days to Die Dedicated Server";

            foreach (string lib in SteamLibraries())
            {
                string common = Path.Combine(lib, "steamapps", "common");
                if (!Directory.Exists(common)) { continue; }
                foreach (string dir in SafeDirs(common))
                {
                    string name = Path.GetFileName(dir);
                    if (string.Equals(name, clientName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, serverName, StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(dir);
                    }
                }
            }

            foreach (string c in candidates)
            {
                if (string.IsNullOrEmpty(c)) { continue; }
                string exe = dedicated ? Path.Combine(c, ServerExeName) : Path.Combine(c, ClientExeName);
                if (File.Exists(exe)) { return Path.GetFullPath(c); }
            }
            return null;
        }

        public static string ClientRoot(string hint) { return FindAppRoot(false, hint); }
        public static string ServerRoot(string hint) { return FindAppRoot(true, hint); }

        public static string ClientModsDir(string gameRoot) { return Path.Combine(gameRoot ?? "", "Mods"); }
        public static string ServerModsDir(string serverRoot) { return Path.Combine(serverRoot ?? "", "7DaysToDieServer_Data", "Mods"); }
        public static string ServerConfig(string serverRoot) { return Path.Combine(serverRoot ?? "", "serverconfig.xml"); }

        // serverdata folder of the dedicated server holds Saves/ and controlpanel.json.
        public static string ServerDataDir(string serverRoot) { return Path.Combine(serverRoot ?? "", "7DaysToDieServer_Data", "serverdata"); }
        public static string ServerSavesDir(string serverRoot) { return Path.Combine(ServerDataDir(serverRoot), "Saves"); }

        public static string AppDir
        {
            get
            {
                try { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }
                catch { return Directory.GetCurrentDirectory(); }
            }
        }

        // Per-app settings folder, e.g. %APPDATA%\WolfLauncher or %APPDATA%\WolfServer.
        public static string SettingsDir(string appName)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appName);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static List<string> Worlds(string savesDir)
        {
            List<string> result = new List<string>();
            if (!Directory.Exists(savesDir)) { return result; }
            foreach (string dir in SafeDirs(savesDir))
            {
                string name = Path.GetFileName(dir);
                if (name.Equals("backups", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("backup", StringComparison.OrdinalIgnoreCase)) { continue; }
                if (LooksLikeWorld(dir)) { result.Add(name); }
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        // Настоящее дерево сохранений выделенного сервера двухуровневое:
        //   Saves\<Мир>\<Сохранение>\main.ttw
        // (обычно при GameName=Game2 и UserDataFolder вне папки сервера). Поэтому мир —
        // это папка, которая либо сама содержит файлы мира, либо содержит папку сохранения
        // с ними. Раньше проверялись только файлы верхнего уровня, и на реальном сервере
        // список миров всегда был пустым: «не найдено ни одного мира» при бэкапе.
        private static bool LooksLikeWorld(string dir)
        {
            if (HasWorldFiles(dir)) { return true; }
            foreach (string sub in SafeDirs(dir))
            {
                if (HasWorldFiles(sub)) { return true; }
            }
            return false;
        }

        private static bool HasWorldFiles(string dir)
        {
            // A real world folder carries main.ttw / world.env / playermap.ttw.
            return SafeFiles(dir).Any(f => f.EndsWith(".ttw", StringComparison.OrdinalIgnoreCase) ||
                                           f.EndsWith(".sdf", StringComparison.OrdinalIgnoreCase) ||
                                           f.EndsWith("world.env", StringComparison.OrdinalIgnoreCase) ||
                                           f.Equals("main.ttw", StringComparison.OrdinalIgnoreCase));
        }

        // ---------- small helpers reused across the suite ----------

        public static IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : Enumerable.Empty<string>(); }
            catch { return Enumerable.Empty<string>(); }
        }

        public static IEnumerable<string> SafeFiles(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.GetFiles(dir) : Enumerable.Empty<string>(); }
            catch { return Enumerable.Empty<string>(); }
        }

        public static IEnumerable<string> SafeFiles(string dir, string pattern, SearchOption option)
        {
            try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern, option) : Enumerable.Empty<string>(); }
            catch { return Enumerable.Empty<string>(); }
        }

        public static string SafeName(string raw, string fallback)
        {
            if (string.IsNullOrWhiteSpace(raw)) { return fallback; }
            StringBuilder sb = new StringBuilder();
            foreach (char c in raw)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            }
            string result = sb.ToString().Trim('_');
            return string.IsNullOrEmpty(result) ? fallback : result;
        }

        public static string HumanSize(long bytes)
        {
            string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return (u == 0 ? v.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                           : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)) + " " + units[u];
        }

        private static void AddRoot(List<string> roots, string path)
        {
            if (string.IsNullOrEmpty(path)) { return; }
            if (!Directory.Exists(path)) { return; }
            string full;
            try { full = Path.GetFullPath(path).TrimEnd('\\'); }
            catch { return; }
            if (!roots.Any(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase))) { roots.Add(full); }
        }

        private static IEnumerable<string> SteamLibraryVdfs(string steamFolder)
        {
            List<string> result = new List<string>();
            if (!string.IsNullOrEmpty(steamFolder))
            {
                string vdf = Path.Combine(steamFolder, "config", "libraryfolders.vdf");
                if (File.Exists(vdf)) { result.Add(vdf); }
            }
            string sf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "config", "libraryfolders.vdf");
            if (File.Exists(sf) && !result.Any(p => string.Equals(p, sf, StringComparison.OrdinalIgnoreCase))) { result.Add(sf); }
            return result;
        }

        // Reads every "path" "..." line out of a Steam .vdf file.
        private static List<string> ReadVdfPaths(string vdf)
        {
            List<string> paths = new List<string>();
            try
            {
                foreach (string line in File.ReadAllLines(vdf))
                {
                    string t = line.Trim();
                    if (!t.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) { continue; }
                    int first = t.IndexOf('"', 6);
                    if (first < 0) { continue; }
                    int second = t.IndexOf('"', first + 1);
                    if (second < 0) { continue; }
                    string value = t.Substring(first + 1, second - first - 1);
                    if (!string.IsNullOrWhiteSpace(value)) { paths.Add(value); }
                }
            }
            catch { }
            return paths;
        }
    }
}
