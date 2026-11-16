using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Wolf
{
    // Which mod folders and files may be handed out to players.
    //
    // The pack published here must match what ModSync already distributes byte for byte,
    // otherwise Zipp0o's-AntiCheat kicks players: it hashes every .dll/.unity3d/.bundle/.assets
    // inside Mods and compares the client list with the server list. So the rules are not
    // duplicated by hand - they are read from the two files that already define them:
    //   C:\ProgramData\ModSyncServer\config.json   -> exclude_patterns
    //   Mods\Zipp0o's-AntiCheat\AntiCheatConfig.xml -> ServerOnlyMods
    public sealed class ModFilters
    {
        private readonly HashSet<string> excludedMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Regex> excludedFiles = new List<Regex>();
        private readonly List<string> sources = new List<string>();

        public IList<string> Sources { get { return sources; } }
        public IEnumerable<string> ExcludedMods { get { return excludedMods.OrderBy(s => s, StringComparer.OrdinalIgnoreCase); } }

        // Rules that are always applied: the launcher's own staging folders and OS junk.
        public static readonly string[] AlwaysExcludedDirs = { "disabled_mods", "Config", ".git", "obj", "bin", ".vs", ".idea" };

        public bool IsModExcluded(string modFolderName)
        {
            if (string.IsNullOrEmpty(modFolderName)) { return true; }
            if (excludedMods.Contains(modFolderName)) { return true; }
            if (modFolderName.StartsWith(".", StringComparison.Ordinal)) { return true; }
            foreach (string d in AlwaysExcludedDirs)
            {
                if (string.Equals(modFolderName, d, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        // rel is "ModName/sub/file.ext" with forward slashes.
        public bool IsFileExcluded(string rel)
        {
            string norm = "/" + rel.Replace('\\', '/');
            foreach (Regex r in excludedFiles)
            {
                if (r.IsMatch(norm)) { return true; }
            }
            string name = Path.GetFileName(rel);
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".wolf-tmp", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            // Hidden files and VCS leftovers (.gitignore, .github/..., .gitattributes) are not
            // shipped: ModSync leaves them out too, and the pack must stay byte-identical.
            if (name.StartsWith(".", StringComparison.Ordinal)) { return true; }
            foreach (string part in norm.Split('/'))
            {
                if (part.StartsWith(".git", StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        public void ExcludeMod(string name)
        {
            if (!string.IsNullOrWhiteSpace(name)) { excludedMods.Add(name.Trim()); }
        }

        public void AddFilePattern(string glob)
        {
            if (string.IsNullOrWhiteSpace(glob)) { return; }
            string pattern = glob.Trim().Replace('\\', '/');
            if (pattern.StartsWith("/")) { pattern = pattern.Substring(1); }
            // A bare folder name ("ChunkKeepAlive/*") must match at any depth.
            string regex = "^/?" + GlobToRegex(pattern);
            excludedFiles.Add(new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.Compiled));
        }

        private static string GlobToRegex(string glob)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < glob.Length; i++)
            {
                char c = glob[i];
                if (c == '*')
                {
                    // "**/" -> any depth, "*/" -> one level, "*" -> anything but a slash
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        sb.Append(".*");
                        i++;
                        if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }
                }
                else if (c == '?') { sb.Append("[^/]"); }
                else { sb.Append(Regex.Escape(c.ToString())); }
            }
            return sb.ToString();
        }

        // ---------- loaders ----------

        public bool LoadModSyncConfig(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return false; }
            try
            {
                Dictionary<string, object> cfg = Json.ReadFile(path);
                foreach (object o in Json.Arr(cfg, "exclude_patterns"))
                {
                    string pattern = Convert.ToString(o);
                    if (string.IsNullOrWhiteSpace(pattern)) { continue; }
                    AddFilePattern(pattern);
                    // "ChunkKeepAlive/*" also means the whole folder stays on the server.
                    string trimmed = pattern.Trim();
                    if (trimmed.EndsWith("/*", StringComparison.Ordinal) && trimmed.IndexOf('/') == trimmed.Length - 2)
                    {
                        ExcludeMod(trimmed.Substring(0, trimmed.Length - 2));
                    }
                }
                sources.Add("ModSync: " + Path.GetFileName(path) + " (" + Json.Arr(cfg, "exclude_patterns").Count + " правил)");
                return true;
            }
            catch (Exception ex)
            {
                sources.Add("ModSync: ошибка чтения " + ex.Message);
                return false;
            }
        }

        public bool LoadAntiCheatConfig(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return false; }
            try
            {
                System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
                doc.Load(path);
                System.Xml.XmlNode node = doc.SelectSingleNode("//property[@name='ServerOnlyMods']");
                if (node == null) { return false; }
                string value = node.Attributes["value"] == null ? "" : node.Attributes["value"].Value;
                int added = 0;
                foreach (string part in value.Split(','))
                {
                    string name = part.Trim();
                    if (name.Length == 0) { continue; }
                    if (excludedMods.Add(name)) { added++; }
                }
                sources.Add("ZAC ServerOnlyMods: " + added + " модов");
                return true;
            }
            catch (Exception ex)
            {
                sources.Add("ZAC: ошибка чтения " + ex.Message);
                return false;
            }
        }

        // Convenience: the rules of this server.
        //
        // useServerOnlyList=false keeps the pack byte-for-byte identical to what ModSync already
        // hands out (it ships every folder except exclude_patterns). That is what we want while
        // players migrate from ModSync to WolfLauncher: nothing is re-downloaded, nothing breaks.
        // Turning it on additionally hides the anti-cheat ServerOnlyMods folders from the pack.
        public static ModFilters ForThisServer(string modsDir)
        {
            return ForThisServer(modsDir, false);
        }

        public static ModFilters ForThisServer(string modsDir, bool useServerOnlyList)
        {
            ModFilters f = new ModFilters();
            f.LoadModSyncConfig(@"C:\ProgramData\ModSyncServer\config.json");
            if (useServerOnlyList && !string.IsNullOrEmpty(modsDir))
            {
                f.LoadAntiCheatConfig(Path.Combine(modsDir, "Zipp0o's-AntiCheat", "AntiCheatConfig.xml"));
            }
            f.LoadExtraFileList(Path.Combine(GamePaths.SettingsDir("WolfServer"), "exclude.txt"));
            return f;
        }

        // Plain text file, one mod folder name per line; lets the admin hide a folder from the
        // pack without touching ModSync or the anti-cheat config.
        public bool LoadExtraFileList(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return false; }
            int added = 0;
            try
            {
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) { continue; }
                    if (line.Contains("*") || line.Contains("?")) { AddFilePattern(line); }
                    else if (excludedMods.Add(line)) { added++; }
                }
                if (added > 0) { sources.Add("exclude.txt: " + added + " модов"); }
                return true;
            }
            catch { return false; }
        }
    }
}
