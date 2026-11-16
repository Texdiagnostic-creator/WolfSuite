using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace Wolf.Server
{
    // Persisted settings of the server tool.
    public sealed class ServerSettings
    {
        public string ServerRoot = "";
        public string SteamCmd = "";
        public string ConfigFile = "";
        public string ModsSource = "";          // folder the admin curates (may be outside the server)
        public string PackName = "Wolf Server";
        public string PackVersion = "1";
        public string PackNote = "";
        public int PublishPort = 8775;          // HTTP: manifest + chunks + tracker (8765 belongs to ModSync)
        public int PeerPort = 6894;             // TCP: chunk exchange between peers
        public string TelnetHost = "127.0.0.1";
        public int TelnetPort = 48153;
        public string TelnetPassword = "";
        // Консоль рвётся при каждом перезапуске сервера, поэтому по умолчанию сторож сам
        // переподключается и вкладка «Чат» остаётся живой.
        public bool TelnetAutoReconnect = true;
        public int TelnetReconnectSeconds = 5;
        public bool AutoRestart = true;
        public bool BackupBeforeRestart = true;
        public int BackupKeepLast = 20;
        public int BackupKeepDays = 7;
        public string BackupFolder = "";
        public bool WatchOnlyProblems = false;
        public string VkUrl = "https://vk.ru/7daystodieworld";
        public string AdminSteamId = "";
        // Ship the anti-cheat ServerOnlyMods folders too. Off by default: while players move
        // from ModSync to WolfLauncher the pack must match what they already have.
        public bool ShipServerOnlyMods = false;
        public bool AutoPublish = true;         // republish when the Mods folder changes
        public int AutoPublishDelaySec = 30;
        public bool ServeOnStart = true;        // start the HTTP distribution with the tool
        public string LauncherPath = "";        // WolfLauncher.exe handed out at /download
        // Скрипт запуска сервера. Пусто — ищем start_server1.bat рядом с сервером.
        // Через него сервер получает affinity, приоритет High и цикл перезапуска,
        // которых нет при прямом запуске 7DaysToDieServer.exe.
        public string LaunchScript = "";
        public bool LaunchViaScript = true;     // false — прямой запуск exe (аварийный вариант)

        // Радио-прокси мода ZZZ_OnlineRadio: /radio/<индекс>.ogg.
        // WolfServer сам тянет поток станции и перекодирует его ffmpeg в OGG/Vorbis,
        // потому что Unity-клиент умеет стримить только Vorbis.
        public bool RadioEnabled = true;
        public string RadioFfmpeg = "";
        public int RadioMaxClients = 16;

        // Notifications (all optional): Telegram and/or VK messages about joins, published
        // packs and the server state. Empty token or chat/peer id disables that channel.
        public string TelegramToken = "";
        public string TelegramChat = "";
        public string VkToken = "";
        public string VkPeer = "";
        public string NotifyVkVersion = "5.199";
        public bool NotifyJoin = true;
        public bool NotifyPack = true;
        public bool NotifyServer = true;

        public static ServerSettings Load(string file)
        {
            Dictionary<string, object> d = Json.ReadFile(file);
            ServerSettings s = new ServerSettings();
            s.ServerRoot = Json.Str(d, "serverRoot", s.ServerRoot);
            s.SteamCmd = Json.Str(d, "steamcmd", s.SteamCmd);
            s.ConfigFile = Json.Str(d, "configFile", s.ConfigFile);
            s.ModsSource = Json.Str(d, "modsSource", s.ModsSource);
            s.PackName = Json.Str(d, "packName", s.PackName);
            s.PackVersion = Json.Str(d, "packVersion", s.PackVersion);
            s.PackNote = Json.Str(d, "packNote", s.PackNote);
            s.PublishPort = Json.Int(d, "publishPort", s.PublishPort);
            s.PeerPort = Json.Int(d, "peerPort", s.PeerPort);
            s.TelnetHost = Json.Str(d, "telnetHost", s.TelnetHost);
            s.TelnetPort = Json.Int(d, "telnetPort", s.TelnetPort);
            s.TelnetPassword = Json.Str(d, "telnetPassword", s.TelnetPassword);
            s.TelnetAutoReconnect = Json.Bool(d, "telnetAutoReconnect", s.TelnetAutoReconnect);
            s.TelnetReconnectSeconds = Json.Int(d, "telnetReconnectSeconds", s.TelnetReconnectSeconds);
            s.AutoRestart = Json.Bool(d, "autoRestart", s.AutoRestart);
            s.BackupBeforeRestart = Json.Bool(d, "backupBeforeRestart", s.BackupBeforeRestart);
            s.BackupKeepLast = Json.Int(d, "backupKeepLast", s.BackupKeepLast);
            s.BackupKeepDays = Json.Int(d, "backupKeepDays", s.BackupKeepDays);
            s.BackupFolder = Json.Str(d, "backupFolder", s.BackupFolder);
            s.WatchOnlyProblems = Json.Bool(d, "watchOnlyProblems", s.WatchOnlyProblems);
            s.VkUrl = Json.Str(d, "vkUrl", s.VkUrl);
            s.AdminSteamId = Json.Str(d, "adminSteamId", s.AdminSteamId);
            s.ShipServerOnlyMods = Json.Bool(d, "shipServerOnlyMods", s.ShipServerOnlyMods);
            s.AutoPublish = Json.Bool(d, "autoPublish", s.AutoPublish);
            s.AutoPublishDelaySec = Json.Int(d, "autoPublishDelaySec", s.AutoPublishDelaySec);
            s.ServeOnStart = Json.Bool(d, "serveOnStart", s.ServeOnStart);
            s.LauncherPath = Json.Str(d, "launcherPath", s.LauncherPath);
            s.LaunchScript = Json.Str(d, "launchScript", s.LaunchScript);
            s.LaunchViaScript = Json.Bool(d, "launchViaScript", s.LaunchViaScript);
            s.RadioEnabled = Json.Bool(d, "radioEnabled", s.RadioEnabled);
            s.RadioFfmpeg = Json.Str(d, "radioFfmpeg", s.RadioFfmpeg);
            s.RadioMaxClients = Json.Int(d, "radioMaxClients", s.RadioMaxClients);
            s.TelegramToken = Json.Str(d, "telegramToken", s.TelegramToken);
            s.TelegramChat = Json.Str(d, "telegramChat", s.TelegramChat);
            s.VkToken = Json.Str(d, "vkToken", s.VkToken);
            s.VkPeer = Json.Str(d, "vkPeer", s.VkPeer);
            s.NotifyVkVersion = Json.Str(d, "notifyVkVersion", s.NotifyVkVersion);
            s.NotifyJoin = Json.Bool(d, "notifyJoin", s.NotifyJoin);
            s.NotifyPack = Json.Bool(d, "notifyPack", s.NotifyPack);
            s.NotifyServer = Json.Bool(d, "notifyServer", s.NotifyServer);
            return s;
        }

        public void Save(string file)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["serverRoot"] = ServerRoot;
            d["steamcmd"] = SteamCmd;
            d["configFile"] = ConfigFile;
            d["modsSource"] = ModsSource;
            d["packName"] = PackName;
            d["packVersion"] = PackVersion;
            d["packNote"] = PackNote;
            d["publishPort"] = PublishPort;
            d["peerPort"] = PeerPort;
            d["telnetHost"] = TelnetHost;
            d["telnetPort"] = TelnetPort;
            d["telnetPassword"] = TelnetPassword;
            d["telnetAutoReconnect"] = TelnetAutoReconnect;
            d["telnetReconnectSeconds"] = TelnetReconnectSeconds;
            d["autoRestart"] = AutoRestart;
            d["backupBeforeRestart"] = BackupBeforeRestart;
            d["backupKeepLast"] = BackupKeepLast;
            d["backupKeepDays"] = BackupKeepDays;
            d["backupFolder"] = BackupFolder;
            d["watchOnlyProblems"] = WatchOnlyProblems;
            d["vkUrl"] = VkUrl;
            d["adminSteamId"] = AdminSteamId;
            d["shipServerOnlyMods"] = ShipServerOnlyMods;
            d["autoPublish"] = AutoPublish;
            d["autoPublishDelaySec"] = AutoPublishDelaySec;
            d["serveOnStart"] = ServeOnStart;
            d["launcherPath"] = LauncherPath;
            d["launchScript"] = LaunchScript;
            d["launchViaScript"] = LaunchViaScript;
            d["radioEnabled"] = RadioEnabled;
            d["radioFfmpeg"] = RadioFfmpeg;
            d["radioMaxClients"] = RadioMaxClients;
            d["telegramToken"] = TelegramToken;
            d["telegramChat"] = TelegramChat;
            d["vkToken"] = VkToken;
            d["vkPeer"] = VkPeer;
            d["notifyVkVersion"] = NotifyVkVersion;
            d["notifyJoin"] = NotifyJoin;
            d["notifyPack"] = NotifyPack;
            d["notifyServer"] = NotifyServer;
            try { Json.WriteFile(file, d, true); } catch { }
        }
    }

    // Everything the server window shares: settings, process control, telnet, backups,
    // the mod publisher (HTTP + tracker + peer node) and the scheduler.
    public sealed class ServerApp : IDisposable
    {
        public readonly ServerSettings Settings;
        public readonly string SettingsFile;
        public readonly Log Log;
        public readonly ServerController Controller = new ServerController();
        public readonly Telnet Telnet = new Telnet();
        public readonly Scheduler Scheduler;
        public readonly BackupManager Backups;
        public readonly Tracker Tracker = new Tracker();
        public readonly ChunkStore Store;
        public readonly Notifier Notifier = new Notifier();
        public readonly PlayerStats Stats;
        public readonly PackHistory History;
        public readonly RadioProxy Radio;

        public string StatsFile { get; private set; }
        public string HistoryFile { get; private set; }

        public MiniHttp Http;
        public PeerNode Peer;
        public ModManifest Manifest;
        public ServerConfig Config = new ServerConfig();
        public readonly HashCache Hashes;

        // Snapshot of the last "listplayers" answer. The HTTP status page reads it from its
        // own thread, so the whole list is replaced instead of being mutated in place.
        public volatile List<GamePlayer> LastPlayers = new List<GamePlayer>();

        public event Action<string> OnPackChanged;

        public ServerApp()
        {
            string dir = GamePaths.SettingsDir("WolfServer");
            SettingsFile = Path.Combine(dir, "settings.json");
            Settings = ServerSettings.Load(SettingsFile);
            Log = new Log(dir, "server.log", 2000);
            Store = new ChunkStore(Path.Combine(dir, "chunks"));
            Hashes = new HashCache(Path.Combine(dir, "hashes.txt"));

            StatsFile = Path.Combine(dir, "players.json");
            Stats = PlayerStats.Load(StatsFile);
            HistoryFile = Path.Combine(dir, "packs.json");
            History = PackHistory.Load(HistoryFile);
            Radio = new RadioProxy(this);
            ApplyNotifier();

            string backupDir = string.IsNullOrEmpty(Settings.BackupFolder)
                ? Path.Combine(dir, "backups")
                : Settings.BackupFolder;
            Backups = new BackupManager(backupDir);
            Backups.KeepLast = Settings.BackupKeepLast;
            Backups.KeepDays = Settings.BackupKeepDays;
            Backups.OnEvent += delegate(string s) { Log.Add(s); };

            Scheduler = new Scheduler(Path.Combine(dir, "schedule.json"));
            Scheduler.OnEvent += delegate(string s) { Log.Add(s); };

            Controller.AutoRestart = Settings.AutoRestart;
            Controller.LaunchScript = Settings.LaunchScript;
            Controller.OnEvent += delegate(string s) { Log.Add("[srv] " + s); };
            Controller.OnLogLine += delegate(string s) { };

            // Прошлый опубликованный набор. Без него после каждого перезапуска тула первая
            // публикация считалась «первой в жизни»: сравнение с клиентами врало, а повторная
            // публикация того же набора выглядела как новая.
            try
            {
                string published = Path.Combine(PublishFolder(), "manifest.json");
                if (File.Exists(published))
                {
                    Manifest = ModManifest.FromJson(File.ReadAllText(published, System.Text.Encoding.UTF8));
                }
            }
            catch { }
        }

        public void Save()
        {
            Settings.Save(SettingsFile);
        }

        public void ApplyNotifier()
        {
            Notifier.TelegramToken = Settings.TelegramToken;
            Notifier.TelegramChat = Settings.TelegramChat;
            Notifier.VkToken = Settings.VkToken;
            Notifier.VkPeer = Settings.VkPeer;
            Notifier.VkVersion = Settings.NotifyVkVersion;
        }

        public void SaveStats()
        {
            Stats.Save(StatsFile);
        }

        public void SaveHistory()
        {
            History.Save(HistoryFile);
        }

        // Manifest snapshots of previous packs, so an older version can be served again
        // without re-hashing the Mods folder (and even if that folder has already changed).
        public string PacksFolder()
        {
            return Path.Combine(Path.GetDirectoryName(SettingsFile) ?? GamePaths.AppDir, "packs");
        }

        public string ResolveServerRoot()
        {
            if (!string.IsNullOrEmpty(Settings.ServerRoot) && Directory.Exists(Settings.ServerRoot))
            {
                return Settings.ServerRoot;
            }
            string found = GamePaths.ServerRoot(null);
            if (found != null)
            {
                Settings.ServerRoot = found;
                Save();
            }
            return found;
        }

        public string ResolveConfigFile()
        {
            if (!string.IsNullOrEmpty(Settings.ConfigFile) && File.Exists(Settings.ConfigFile))
            {
                return Settings.ConfigFile;
            }
            string root = ResolveServerRoot();
            if (root == null) { return null; }
            string cfg = GamePaths.ServerConfig(root);
            if (File.Exists(cfg)) { Settings.ConfigFile = cfg; return cfg; }
            return null;
        }

        public string ResolveSteamCmd()
        {
            if (!string.IsNullOrEmpty(Settings.SteamCmd) && File.Exists(Settings.SteamCmd)) { return Settings.SteamCmd; }
            string found = GamePaths.FindSteamCmd(null);
            if (found != null) { Settings.SteamCmd = found; Save(); }
            return found;
        }

        // The Mods folder the server actually loads.
        public string ServerModsDir()
        {
            string root = ResolveServerRoot();
            if (root == null) { return null; }
            // A Mods folder next to the executable is what the dedicated server reads.
            string beside = Path.Combine(root, "Mods");
            if (Directory.Exists(beside)) { return beside; }
            return GamePaths.ServerModsDir(root);
        }

        // Folder whose contents define the pack handed out to clients.
        public string ModsSourceDir()
        {
            if (!string.IsNullOrEmpty(Settings.ModsSource) && Directory.Exists(Settings.ModsSource))
            {
                return Settings.ModsSource;
            }
            return ServerModsDir();
        }

        // Public files (manifest.json, server.json) served over HTTP.
        public string PublishFolder()
        {
            return Path.Combine(Path.GetDirectoryName(SettingsFile) ?? GamePaths.AppDir, "publish");
        }

        public string SavesDir()
        {
            string root = ResolveServerRoot();
            return Config.Loaded ? Config.SavesFolder(root) : GamePaths.ServerSavesDir(root ?? "");
        }

        // The launcher build handed out to players: dist\WolfLauncher next to this tool,
        // or the copy the admin put into the settings folder.
        public string LauncherFile()
        {
            List<string> candidates = new List<string>();
            string settings = Path.GetDirectoryName(SettingsFile) ?? "";
            if (!string.IsNullOrEmpty(Settings.LauncherPath)) { candidates.Add(Settings.LauncherPath); }
            candidates.Add(Path.Combine(settings, "WolfLauncher.exe"));
            candidates.Add(Path.Combine(Path.Combine(GamePaths.AppDir, ".."), "WolfLauncher", "WolfLauncher.exe"));
            candidates.Add(Path.Combine(Path.Combine(GamePaths.AppDir, ".."), "dist", "WolfLauncher", "WolfLauncher.exe"));
            candidates.Add(Path.Combine(GamePaths.AppDir, "WolfLauncher.exe"));
            foreach (string c in candidates)
            {
                try
                {
                    if (!string.IsNullOrEmpty(c) && File.Exists(c)) { return Path.GetFullPath(c); }
                }
                catch { }
            }
            return null;
        }

        // Publishes launcher.json (size + sha256) so clients can tell whether they are current.
        public string PublishLauncherInfo()
        {
            string exe = LauncherFile();
            if (exe == null) { return null; }
            string publicDir = PublishFolder();
            Directory.CreateDirectory(publicDir);
            FileInfo fi = new FileInfo(exe);
            string sha = ChunkStore.HashOfFile(exe);
            string version = "";
            try
            {
                System.Diagnostics.FileVersionInfo vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
                version = vi.FileVersion ?? "";
            }
            catch { }
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["size"] = fi.Length;
            d["sha256"] = sha;
            d["version"] = string.IsNullOrEmpty(version) ? "" : version;
            d["built"] = fi.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            d["note"] = Settings.PackNote;
            Json.WriteFile(Path.Combine(publicDir, "launcher.json"), d, true);
            return sha;
        }

        // Zip with the launcher and its instruction, so a new player needs one download.
        public string LauncherZip()
        {
            string exe = LauncherFile();
            if (exe == null) { return null; }
            string publicDir = PublishFolder();
            string zip = Path.Combine(publicDir, "WolfLauncher.zip");
            string readme = Path.Combine(Path.GetDirectoryName(exe) ?? "", "README.txt");
            try
            {
                if (File.Exists(zip))
                {
                    DateTime exeTime = File.GetLastWriteTimeUtc(exe);
                    DateTime zipTime = File.GetLastWriteTimeUtc(zip);
                    if (zipTime >= exeTime && (!File.Exists(readme) || zipTime >= File.GetLastWriteTimeUtc(readme)))
                    {
                        return zip;
                    }
                }
                Directory.CreateDirectory(publicDir);
                if (File.Exists(zip)) { File.Delete(zip); }
                using (FileStream fs = File.Create(zip))
                using (System.IO.Compression.ZipArchive za = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
                {
                    AddToZip(za, exe, Path.GetFileName(exe));
                    if (File.Exists(readme)) { AddToZip(za, readme, "README.txt"); }
                }
                return zip;
            }
            catch (Exception ex)
            {
                Log.Add("не удалось собрать zip лаунчера: " + ex.Message);
                return File.Exists(zip) ? zip : null;
            }
        }

        private static void AddToZip(System.IO.Compression.ZipArchive za, string file, string entryName)
        {
            System.IO.Compression.ZipArchiveEntry entry = za.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
            using (Stream target = entry.Open())
            using (FileStream source = File.OpenRead(file))
            {
                source.CopyTo(target);
            }
        }

        public string WorldName()
        {
            return Config.Loaded ? Config.Get("GameName", "") : "";
        }

        public void Dispose()
        {
            try { Notifier.Dispose(); } catch { }
            try { Scheduler.Stop(); } catch { }
            try { Controller.Dispose(); } catch { }
            try { Telnet.Dispose(); } catch { }
            try { if (Peer != null) { Peer.Stop(); } } catch { }
            try { if (Http != null) { Http.Stop(); } } catch { }
        }

        public void RaisePackChanged()
        {
            Action<string> h = OnPackChanged;
            if (h != null) { h(""); }
        }

        // Быстрый вариант для UI: только локальные адреса, к сети не обращается.
        public string LocalBaseUrl()
        {
            int port = Settings.PublishPort;
            List<string> local = Wolf.Http.LocalIps();
            if (local.Count > 0) { return "http://" + local[0] + ":" + port.ToString(CultureInfo.InvariantCulture); }
            return "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
        }

        // Для UI: внешний адрес берём только из кэша, без сетевых вызовов —
        // иначе окно подвисает на старте после перезагрузки (DNS ещё не поднялся).
        public string DisplayBaseUrl()
        {
            int port = Settings.PublishPort;
            string ip = Wolf.Http.PublicIpCached();
            if (!string.IsNullOrEmpty(ip)) { return "http://" + ip + ":" + port.ToString(CultureInfo.InvariantCulture); }
            return LocalBaseUrl();
        }

        // Advertised URL that clients should paste into the launcher.
        // ВНИМАНИЕ: это сетевой вызов (до ~7 с при мёртвом DNS) — только из фонового потока.
        public string PublicBaseUrl()
        {
            string ip = Wolf.Http.PublicIp();
            int port = Settings.PublishPort;
            if (!string.IsNullOrEmpty(ip)) { return "http://" + ip + ":" + port.ToString(CultureInfo.InvariantCulture); }
            return LocalBaseUrl();
        }

        public void OpenFolder(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) { Proc.StartDetached("explorer.exe", "\"" + path + "\"", null); }
            }
            catch { }
        }

        public void OpenUrl(string url)
        {
            try { Proc.StartDetached(url, "", null); } catch { }
        }
    }
}
