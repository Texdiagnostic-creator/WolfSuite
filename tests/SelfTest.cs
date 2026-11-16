using System;
using System.Collections.Generic;
using System.IO;
using Wolf;

internal static class SelfTest
{
    private static int failures;

    private static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        TestJson();
        TestConfig();
        TestTelnet();
        TestFilters();
        TestManifest();
        TestManifestParity();
        TestChunkStore();
        TestCommonExtras();

        Console.WriteLine(failures == 0 ? "\nALL TESTS PASSED" : "\nFAILURES: " + failures);
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    private static void Check(bool condition, string what)
    {
        if (condition) { Console.WriteLine("  ok   " + what); }
        else { Console.WriteLine("  FAIL " + what); failures++; }
    }

    private static void TestJson()
    {
        Console.WriteLine("== JSON ==");
        Dictionary<string, object> d = new Dictionary<string, object>();
        d["name"] = "тест \"кавычки\"\nстрока";
        d["n"] = 42L;
        d["f"] = 1.5;
        d["b"] = true;
        List<object> arr = new List<object>();
        arr.Add(1L);
        arr.Add("два");
        d["list"] = arr;
        d["nested"] = new Dictionary<string, object> { { "k", "v" } };

        string text = Json.Serialize(d);
        Dictionary<string, object> back = Json.ParseObject(text);
        Check(Json.Str(back, "name", "") == "тест \"кавычки\"\nстрока", "строка с кавычками и переводом строки");
        Check(Json.Long(back, "n", 0) == 42, "целое число");
        Check(Math.Abs((double)back["f"] - 1.5) < 0.0001, "дробное число");
        Check(Json.Bool(back, "b", false), "bool");
        Check(Json.Arr(back, "list").Count == 2, "массив");
        Check(Json.Str(Json.Obj(back, "nested"), "k", "") == "v", "вложенный объект");
        Check(Json.SerializePretty(d).Contains("\n"), "pretty-печать");
        Check(Json.ParseObject("").Count == 0, "пустой вход");
    }

    private static void TestConfig()
    {
        Console.WriteLine("== serverconfig.xml ==");
        string real = TestPaths.ConfigFile;
        if (string.IsNullOrEmpty(real))
        {
            Console.WriteLine("  skip: переменная WOLF_TEST_CONFIG не задана");
            return;
        }
        ServerConfig cfg = new ServerConfig();
        bool loaded = cfg.Load(real);
        Check(loaded, "реальный конфиг загружен: " + real);
        if (!loaded) { return; }

        Console.WriteLine("     параметров найдено: " + cfg.Properties.Count);
        Check(cfg.Properties.Count > 40, "больше 40 параметров");
        Check(!string.IsNullOrEmpty(cfg.Get("ServerName", "")), "ServerName = " + cfg.Get("ServerName", ""));
        Check(cfg.GetInt("ServerPort", 0) > 0, "ServerPort = " + cfg.GetInt("ServerPort", 0));
        Check(cfg.Find("GameWorld") != null, "GameWorld найден");
        Check(cfg.Find("TelnetPort") != null, "TelnetPort найден");

        // Round-trip: change a value in memory, verify the comment survives the edit.
        string original = cfg.Get("ServerName", "");
        bool changed = cfg.Set("ServerName", "Wolf Test Name");
        Check(changed, "изменение значения");
        Check(cfg.Get("ServerName", "") == "Wolf Test Name", "значение применено");
        cfg.Set("ServerName", original);
        Check(cfg.Get("ServerName", "") == original, "значение возвращено");

        List<string> issues = cfg.Validate();
        Console.WriteLine("     валидация: " + (issues.Count == 0 ? "без замечаний" : string.Join("; ", issues.ToArray())));
        Check(cfg.SavesFolder(Path.GetDirectoryName(real)) != null, "папка сохранений определена");

        // A config with a deliberately broken value must be reported.
        cfg.Set("ServerPort", "1");
        Check(cfg.Validate().Count > 0, "валидатор ловит плохой порт");
        cfg.Set("ServerPort", "26900");

        List<string> worlds = ServerConfig.KnownWorlds(Path.GetDirectoryName(real));
        Console.WriteLine("     известные миры: " + (worlds.Count == 0 ? "(нет)" : string.Join(", ", worlds.ToArray())));
    }

    private static void TestTelnet()
    {
        Console.WriteLine("== telnet listplayers ==");
        // V3.3 reply: "0: Name (SteamID=...) at (...)".
        string old3 =
            "22:02 1684.450 INF EntityBackpack id 123027, plyrId 116539\n" +
            "0: Wolf (SteamID=76561198164237936) at (1234, 64, -5678)\n" +
            "1: Игрок (SteamID=76561198000000000) at (-20, 61.5, 30)\n" +
            "22:03 1685.299 INF Executing command 'listplayers' by Telnet from 127.0.0.1:59933\n" +
            "Total of 2 in the game\n";
        List<GamePlayer> oldPlayers = Telnet.ParsePlayers(old3);
        Check(oldPlayers.Count == 2, "старый формат V3.3, найдено " + oldPlayers.Count);
        Check(oldPlayers.Count > 0 && oldPlayers[0].Name == "Wolf", "имя первого игрока (V3.3)");
        Check(oldPlayers.Count > 1 && oldPlayers[1].Name == "Игрок", "кириллица в нике");

        // V3.4 reply, captured from the live server on 2026-11-04.
        string v34 =
            "2026-11-03T23:58:26 7331.796 INF 7331.796 SleeperVolume -871, 48, -714: Spawning -860, 48, -696\n" +
            "2026-11-03T23:58:29 7371.574 INF Executing command 'dronectl hello 0' from client EntityID=116539, PltfmId='Steam_76561198143719463', CrossId='EOS_0002f073a7754c4aab096a50e5f56896', OwnerID='Steam_76561198143719463', PlayerName='Капитан Черный Болт', ClientNumber='2'\n" +
            "0. id=115020, sori 37, pos=(-878.7, 48.1, -719.4), rot=(0.0, 4048.6, 0.0), remote=True, health=56, deaths=27, zombies=401, players=45, score=218, level=20, pltfmid=Steam_76561199145351199, crossid=EOS_000251d1556249fabaa142489482855f, ip=176.110.191.181, ping=9\n" +
            "1. id=116539, Капитан Черный Болт, pos=(-1115.6, 43.1, -1209.8), rot=(-16.9, -105.6, 0.0), remote=True, health=122, deaths=17, zombies=293, players=65, score=225, level=20, pltfmid=Steam_76561198143719463, crossid=EOS_0002f073a7754c4aab096a50e5f56896, ip=94.140.245.41, ping=5\n" +
            "Total of 2 in the game\n";
        List<GamePlayer> players = Telnet.ParsePlayers(v34);
        Check(players.Count == 2, "V3.4: строки лога не считаются игроками, найдено " + players.Count);
        Check(players.Count == 2 && players[0].Name == "sori 37", "V3.4 имя с пробелом: " + (players.Count > 0 ? players[0].Name : "-"));
        Check(players.Count > 1 && players[1].Name == "Капитан Черный Болт", "V3.4 кириллица: " + (players.Count > 1 ? players[1].Name : "-"));
        Check(players.Count > 0 && players[0].EntityId == "115020", "V3.4 entity id: " + (players.Count > 0 ? players[0].EntityId : "-"));
        Check(players.Count > 0 && players[0].SteamId == "Steam_76561199145351199",
            "V3.4 pltfmid не спутан с id=: " + (players.Count > 0 ? players[0].SteamId : "-"));
        Check(players.Count > 0 && players[0].HasPosition && Math.Abs(players[0].X + 878.7) < 0.01, "V3.4 позиция разобрана");
        Check(players.Count > 0 && players[0].Handle == "115020", "handle для команд — entity id");
        Check(players.Count > 0 && players[0].Level == "20" && players[0].Ping == "9", "V3.4 уровень/пинг");

        // listplayerids carries the same names in a shorter form.
        List<GamePlayer> ids = Telnet.ParsePlayers("1. id=115020, sori 37\n2. id=116539, Капитан Черный Болт\nTotal of 2 in the game\n");
        Check(ids.Count == 2, "listplayerids: найдено " + ids.Count);
        Check(ids.Count > 1 && ids[1].Name == "Капитан Черный Болт", "listplayerids имя");

        // V3.4 "ban list": "until - UserID (name) - Reason".
        List<BannedPlayer> bans = Telnet.ParseBans(
            "Ban list entries:\n" +
            "  Banned until - UserID (name) - Reason\n" +
            "11/4/26 12:00:00 AM - Steam_76561198164237936 (Wolf) - cheating\n" +
            "Never - Steam_76561198000000000 (Игрок) - гриферство\n");
        Check(bans.Count == 2, "ban list V3.4: найдено " + bans.Count);
        Check(bans.Count > 0 && bans[0].Name == "Wolf", "ban list имя");
        Check(bans.Count > 0 && bans[0].SteamId == "Steam_76561198164237936", "ban list userid");
        Check(bans.Count > 1 && bans[1].Reason == "гриферство", "ban list причина: " + (bans.Count > 1 ? bans[1].Reason : "-"));

        // Exact entry captured from the live V3.4 server after a test ban.
        List<BannedPlayer> live = Telnet.ParseBans(
            "Ban list entries:\n" +
            "  Banned until - UserID (name) - Reason\n" +
            "  2026-11-04 05:48:51 - Steam_76561190000000001 (wolf syntax test) - wolf syntax test\n");
        Check(live.Count == 1 && live[0].SteamId == "Steam_76561190000000001",
            "ban list V3.4: живая строка разобрана");
        Check(live.Count == 1 && live[0].Expires == "2026-11-04 05:48:51",
            "ban list V3.4: дата снятия");

        // Commands as the game documents them in V3.4 (verified against the live server).
        Check(Telnet.SayCommand("привет мир") == "say \"привет мир\"", "say: фраза целиком в кавычках");
        Check(Telnet.SayCommand("a \"b\"\nc") == "say \"a 'b' c\"", "say: кавычки и перевод строки обезврежены");
        Check(Telnet.BanAddCommand("Steam_76561190000000001", 2, "hour(s)", "тест") ==
              "ban add Steam_76561190000000001 2 hour(s) \"тест\"", "ban add: синтаксис V3.4");

        // A reply with no players is not an error: it must simply produce an empty list.
        Check(Telnet.ParsePlayers("2026-11-04T05:46:44 13744.148 INF Executing command 'listplayers' by Telnet from 127.0.0.1:57621\nTotal of 0 in the game\n").Count == 0,
            "listplayers: пустой сервер");
        Check(Telnet.ParsePlayers("0. id=115020, , pos=(1, 2, 3), pltfmid=Steam_76561199145351199, ping=9\n").Count == 1,
            "listplayers: игрок без имени не теряется");
    }

    private static void TestFilters()
    {
        Console.WriteLine("== ModFilters ==");
        ModFilters f = new ModFilters();

        // Glob semantics are the tricky part: "*" is one path level, "**" is any depth.
        f.AddFilePattern("*/Saves/*");
        Check(f.IsFileExcluded("SomeMod/Saves/player.xml"), "*/Saves/* ловит файл внутри мода");
        Check(!f.IsFileExcluded("SomeMod/Config/Saves.xml"), "*/Saves/* не трогает Saves.xml");
        Check(f.IsFileExcluded("SomeMod/Deep/Nested/Saves/x.xml") == false, "*/Saves/* не матчит на глубину больше одного уровня");

        ModFilters g = new ModFilters();
        g.AddFilePattern("*/obj/*");
        Check(g.IsFileExcluded("Mod/obj/Debug/x.dll"), "*/obj/* ловит артефакт сборки");
        Check(!g.IsFileExcluded("Mod/obj.dll"), "*/obj/* не трогает файл obj.dll в корне мода");

        ModFilters h = new ModFilters();
        h.ExcludeMod("ChunkKeepAlive");
        Check(h.IsModExcluded("ChunkKeepAlive"), "мод исключён по имени");
        Check(h.IsModExcluded("chunkkeepalive"), "имя мода сравнивается без учёта регистра");
        Check(h.IsModExcluded("disabled_mods"), "disabled_mods всегда исключён");
        Check(!h.IsModExcluded("Ark"), "посторонний мод остаётся в наборе");

        ModFilters real = new ModFilters();
        if (real.LoadModSyncConfig(TestPaths.ModSyncConfig))
        {
            Console.WriteLine("     " + string.Join("; ", new List<string>(real.Sources).ToArray()));
            Check(real.IsModExcluded("ChunkKeepAlive"), "ModSync exclude_patterns даёт исключение мода");
            Check(real.IsFileExcluded("Anything/obj/Debug/x.dll"), "ModSync exclude_patterns даёт исключение файла");
            Check(!real.IsModExcluded("Ark"), "Ark остаётся в раздаче");
        }
        else
        {
            Console.WriteLine("  skip: конфиг ModSync не найден (" + TestPaths.ModSyncConfig + ")");
        }

        ModFilters zac = new ModFilters();
        if (zac.LoadAntiCheatConfig(TestPaths.AntiCheatConfig))
        {
            Check(zac.IsModExcluded("ServerFixes"), "ServerOnlyMods античита применяется");
        }
        else
        {
            Console.WriteLine("  skip: конфиг античита не найден (" + TestPaths.AntiCheatConfig + ")");
        }

        // The decisive check: the pack we publish must contain exactly the folders ModSync
        // distributes today, otherwise players re-download mods (or miss ones the server has).
        if (Directory.Exists(TestPaths.ModsDir) && File.Exists(TestPaths.ModSyncConfig))
        {
            ModFilters pack = ModFilters.ForThisServer(TestPaths.ModsDir, false);
            HashSet<string> ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in GamePaths.SafeDirs(TestPaths.ModsDir))
            {
                string name = Path.GetFileName(dir);
                if (!pack.IsModExcluded(name)) { ours.Add(name); }
            }

            Dictionary<string, object> cfg = Json.ReadFile(TestPaths.ModSyncConfig);
            // tracked_mods is only the scan list: ModSync still drops the folders matched by
            // exclude_patterns ("ChunkKeepAlive/*" ...), and the served manifest has 164 mods.
            HashSet<string> folderExcluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object o in Json.Arr(cfg, "exclude_patterns"))
            {
                string pattern = Convert.ToString(o);
                if (pattern != null && pattern.EndsWith("/*", StringComparison.Ordinal) &&
                    pattern.IndexOf('/') == pattern.Length - 2)
                {
                    folderExcluded.Add(pattern.Substring(0, pattern.Length - 2));
                }
            }

            HashSet<string> theirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object o in Json.Arr(cfg, "tracked_mods"))
            {
                string name = Convert.ToString(o);
                if (string.IsNullOrEmpty(name) || folderExcluded.Contains(name)) { continue; }
                if (Directory.Exists(Path.Combine(TestPaths.ModsDir, name))) { theirs.Add(name); }
            }

            List<string> onlyOurs = new List<string>();
            foreach (string name in ours) { if (!theirs.Contains(name)) { onlyOurs.Add(name); } }
            List<string> onlyTheirs = new List<string>();
            foreach (string name in theirs) { if (!ours.Contains(name)) { onlyTheirs.Add(name); } }

            Console.WriteLine("     наборов: наши " + ours.Count + ", ModSync " + theirs.Count);
            if (onlyOurs.Count > 0) { Console.WriteLine("     только у нас: " + string.Join(", ", onlyOurs.ToArray())); }
            if (onlyTheirs.Count > 0) { Console.WriteLine("     только у ModSync: " + string.Join(", ", onlyTheirs.ToArray())); }
            Check(onlyOurs.Count == 0 && onlyTheirs.Count == 0, "набор совпадает со списком ModSync");
        }
        else
        {
            Console.WriteLine("  skip: нет Mods или конфига ModSync");
        }
    }

    // Compares the pack we publish with the one ModSync already serves. ModSync is reached
    // over its local HTTP API; when it is not running the check is skipped.
    private static void TestManifestParity()
    {
        Console.WriteLine("== паритет с ModSync ==");
        string msText;
        if (!Http.TryGet("http://127.0.0.1:8765/api/v2/manifest", 20000, out msText))
        {
            Console.WriteLine("  skip: ModSync не отвечает на 8765");
            return;
        }

        string publish = Path.Combine(GamePaths.SettingsDir("WolfServer"), "publish", "manifest.json");
        if (!File.Exists(publish))
        {
            Console.WriteLine("  skip: наш манифест ещё не опубликован");
            return;
        }

        HashSet<string> theirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, long> theirSize = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (object mo in Json.Arr(Json.ParseObject(msText), "mods"))
        {
            Dictionary<string, object> md = mo as Dictionary<string, object>;
            if (md == null) { continue; }
            foreach (object fo in Json.Arr(md, "files"))
            {
                Dictionary<string, object> fd = fo as Dictionary<string, object>;
                if (fd == null) { continue; }
                string path = Ascii(Json.Str(fd, "path", ""));
                if (path.Length == 0) { continue; }
                theirs.Add(path);
                theirSize[path] = Json.Long(fd, "size", 0);
            }
        }

        ModManifest ours = ModManifest.FromJson(File.ReadAllText(publish, System.Text.Encoding.UTF8));
        HashSet<string> mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, long> mySize = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (ManifestFile f in ours.files)
        {
            string path = Ascii(f.rel);
            mine.Add(path);
            mySize[path] = f.size;
        }

        List<string> onlyTheirs = new List<string>();
        foreach (string p in theirs) { if (!mine.Contains(p)) { onlyTheirs.Add(p); } }
        List<string> onlyMine = new List<string>();
        foreach (string p in mine) { if (!theirs.Contains(p)) { onlyMine.Add(p); } }
        int sizeDiff = 0;
        foreach (string p in theirs)
        {
            long s;
            if (mySize.TryGetValue(p, out s) && s != theirSize[p]) { sizeDiff++; }
        }

        Console.WriteLine("     файлов: ModSync " + theirs.Count + ", наши " + mine.Count +
                          "; только у ModSync " + onlyTheirs.Count + ", только у нас " + onlyMine.Count +
                          ", расхождений по размеру " + sizeDiff);
        foreach (string p in onlyTheirs) { Console.WriteLine("     - " + p); }
        foreach (string p in onlyMine) { Console.WriteLine("     + " + p); }

        Check(onlyTheirs.Count == 0 && onlyMine.Count == 0 && sizeDiff == 0,
              "набор файлов совпадает с тем, что раздаёт ModSync");
    }

    // Non-ASCII characters are dropped before comparison: the two manifests may be decoded
    // differently by different clients, but the file set must still match.
    private static string Ascii(string path)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder(path.Length);
        foreach (char c in path) { if (c >= 0x20 && c < 0x7F) { sb.Append(c); } }
        return sb.ToString();
    }

    private static void TestChunkStore()
    {
        Console.WriteLine("== ChunkStore ==");
        string dir = Path.Combine(Path.GetTempPath(), "wolf_test_store");
        if (Directory.Exists(dir)) { Directory.Delete(dir, true); }
        ChunkStore store = new ChunkStore(dir);

        byte[] payload = new byte[2 * 1024 * 1024 + 12345];
        new Random(7).NextBytes(payload);
        string file = Path.Combine(Path.GetTempPath(), "wolf_test_payload.bin");
        File.WriteAllBytes(file, payload);

        List<ChunkRef> refs = store.ImportFile(file);
        Check(refs.Count == 3, "файл разбит на 3 чанка, получено " + refs.Count);

        byte[] again = new byte[payload.Length];
        long pos = 0;
        foreach (ChunkRef c in refs)
        {
            byte[] data = store.Read(c.sha);
            Buffer.BlockCopy(data, 0, again, (int)pos, data.Length);
            pos += data.Length;
        }
        bool same = again.Length == payload.Length;
        for (int i = 0; same && i < payload.Length; i++) { same = again[i] == payload[i]; }
        Check(same, "содержимое восстановлено побайтово");

        string rebuilt = Path.Combine(Path.GetTempPath(), "wolf_test_rebuilt.bin");
        Check(store.ExportFile(rebuilt, refs, null), "ExportFile успешен");
        Check(ChunkStore.HashOfFile(rebuilt) == ChunkStore.HashOfFile(file), "хэш восстановленного файла совпадает");

        Check(!store.Put("0000000000000000000000000000000000000000000000000000000000000000", payload), "поддельный чанк отвергнут");
        Check(store.Put(ChunkStore.HashOf(payload), payload), "валидный чанк принят");

        File.Delete(file);
        File.Delete(rebuilt);
        Directory.Delete(dir, true);
    }

    private static void TestManifest()
    {
        Console.WriteLine("== ModManifest ==");
        string mods = TestPaths.ModsDir;
        if (!Directory.Exists(mods))
        {
            Console.WriteLine("  skip: папка Mods не найдена");
            return;
        }

        string dir = Path.Combine(Path.GetTempPath(), "wolf_test_manifest");
        if (Directory.Exists(dir)) { Directory.Delete(dir, true); }
        ChunkStore store = new ChunkStore(dir);

        // Build over a small subset so the test stays fast.
        string subset = Path.Combine(Path.GetTempPath(), "wolf_test_mods");
        if (Directory.Exists(subset)) { Directory.Delete(subset, true); }
        Directory.CreateDirectory(subset);
        int copied = 0;
        foreach (string modDir in GamePaths.SafeDirs(mods))
        {
            if (copied >= 3) { break; }
            string name = Path.GetFileName(modDir);
            string target = Path.Combine(subset, name);
            Directory.CreateDirectory(target);
            foreach (string f in GamePaths.SafeFiles(modDir, "*", SearchOption.TopDirectoryOnly))
            {
                try { File.Copy(f, Path.Combine(target, Path.GetFileName(f)), true); } catch { }
            }
            copied++;
        }

        string error;
        ModManifest m = ModManifest.Build(subset, store, "Wolf Test", "1", "самотест", out error);
        Check(error == null, "манифест собран без ошибок" + (error == null ? "" : ": " + error));
        Check(m.loadOrder.Count > 0, "модов в наборе: " + m.loadOrder.Count);
        Check(m.files.Count > 0, "файлов в наборе: " + m.files.Count + ", " + GamePaths.HumanSize(m.TotalBytes));
        Check(m.ContentHash.Length == 8, "хэш содержимого: " + m.ContentHash);

        string json = Json.Serialize(m.ToJson());
        ModManifest round = ModManifest.FromJson(json);
        Check(round.files.Count == m.files.Count, "сериализация/разбор сохраняют файлы");
        Check(round.ContentHash == m.ContentHash, "хэш стабилен после round-trip");

        // A second build over the same folder must produce the identical content hash.
        ModManifest again = ModManifest.Build(subset, store, "Другое имя", "9", "", out error);
        Check(again.ContentHash == m.ContentHash, "хэш не зависит от названия и версии");

        Directory.Delete(subset, true);
        Directory.Delete(dir, true);
    }

    // ---------- new in 1.2: diff, history, statistics, notifications ----------

    private static void TestCommonExtras()
    {
        TestManifestDiff();
        TestPackHistory();
        TestPlayerStats();
        TestNotifierAndText();
    }

    private static ModManifest SmallManifest(string mod, string version, string fileHash)
    {
        ModManifest m = new ModManifest();
        m.serverName = "Test";
        m.version = "1";
        m.loadOrder.Add(mod);
        m.modVersions[mod] = version;
        ManifestFile f = new ManifestFile();
        f.rel = mod + "/data.txt";
        f.size = 10;
        f.hash = fileHash;
        m.files.Add(f);
        return m;
    }

    private static void TestManifestDiff()
    {
        Console.WriteLine("== ManifestDiff ==");
        ModManifest a = new ModManifest();
        a.loadOrder.AddRange(new[] { "A", "B" });
        a.modVersions["A"] = "1.0";
        a.modVersions["B"] = "3.0";
        a.files.Add(new ManifestFile { rel = "A/x.txt", size = 10, hash = "h1" });
        a.files.Add(new ManifestFile { rel = "B/y.txt", size = 20, hash = "h2" });

        ModManifest b = new ModManifest();
        b.loadOrder.AddRange(new[] { "A", "C" });
        b.modVersions["A"] = "2.0";
        b.modVersions["C"] = "1.0";
        b.files.Add(new ManifestFile { rel = "A/x.txt", size = 10, hash = "h1" });
        b.files.Add(new ManifestFile { rel = "C/z.txt", size = 30, hash = "h3" });

        List<string> changes = ManifestDiff.Compare(a, b);
        Check(changes.Contains("~ A 1.0 → 2.0"), "обновление версии мода");
        Check(changes.Contains("+ C 1.0"), "добавленный мод");
        Check(changes.Contains("- B"), "убранный мод");
        Check(ManifestDiff.Summary(changes) == "1 обновлено, 1 добавлено, 1 убрано",
            "сводка изменений: " + ManifestDiff.Summary(changes));

        // Same version, different bytes: still a change.
        ModManifest c = SmallManifest("A", "1.0", "old");
        ModManifest d = SmallManifest("A", "1.0", "new");
        List<string> fileChange = ManifestDiff.Compare(c, d);
        Check(fileChange.Count == 1 && fileChange[0].Contains("файлы обновлены"), "изменение файлов без смены версии");
        Check(ManifestDiff.Compare(c, c).Count == 0, "одинаковые наборы не дают изменений");

        // First publish: everything is new.
        List<string> first = ManifestDiff.Compare(null, c);
        Check(first.Count == 1 && first[0].StartsWith("+ A"), "первая публикация: все моды новые");
        Check(ManifestDiff.Summary(new List<string>()) == "без изменений", "пустой список изменений");
    }

    private static void TestPackHistory()
    {
        Console.WriteLine("== PackHistory ==");
        string dir = Path.Combine(Path.GetTempPath(), "wolf_test_history");
        if (Directory.Exists(dir)) { Directory.Delete(dir, true); }
        Directory.CreateDirectory(dir);

        PackHistory h = new PackHistory();
        List<string> changes = new List<string> { "+ A 1.0", "~ B 1.0 → 1.1" };
        ModManifest m1 = SmallManifest("A", "1.0", "hash-one");
        PackEntry e1 = h.Add(m1, changes);
        Check(e1 != null && e1.hash == m1.ContentHash, "набор добавлен в историю");
        Check(e1.changes.Count == 2 && e1.files == 1, "изменения и число файлов сохранены");

        h.Add(m1, changes);
        Check(h.Entries.Count == 1, "повторная публикация того же хэша не дублируется");

        ModManifest m2 = SmallManifest("B", "2.0", "hash-two");
        h.Add(m2, null);
        Check(h.Entries.Count == 2 && h.Entries[0].hash == m2.ContentHash, "новый набор встаёт первым");

        string file = Path.Combine(dir, "packs.json");
        h.Save(file);
        PackHistory loaded = PackHistory.Load(file);
        Check(loaded.Entries.Count == 2, "история прочитана с диска");
        Check(loaded.Find(m1.ContentHash) != null, "поиск набора по хэшу");
        Check(loaded.Entries[0].built.Length >= 10, "дата публикации заполнена");

        loaded.Keep = 1;
        loaded.Trim();
        Check(loaded.Entries.Count == 1 && loaded.Find(m2.ContentHash) != null, "лимит истории обрезает старые записи");

        string snapshot = PackHistory.SnapshotFile(dir, m1.ContentHash);
        Check(snapshot.EndsWith(m1.ContentHash + ".json"), "путь снимка набора");
        Directory.Delete(dir, true);
    }

    private static GamePlayer Player(string name, string steam, string entity)
    {
        GamePlayer p = new GamePlayer();
        p.Name = name;
        p.SteamId = steam;
        p.EntityId = entity;
        return p;
    }

    private static void TestPlayerStats()
    {
        Console.WriteLine("== PlayerStats ==");
        PlayerStats stats = new PlayerStats();
        DateTime t0 = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

        bool joined = stats.Observe(new List<GamePlayer> { Player("Wolf", "Steam_1", "10") }, t0);
        Check(joined && stats.OnlineCount == 1, "вход игрока отмечен");
        Check(stats.TotalPlayers == 1 && stats.PeakOnline == 1, "счётчики игроков и пика");
        Check(stats.Active.Count == 1 && stats.Active[0].name == "Wolf", "активная сессия");

        bool steady = stats.Observe(new List<GamePlayer> { Player("Wolf", "Steam_1", "10") }, t0.AddSeconds(60));
        Check(!steady && stats.OnlineCount == 1, "повторный опрос не создаёт новую сессию");

        bool left = stats.Observe(new List<GamePlayer>(), t0.AddSeconds(120));
        Check(left && stats.OnlineCount == 0, "выход игрока отмечен");
        Check(stats.TotalSeconds >= 119, "время сессии учтено: " + stats.TotalSeconds + " с");

        stats.Observe(new List<GamePlayer> { Player("Wolf", "Steam_1", "10") }, t0.AddSeconds(180));
        Check(stats.TotalPlayers == 1, "вернувшийся игрок не считается новым");
        Check(stats.Sessions.Count == 2, "история хранит обе сессии");
        Check(stats.PeakOnline == 1, "пик онлайна не уменьшился");

        string dir = Path.Combine(Path.GetTempPath(), "wolf_test_stats");
        if (Directory.Exists(dir)) { Directory.Delete(dir, true); }
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "players.json");
        stats.Save(file);
        PlayerStats loaded = PlayerStats.Load(file);
        Check(loaded.TotalSeconds == stats.TotalSeconds && loaded.TotalPlayers == 1, "статистика прочитана с диска");
        Check(loaded.OnlineCount == 0, "после перезапуска активных сессий нет");
        Check(loaded.Sessions.Count == 2 && loaded.Sessions[0].leftUtc != DateTime.MinValue,
            "незакрытая сессия закрыта временем последнего опроса");

        loaded.Reset();
        Check(loaded.Sessions.Count == 0 && loaded.PeakOnline == 0 && loaded.TotalPlayers == 0, "сброс статистики");
        Directory.Delete(dir, true);
    }

    private static void TestNotifierAndText()
    {
        Console.WriteLine("== Notifier / текст ==");
        Check(Notifier.TelegramUrl("ABC") == "https://api.telegram.org/botABC/sendMessage", "URL Telegram");
        Dictionary<string, string> tg = Notifier.TelegramFields("123", "привет");
        Check(tg["chat_id"] == "123" && tg["text"] == "привет", "поля Telegram");
        Check(tg["disable_web_page_preview"] == "true", "Telegram без превью ссылок");

        Dictionary<string, string> vk = Notifier.VkFields("tok", "456", "текст", 7, "5.199");
        Check(vk["peer_id"] == "456" && vk["random_id"] == "7" && vk["v"] == "5.199", "поля VK");
        Check(Notifier.VkFields("tok", "1", "x", 1, "").ContainsKey("v"), "VK: версия по умолчанию");

        Check(Notifier.Mask("1234567890") == "1234…7890", "токен маскируется в журнале");
        Check(Notifier.Mask("short") == "*****", "короткий токен скрыт целиком");
        Check(Notifier.TelegramFields("1", new string('x', 4000))["text"].Length == 3500,
            "длинное сообщение обрезается до лимита Telegram");

        Check(WolfInfo.HtmlEscape("<a href=\"x\">&'") == "&lt;a href=&quot;x&quot;&gt;&amp;&#39;",
            "HTML-экранирование");
        Check(WolfInfo.HtmlEscape(null) == "", "экранирование null");
        Check(WolfInfo.HumanDuration(TimeSpan.FromMinutes(47)) == "47 мин", "длительность: минуты");
        Check(WolfInfo.HumanDuration(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(13)) == "2 ч 13 мин",
            "длительность: часы");
        Check(WolfInfo.HumanDuration(TimeSpan.FromDays(3) + TimeSpan.FromHours(4)) == "3 д 4 ч",
            "длительность: дни");
        Check(!string.IsNullOrEmpty(WolfInfo.Version) && WolfInfo.Version != "?", "версия сборки доступна: " + WolfInfo.Version);
    }
}
