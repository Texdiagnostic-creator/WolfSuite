using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Wolf.Launcher
{
    // Persisted launcher settings (mirrors what ModSync kept in config.json).
    public sealed class LauncherSettings
    {
        // Pre-filled so a new player only has to press "Проверить моды" and "Играть".
        public const string DefaultServerUrl = "http://188.134.68.141:8775";

        public string ServerUrl = DefaultServerUrl;
        public string GameRoot = "";
        public string ModsDir = "";
        public string SteamId = "";
        public string PlayerName = "";
        public bool AutoCheck = true;
        public int CheckIntervalMin = 30;
        public bool UseEac = false;
        public bool SkipIntro = true;
        public bool SkipNews = true;
        public string ExtraArgs = "";
        public bool SetupDone = false;
        public bool AcceptedRules = false;
        public string LastHash = "";
        public int PeerPort = 6895;
        public string VkUrl = "https://vk.ru/7daystodieworld";
        public bool AutoPlayAfterSync = false;   // start the game right after a successful update
        public bool RunAtStartup = true;         // launcher starts with Windows (HKCU Run); on by default

        public static LauncherSettings Load(string file)
        {
            Dictionary<string, object> d = Json.ReadFile(file);
            LauncherSettings s = new LauncherSettings();
            s.ServerUrl = Json.Str(d, "serverUrl", s.ServerUrl);
            s.GameRoot = Json.Str(d, "gameRoot", s.GameRoot);
            s.ModsDir = Json.Str(d, "modsDir", s.ModsDir);
            s.SteamId = Json.Str(d, "steamid", s.SteamId);
            s.PlayerName = Json.Str(d, "playerName", s.PlayerName);
            s.AutoCheck = Json.Bool(d, "autoCheck", s.AutoCheck);
            s.CheckIntervalMin = Json.Int(d, "checkIntervalMin", s.CheckIntervalMin);
            s.UseEac = Json.Bool(d, "useEac", s.UseEac);
            s.SkipIntro = Json.Bool(d, "skipIntro", s.SkipIntro);
            s.SkipNews = Json.Bool(d, "skipNews", s.SkipNews);
            s.ExtraArgs = Json.Str(d, "extraArgs", s.ExtraArgs);
            s.SetupDone = Json.Bool(d, "setupDone", s.SetupDone);
            s.AcceptedRules = Json.Bool(d, "acceptedRules", s.AcceptedRules);
            s.LastHash = Json.Str(d, "lastHash", s.LastHash);
            s.PeerPort = Json.Int(d, "peerPort", s.PeerPort);
            s.VkUrl = Json.Str(d, "vkUrl", s.VkUrl);
            s.AutoPlayAfterSync = Json.Bool(d, "autoPlayAfterSync", s.AutoPlayAfterSync);
            s.RunAtStartup = Json.Bool(d, "runAtStartup", s.RunAtStartup);
            return s;
        }

        public void Save(string file)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["serverUrl"] = ServerUrl;
            d["gameRoot"] = GameRoot;
            d["modsDir"] = ModsDir;
            d["steamid"] = SteamId;
            d["playerName"] = PlayerName;
            d["autoCheck"] = AutoCheck;
            d["checkIntervalMin"] = CheckIntervalMin;
            d["useEac"] = UseEac;
            d["skipIntro"] = SkipIntro;
            d["skipNews"] = SkipNews;
            d["extraArgs"] = ExtraArgs;
            d["setupDone"] = SetupDone;
            d["acceptedRules"] = AcceptedRules;
            d["lastHash"] = LastHash;
            d["peerPort"] = PeerPort;
            d["vkUrl"] = VkUrl;
            d["autoPlayAfterSync"] = AutoPlayAfterSync;
            d["runAtStartup"] = RunAtStartup;
            try { Json.WriteFile(file, d, true); } catch { }
        }
    }

    // What the server publishes at /server.json.
    public sealed class ServerInfo
    {
        public string Name = "";
        public string Version = "";
        public string Note = "";
        public string Vk = "";
        public string World = "";
        public int Slots;
        public int Port = 26900;
        public string Built = "";
        public List<string> Changes = new List<string>();

        public static ServerInfo FromJson(string text)
        {
            Dictionary<string, object> d = Json.ParseObject(text);
            ServerInfo s = new ServerInfo();
            s.Name = Json.Str(d, "name", "");
            s.Version = Json.Str(d, "version", "");
            s.Note = Json.Str(d, "note", "");
            s.Vk = Json.Str(d, "vk", "");
            s.World = Json.Str(d, "world", "");
            s.Slots = Json.Int(d, "slots", 0);
            s.Port = Json.Int(d, "port", 26900);
            s.Built = Json.Str(d, "built", "");
            foreach (object o in Json.Arr(d, "changes"))
            {
                string line = Convert.ToString(o);
                if (!string.IsNullOrEmpty(line)) { s.Changes.Add(line); }
            }
            return s;
        }
    }

    // Почему конкретный файл не совпал с сервером: нужно, чтобы игрок видел причину кика
    // античитом, а не сообщение «моды не совпали». Пишется в журнал лаунчера.
    public sealed class DiffMismatch
    {
        public string rel = "";
        public string reason = "";      // нет файла / размер / содержимое
        public long localSize;
        public long serverSize;
        public string localHash = "";
        public string serverHash = "";

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(rel).Append(": ").Append(reason);
            if (reason == "размер")
            {
                sb.Append(" (локально ").Append(localSize.ToString(CultureInfo.InvariantCulture))
                  .Append(" Б, на сервере ").Append(serverSize.ToString(CultureInfo.InvariantCulture)).Append(" Б)");
            }
            else if (reason == "содержимое")
            {
                sb.Append(" (локально ").Append(Short(localHash))
                  .Append(", на сервере ").Append(Short(serverHash)).Append(", размер тот же)");
            }
            return sb.ToString();
        }

        private static string Short(string hash)
        {
            return string.IsNullOrEmpty(hash) ? "?" : hash.Substring(0, Math.Min(12, hash.Length));
        }
    }

    // Client-side mod synchronisation: read manifest -> fetch missing chunks -> materialise files.
    public sealed class ModSync
    {
        private readonly ChunkStore store;
        private readonly string modsDir;
        private readonly Log log;

        // Кэш локальных хешей делает глубокую сверку дешёвой: пересчитываются только файлы,
        // у которых изменился размер или дата изменения.
        public LocalHashCache HashCache { get; set; }

        // true = не доверять кэшу и пересчитать хеши всех файлов (кнопка «Проверить файлы»).
        // Нужно на случай, когда файл подменили, сохранив его размер и дату изменения:
        // кэш такую подмену не заметит, а античит на сервере заметит.
        public bool IgnoreCache { get; set; }

        // Заполняется в Diff(): список файлов, которые не совпали, с причиной расхождения.
        public List<DiffMismatch> Mismatches { get { return mismatches; } }
        private readonly List<DiffMismatch> mismatches = new List<DiffMismatch>();

        public event Action<int, int, string> OnProgress;
        public event Action<long, long, string> OnTransfer;   // bytes done, bytes total, source
        public event Action<string> OnStep;

        public ModManifest Manifest { get; private set; }
        public ServerInfo Info { get; private set; }

        // Cancellation: the UI calls Cancel() from the main thread; Fetch and Apply stop at
        // the next chunk boundary. Chunks already in the store are kept, so "Повторить"
        // continues from where it stopped instead of starting from zero.
        private volatile bool cancelled;
        private ChunkFetcher fetcher;

        public bool Cancelled { get { return cancelled; } }

        public void Cancel()
        {
            cancelled = true;
            ChunkFetcher f = fetcher;
            if (f != null) { f.Cancel(); }
        }

        public ModSync(ChunkStore store, string modsDir, Log log)
        {
            this.store = store;
            this.modsDir = modsDir;
            this.log = log;
        }

        public string BaseUrl { get; set; }

        public bool FetchManifest(out string error)
        {
            error = null;
            try
            {
                string text = Wolf.Http.Get(BaseUrl.TrimEnd('/') + "/manifest.json", 15000);
                Manifest = ModManifest.FromJson(text);
                if (Manifest.files.Count == 0)
                {
                    error = "манифест пуст";
                    return false;
                }
                string infoText;
                if (Wolf.Http.TryGet(BaseUrl.TrimEnd('/') + "/server.json", 8000, out infoText))
                {
                    Info = ServerInfo.FromJson(infoText);
                }
                else
                {
                    Info = new ServerInfo();
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // Which of the manifest files differ from what is on disk right now.
        public List<ManifestFile> Diff()
        {
            return Diff(false);
        }

        // deep=true compares the sha256 of every file, not only its size. That catches a
        // same-size corrupted file and a half-written download left by a killed launcher.
        // With HashCache set the hashes are cached per path+size+mtime, so the deep pass costs
        // almost nothing for files that did not change.
        public List<ManifestFile> Diff(bool deep)
        {
            List<ManifestFile> changed = new List<ManifestFile>();
            mismatches.Clear();
            if (Manifest == null) { return changed; }
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int processed = 0;
            int total = Manifest.files.Count;
            foreach (ManifestFile f in Manifest.files)
            {
                if (cancelled) { break; }
                processed++;
                // Глубокая сверка 9 ГБ на первом запуске идёт минутами: без прогресса игрок
                // решает, что лаунчер завис. Сообщение уходит раз в 50 файлов, в журнал — нет.
                if (deep && OnStep != null && processed % 50 == 0)
                {
                    OnStep("проверка файлов " + processed.ToString(CultureInfo.InvariantCulture) +
                           " / " + total.ToString(CultureInfo.InvariantCulture));
                }
                seen.Add(f.rel);
                string target = Path.Combine(modsDir, f.rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(target))
                {
                    changed.Add(f);
                    mismatches.Add(NewMismatch(f, "нет файла"));
                    continue;
                }
                try
                {
                    FileInfo fi = new FileInfo(target);
                    if (fi.Length != f.size)
                    {
                        changed.Add(f);
                        DiffMismatch m = NewMismatch(f, "размер");
                        m.localSize = fi.Length;
                        mismatches.Add(m);
                        continue;
                    }
                    if (deep)
                    {
                        string hash;
                        if (HashCache != null && !IgnoreCache)
                        {
                            hash = HashCache.HashOf(f.rel, target, fi.Length, fi.LastWriteTimeUtc);
                        }
                        else
                        {
                            hash = ChunkStore.HashOfFile(target);
                            // Принудительная сверка обновляет кэш: иначе следующий обычный
                            // запуск снова возьмёт из кэша устаревшую запись.
                            if (HashCache != null)
                            {
                                HashCache.Set(f.rel, fi.Length, fi.LastWriteTimeUtc.Ticks, hash);
                            }
                        }
                        if (!string.Equals(hash, f.hash, StringComparison.OrdinalIgnoreCase))
                        {
                            changed.Add(f);
                            DiffMismatch m = NewMismatch(f, "содержимое");
                            m.localHash = hash;
                            mismatches.Add(m);
                        }
                    }
                }
                catch
                {
                    changed.Add(f);
                    mismatches.Add(NewMismatch(f, "не читается"));
                }
            }
            // Хеши файлов, которых сервер больше не раздаёт, в кэше держать незачем.
            if (HashCache != null) { HashCache.Prune(seen); }
            return changed;
        }

        private static DiffMismatch NewMismatch(ManifestFile f, string reason)
        {
            DiffMismatch m = new DiffMismatch();
            m.rel = f.rel;
            m.reason = reason;
            m.serverSize = f.size;
            m.serverHash = f.hash;
            return m;
        }

        public bool Apply(List<ManifestFile> changed, List<PeerInfo> peers, out string error)
        {
            error = null;
            try
            {
                if (cancelled) { error = "отменено"; return false; }

                List<ChunkRef> needed = new List<ChunkRef>();
                foreach (ManifestFile f in changed)
                {
                    needed.AddRange(f.chunks);
                }

                OnStepStep("поиск источников...");
                ChunkFetcher fetcher = new ChunkFetcher(store, 8000);
                this.fetcher = fetcher;
                fetcher.OnEvent += delegate(string s) { if (log != null) { log.Add("[p2p] " + s); } };
                fetcher.OnTransfer += delegate(long doneBytes, long totalBytes, string source)
                {
                    if (OnTransfer != null) { OnTransfer(doneBytes, totalBytes, source); }
                };

                bool ok = fetcher.Fetch(needed, peers, BaseUrl, delegate(int done, int total, string source)
                {
                    if (OnProgress != null) { OnProgress(done, total, source); }
                });

                if (cancelled) { error = "отменено"; return false; }
                if (!ok)
                {
                    error = "не все чанки получены (нет источника)";
                    return false;
                }

                int index = 0;
                foreach (ManifestFile f in changed)
                {
                    if (cancelled) { error = "отменено"; return false; }
                    index++;
                    OnStepStep("запись " + index + "/" + changed.Count + ": " + f.rel);
                    string target = Path.Combine(modsDir, f.rel.Replace('/', Path.DirectorySeparatorChar));
                    if (!store.ExportFile(target, f.chunks, null))
                    {
                        error = "не удалось собрать " + f.rel;
                        return false;
                    }
                    // Только что собранный файл точно совпадает с манифестом — запоминаем хеш,
                    // чтобы следующая глубокая сверка не читала его с диска.
                    if (HashCache != null) { HashCache.Remember(f.rel, target, f.hash); }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                this.fetcher = null;
            }
        }

        private void OnStepStep(string text)
        {
            if (OnStep != null) { OnStep(text); }
            if (log != null) { log.Add(text); }
        }

        // Mod folders that the server no longer ships are MOVED to disabled_mods, not deleted:
        // a mod the player installed himself, or one whose folder name differs by a symbol,
        // must not be lost because of a manifest that arrived half-empty.
        public List<string> RemoveObsolete(Action<string> onRemove)
        {
            List<string> removed = new List<string>();
            if (Manifest == null || !Directory.Exists(modsDir)) { return removed; }
            // Sanity guard: never sweep a folder based on a suspiciously small manifest.
            if (Manifest.loadOrder.Count < 5) { return removed; }

            string quarantine = Path.Combine(modsDir, "disabled_mods");
            HashSet<string> keep = new HashSet<string>(Manifest.loadOrder, StringComparer.OrdinalIgnoreCase);
            foreach (string dir in GamePaths.SafeDirs(modsDir))
            {
                string name = Path.GetFileName(dir);
                if (keep.Contains(name)) { continue; }
                if (name.Equals("disabled_mods", StringComparison.OrdinalIgnoreCase)) { continue; }
                if (name.StartsWith("_wolf", StringComparison.OrdinalIgnoreCase)) { continue; }
                try
                {
                    Directory.CreateDirectory(quarantine);
                    string target = Path.Combine(quarantine, name);
                    if (Directory.Exists(target)) { target += "_" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture); }
                    Directory.Move(dir, target);
                    removed.Add(name);
                    if (onRemove != null) { onRemove(name); }
                }
                catch { }
            }
            return removed;
        }

        public long DownloadSize(List<ManifestFile> changed)
        {
            long total = 0;
            foreach (ManifestFile f in changed)
            {
                foreach (ChunkRef c in f.chunks)
                {
                    if (!store.Has(c.sha, c.size)) { total += c.size; }
                }
            }
            return total;
        }

        // Лаунчер пишет адрес сервера для мода автоподключения (Mods\ZZZ_WolfAutoConnect).
        // В самой игре нет ключа командной строки для автовхода, поэтому адрес передаётся
        // файлом: мод читает его при входе в главное меню.
        public static void WriteAutoConnect(string serverUrl, int port)
        {
            try
            {
                if (string.IsNullOrEmpty(serverUrl)) { return; }
                Uri uri;
                if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out uri)) { return; }
                if (port <= 0) { port = 26900; }

                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "7DaysToDie");
                Directory.CreateDirectory(dir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                sb.AppendLine("<AutoConnect>");
                sb.AppendLine("  <Enabled>true</Enabled>");
                sb.AppendLine("  <ServerIP>" + uri.Host + "</ServerIP>");
                sb.AppendLine("  <ServerPort>" + port.ToString(CultureInfo.InvariantCulture) + "</ServerPort>");
                sb.AppendLine("</AutoConnect>");
                File.WriteAllText(Path.Combine(dir, "WolfAutoConnect.xml"), sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        // Убираем файл после запуска игры: иначе прямой запуск из Steam тоже тянул бы на сервер.
        public static void ClearAutoConnect()
        {
            try
            {
                string file = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "7DaysToDie"), "WolfAutoConnect.xml");
                if (File.Exists(file)) { File.Delete(file); }
            }
            catch { }
        }

        // Launches the game with the flags ModSync used (EAC off, skip intro/news).
        public static bool LaunchGame(string gameRoot, LauncherSettings s, out string error)
        {
            error = null;
            string exe = Path.Combine(gameRoot ?? "", GamePaths.ClientExeName);
            if (!File.Exists(exe))
            {
                error = "7DaysToDie.exe не найден в " + gameRoot;
                return false;
            }

            List<string> args = new List<string>();
            args.Add("-useEAC=" + (s.UseEac ? "1" : "0"));
            if (s.SkipIntro) { args.Add("-skipintro"); }
            if (s.SkipNews) { args.Add("-skipnews"); }
            if (!string.IsNullOrWhiteSpace(s.ExtraArgs)) { args.Add(s.ExtraArgs.Trim()); }

            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(exe, string.Join(" ", args.ToArray()));
                psi.WorkingDirectory = gameRoot;
                psi.UseShellExecute = true;
                System.Diagnostics.Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // Steam ID from the registry of the local Steam client (used only for the tracker name).
        public static string DetectSteamId()
        {
            try
            {
                object v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null);
                if (v != null)
                {
                    long id = Convert.ToInt64(v, CultureInfo.InvariantCulture);
                    if (id > 0)
                    {
                        return (id + 76561197960265728L).ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
            catch { }
            return "";
        }
    }
}
