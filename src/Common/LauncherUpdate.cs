using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Wolf
{
    // Self-update for the launcher. The server publishes its current WolfLauncher.exe as
    // /launcher.exe plus /launcher.json (size + sha256). The client compares that with its own
    // file and, when they differ, downloads the new build next to itself and swaps it in place
    // when the launcher closes.
    //
    // The swap deliberately avoids spawning anything. An earlier version wrote a .cmd file to
        // %TEMP% and ran it through cmd.exe to move the file after the process exited; that is the
        // pattern malware droppers use, and Windows Defender blocks the launcher on sight because
        // of it. Windows does allow renaming a running executable, only overwriting it is denied,
        // so the swap is done from inside the process: WolfLauncher.exe -> <temp>\WolfLauncher.old.exe,
        // WolfLauncher.new.exe -> WolfLauncher.exe. The old build goes to %TEMP% and not next to the
        // launcher: a running exe cannot be deleted, only renamed, and a leftover "WolfLauncher.exe.old"
        // next to the launcher looked like a stray file to players. %TEMP% cleans itself up.
        public static class LauncherUpdate
        {
            public const string FileName = "WolfLauncher.exe";
            public const string StagingName = "WolfLauncher.new.exe";
            public const string OldSuffix = ".old";

        public sealed class Info
        {
            public long size;
            public string sha = "";
            public string version = "";
            public string note = "";
            public string url = "";
        }

        public static Info Fetch(string baseUrl, int timeoutMs)
        {
            string text;
            if (!Http.TryGet(baseUrl.TrimEnd('/') + "/launcher.json", timeoutMs, out text)) { return null; }
            try
            {
                Dictionary<string, object> d = Json.ParseObject(text);
                Info info = new Info();
                info.size = Json.Long(d, "size", 0);
                info.sha = Json.Str(d, "sha256", "");
                info.version = Json.Str(d, "version", "");
                info.note = Json.Str(d, "note", "");
                info.url = baseUrl.TrimEnd('/') + "/launcher.exe";
                return info.size > 0 && info.sha.Length == 64 ? info : null;
            }
            catch { return null; }
        }

        public static string CurrentExePath()
        {
            try
            {
                Assembly entry = Assembly.GetEntryAssembly();
                return entry == null ? null : entry.Location;
            }
            catch { return null; }
        }

        public static string CurrentVersion()
        {
            try
            {
                Version v = Assembly.GetEntryAssembly().GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
            catch { return "0.0.0"; }
        }

        // True when the server ships a build other than the one running. Comparing only the
        // file size was not enough: two builds can have the same length, and then players
        // never received the update. So the version from the file metadata is compared first,
        // and the published sha256 is used when the versions are equal.
        public static bool IsNewer(Info info, string exePath)
        {
            if (info == null || string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) { return false; }
            try
            {
                if (new FileInfo(exePath).Length == info.size) { return false; }
                Version local, remote;
                if (!TryParseVersion(FileVersionOf(exePath), out local)) { return true; }
                if (!TryParseVersion(info.version, out remote)) { return true; }
                if (remote != local) { return remote > local; }
                // Версии одинаковые (пересборка того же номера) — сверяем содержимое.
                return !string.Equals(ChunkStore.HashOfFile(exePath), info.sha, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string FileVersionOf(string exePath)
        {
            try { return System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath).FileVersion; }
            catch { return ""; }
        }

        private static bool TryParseVersion(string text, out Version version)
        {
            version = null;
            if (string.IsNullOrEmpty(text)) { return false; }
            text = text.Trim();
            string[] parts = text.Split('.');
            if (parts.Length > 4) { text = string.Join(".", parts, 0, 4); }
            return Version.TryParse(text, out version);
        }

        // Downloads and verifies the new build. Returns the staging path, or null.
        public static string Download(Info info, string exePath, Action<string> log, int timeoutMs)
        {
            if (info == null || string.IsNullOrEmpty(exePath)) { return null; }
            string staging = Path.Combine(Path.GetDirectoryName(exePath), StagingName);
            try
            {
                byte[] data = Http.GetBytes(info.url, timeoutMs, null);
                if (data == null || data.LongLength != info.size)
                {
                    if (log != null) { log("самообновление: размер не совпал (" + (data == null ? 0 : data.LongLength) + " вместо " + info.size + ")"); }
                    return null;
                }
                if (!string.Equals(ChunkStore.HashOf(data), info.sha, StringComparison.OrdinalIgnoreCase))
                {
                    if (log != null) { log("самообновление: хэш не совпал"); }
                    return null;
                }
                if (File.Exists(staging)) { File.Delete(staging); }
                File.WriteAllBytes(staging, data);
                return staging;
            }
            catch (Exception ex)
            {
                if (log != null) { log("самообновление: " + ex.Message + " (" + info.url + ")"); }
                return null;
            }
        }

        // Swaps the staged build in place. Called while the launcher is closing, so the window
        // is already gone and no restart is needed: the next launch picks up the new file.
        // Renaming the running exe is allowed; overwriting it is not, hence the two moves.
        public static bool ApplyStaged(string exePath, string staging, Action<string> log)
        {
            if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(staging)) { return false; }
            if (!File.Exists(exePath) || !File.Exists(staging)) { return false; }
            string oldPath = OldCopyPath(exePath);
            try
            {
                // Копию прошлой версии уносим в %TEMP%: рядом с лаунчером игрок видел лишний файл.
                TryDelete(oldPath);
                string oldDir = Path.GetDirectoryName(oldPath);
                if (!string.IsNullOrEmpty(oldDir) && !Directory.Exists(oldDir)) { Directory.CreateDirectory(oldDir); }
                File.Move(exePath, oldPath);
                try
                {
                    File.Move(staging, exePath);
                }
                catch
                {
                    // Put the working build back rather than leaving no launcher at all.
                    try { File.Move(oldPath, exePath); } catch { }
                    throw;
                }
                if (log != null) { log("самообновление: новая версия установлена, она запустится в следующий раз"); }
                // Explorer кэширует значок по пути файла: без уведомления игрок видит
                // старую картинку (или пустую страницу) у уже обновлённого exe.
                ShellNotify.RefreshFile(exePath);
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) { log("самообновление: не удалось заменить файл — " + ex.Message); }
                return false;
            }
        }

        // Куда убирается прошлая версия при замене файла.
        private static string OldCopyPath(string exePath)
        {
            string dir = Path.Combine(Path.GetTempPath(), "WolfLauncher_update");
            string name = Path.GetFileName(exePath);
            if (string.IsNullOrEmpty(name)) { name = FileName; }
            return Path.Combine(dir, name + OldSuffix);
        }

        // Clears leftovers: a staged build from a failed download and the previous exe that
        // could not be deleted while it was still running. Called at startup.
        public static void CleanStaging(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) { return; }
            string dir = Path.GetDirectoryName(exePath);
            try
            {
                string staging = Path.Combine(dir, StagingName);
                if (File.Exists(staging)) { TryDelete(staging); }
            }
            catch { }
            try
            {
                // Копия прошлой версии, которую не удалось стереть в момент замены.
                string oldPath = OldCopyPath(exePath);
                if (File.Exists(oldPath)) { TryDelete(oldPath); }
            }
            catch { }
            try
            {
                // У игроков, обновившихся до 1.5.2, рядом с лаунчером остался WolfLauncher.exe.old.
                string legacy = exePath + OldSuffix;
                if (File.Exists(legacy)) { TryDelete(legacy); }
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
            try { File.Delete(path); } catch { }
        }
    }
}
