using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Wolf
{
    // One <property name="..." value="..."/> line of serverconfig.xml.
    public sealed class ConfigProp
    {
        public string Name;
        public string Value;
        public int LineIndex;
        public string Comment = "";
        public string Key;
    }

    // Line-preserving editor for serverconfig.xml.
    // The file is full of comments that document every option, so it is edited textually:
    // only the value="..." fragment of the touched line changes, everything else stays as is.
    public sealed class ServerConfig
    {
        private readonly List<string> lines = new List<string>();
        private readonly List<ConfigProp> props = new List<ConfigProp>();
        private string path;

        private static readonly Regex PropRegex = new Regex(
            "name\\s*=\\s*\"(?<name>[^\"]*)\"\\s*value\\s*=\\s*\"(?<value>[^\"]*)\"",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public string FilePath { get { return path; } }
        public bool Loaded { get { return path != null && lines.Count > 0; } }
        public IList<ConfigProp> Properties { get { return props; } }

        // Options surfaced in the UI, grouped the way the game groups them.
        public static readonly string[] GeneralKeys =
        {
            "ServerName", "ServerDescription", "ServerPassword", "ServerWebsiteURL",
            "Region", "Language", "ServerLoginConfirmationText",
        };

        public static readonly string[] NetworkKeys =
        {
            "ServerPort", "ServerVisibility", "ServerMaxPlayerCount", "ServerReservedSlots",
            "ServerMaxWorldTransferSpeedKiBps", "ServerMaxWorldTransferSpeedKiBs",
            "ServerDisabledNetworkProtocols", "ServerAllowCrossplay", "EACEnabled",
        };

        public static readonly string[] WorldKeys =
        {
            "GameWorld", "WorldGenSeed", "WorldGenSize", "GameName", "GameMode",
            "PlayerSafeZoneLevel", "PlayerSafeZoneHours", "MaxSpawnedZombies", "MaxSpawnedAnimals",
            "ServerMaxAllowedViewDistance", "LandClaimCount", "PlayerKillingMode",
        };

        public static readonly string[] AdminKeys =
        {
            "TelnetEnabled", "TelnetPort", "TelnetPassword",
            "WebDashboardEnabled", "WebDashboardPort", "EnableMapRendering",
            "TerminalWindowEnabled", "AdminFileName", "SaveDataLimit",
        };

        public bool Load(string file)
        {
            path = file;
            lines.Clear();
            props.Clear();
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) { return false; }

            string[] raw;
            try { raw = File.ReadAllLines(file, Encoding.UTF8); }
            catch { return false; }

            for (int i = 0; i < raw.Length; i++)
            {
                lines.Add(raw[i]);
                Match m = PropRegex.Match(raw[i]);
                if (!m.Success) { continue; }

                // Строка вида <!-- <property name="X" value="..."/> --> — пример из документации,
                // а не настройка. Иначе такой вариант перекрывал бы настоящее значение ниже.
                int comment = raw[i].IndexOf("<!--", StringComparison.Ordinal);
                if (comment >= 0 && comment < m.Index)
                {
                    int commentEnd = raw[i].IndexOf("-->", comment, StringComparison.Ordinal);
                    if (commentEnd < 0 || commentEnd > m.Index + m.Length) { continue; }
                }

                ConfigProp p = new ConfigProp();
                p.Name = m.Groups["name"].Value;
                p.Value = m.Groups["value"].Value;
                p.LineIndex = i;
                p.Key = p.Name.ToLowerInvariant();

                // Keep the trailing <!-- ... --> comment so the UI can show the original hint.
                int c = raw[i].IndexOf("<!--", m.Index + m.Length, StringComparison.Ordinal);
                if (c > 0)
                {
                    string tail = raw[i].Substring(c).Trim();
                    if (tail.StartsWith("<!--", StringComparison.Ordinal)) { tail = tail.Substring(4); }
                    if (tail.EndsWith("-->", StringComparison.Ordinal)) { tail = tail.Substring(0, tail.Length - 3); }
                    p.Comment = tail.Trim();
                }
                props.Add(p);
            }
            return props.Count > 0;
        }

        public ConfigProp Find(string name)
        {
            string key = (name ?? "").ToLowerInvariant();
            foreach (ConfigProp p in props)
            {
                if (p.Key == key) { return p; }
            }
            return null;
        }

        public string Get(string name, string def)
        {
            ConfigProp p = Find(name);
            return p == null ? def : p.Value;
        }

        public int GetInt(string name, int def)
        {
            int v;
            return int.TryParse(Get(name, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        public bool GetBool(string name, bool def)
        {
            string v = (Get(name, "") ?? "").Trim().ToLowerInvariant();
            if (v == "true" || v == "1" || v == "yes") { return true; }
            if (v == "false" || v == "0" || v == "no") { return false; }
            return def;
        }

        // Sets a value; returns true when something actually changed (dirty tracking).
        public bool Set(string name, string value)
        {
            ConfigProp p = Find(name);
            if (p == null) { return false; }
            string clean = value == null ? "" : value;
            if (p.Value == clean) { return false; }
            string line = lines[p.LineIndex];
            Match m = PropRegex.Match(line);
            if (!m.Success) { return false; }
            Group vg = m.Groups["value"];
            lines[p.LineIndex] = line.Substring(0, vg.Index) + clean + line.Substring(vg.Index + vg.Length);
            p.Value = clean;
            return true;
        }

        public bool SetInt(string name, int value)
        {
            return Set(name, value.ToString(CultureInfo.InvariantCulture));
        }

        public bool SetBool(string name, bool value)
        {
            return Set(name, value ? "true" : "false");
        }

        // Writes to disk keeping a one-step .bak of the previous contents.
        public bool Save(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path)) { error = "файл не загружен"; return false; }
            try
            {
                if (File.Exists(path))
                {
                    string bak = path + ".wolfbak";
                    try { File.Copy(path, bak, true); } catch { }
                }
                File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // Validates the few things the game refuses to start with.
        public List<string> Validate()
        {
            List<string> issues = new List<string>();
            if (!Loaded) { issues.Add("serverconfig.xml не загружен"); return issues; }

            string name = Get("ServerName", "");
            if (string.IsNullOrWhiteSpace(name)) { issues.Add("ServerName пустой"); }
            if (name.Length > 60) { issues.Add("ServerName длиннее 60 символов"); }

            int port = GetInt("ServerPort", 0);
            if (port < 1024 || port > 65535) { issues.Add("ServerPort должен быть в диапазоне 1024-65535"); }

            int slots = GetInt("ServerMaxPlayerCount", 0);
            if (slots < 1 || slots > 64) { issues.Add("ServerMaxPlayerCount: допустимо 1-64"); }

            string gameName = Get("GameName", "");
            if (string.IsNullOrWhiteSpace(gameName)) { issues.Add("GameName пустой — сохранение не создастся"); }
            else if (Regex.IsMatch(gameName, "[^A-Za-z0-9_\\-. ]")) { issues.Add("GameName: только латиница, цифры, _ - . и пробел"); }

            string world = Get("GameWorld", "");
            if (string.Equals(world, "RWG", StringComparison.OrdinalIgnoreCase))
            {
                int size = GetInt("WorldGenSize", 6144);
                if (size < 6144 || size > 10240 || size % 2048 != 0)
                {
                    issues.Add("WorldGenSize для RWG: 6144, 8192 или 10240");
                }
            }

            if (GetBool("TelnetEnabled", false))
            {
                int tp = GetInt("TelnetPort", 8081);
                if (tp == port) { issues.Add("TelnetPort совпадает с ServerPort"); }
            }

            return issues;
        }

        // Worlds shipped with the game plus anything already generated locally.
        public static List<string> KnownWorlds(string serverRoot)
        {
            List<string> worlds = new List<string>();
            if (!string.IsNullOrEmpty(serverRoot))
            {
                string dataWorlds = Path.Combine(serverRoot, "Data", "Worlds");
                foreach (string d in GamePaths.SafeDirs(dataWorlds))
                {
                    string n = Path.GetFileName(d);
                    if (!worlds.Contains(n)) { worlds.Add(n); }
                }
                string generated = Path.Combine(serverRoot, "Data", "Worlds");
                foreach (string d in GamePaths.SafeDirs(generated))
                {
                    string n = Path.GetFileName(d);
                    if (!worlds.Contains(n)) { worlds.Add(n); }
                }
            }
            foreach (string d in GamePaths.SafeDirs(Path.Combine(GamePaths.AppData7Dtd, "GeneratedWorlds")))
            {
                string n = Path.GetFileName(d);
                if (!worlds.Contains(n)) { worlds.Add(n); }
            }
            worlds.Sort(StringComparer.OrdinalIgnoreCase);
            return worlds;
        }

        // Where this config actually stores saves: UserDataFolder override or the default.
        public string SavesFolder(string serverRoot)
        {
            string custom = Get("UserDataFolder", "");
            if (!string.IsNullOrWhiteSpace(custom))
            {
                string candidate = custom.Trim();
                if (!System.IO.Path.IsPathRooted(candidate))
                {
                    candidate = System.IO.Path.Combine(serverRoot ?? "", candidate);
                }
                string withSaves = System.IO.Path.Combine(candidate, "Saves");
                if (Directory.Exists(withSaves)) { return withSaves; }
                if (Directory.Exists(candidate) && System.IO.Path.GetFileName(candidate).Equals("Saves", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
                return withSaves;
            }

            // The dedicated server keeps user data next to the executable in these folders.
            if (!string.IsNullOrEmpty(serverRoot))
            {
                string data = System.IO.Path.Combine(serverRoot, "7DaysToDieServer_Data", "serverdata", "Saves");
                if (Directory.Exists(data)) { return data; }
                string userData = System.IO.Path.Combine(serverRoot, "UserData", "Saves");
                if (Directory.Exists(userData)) { return userData; }
            }
            return System.IO.Path.Combine(GamePaths.AppData7Dtd, "Saves");
        }
    }
}
