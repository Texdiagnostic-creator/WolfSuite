using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Wolf;

// End-to-end check of the distribution path used by both apps:
// build manifest -> serve over MiniHttp + PeerNode -> fetch into a second store -> rebuild files.
internal static class NetTest
{
    private static int failures;

    private static void Check(bool condition, string what)
    {
        if (condition) { Console.WriteLine("  ok   " + what); }
        else { Console.WriteLine("  FAIL " + what); failures++; }
    }

    private static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string work = Path.Combine(Path.GetTempPath(), "wolf_net_test");
        if (Directory.Exists(work)) { Directory.Delete(work, true); }
        Directory.CreateDirectory(work);

        string modsDir = Path.Combine(work, "servermods");
        string publish = Path.Combine(work, "publish");
        string serverStoreDir = Path.Combine(work, "server_chunks");
        string clientStoreDir = Path.Combine(work, "client_chunks");
        string clientMods = Path.Combine(work, "clientmods");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(publish);

        // Two fake mods, one of them with a file bigger than a single chunk.
        string modA = Path.Combine(modsDir, "0-Core");
        string modB = Path.Combine(modsDir, "1-Vehicles");
        Directory.CreateDirectory(modA);
        Directory.CreateDirectory(Path.Combine(modA, "Config"));
        Directory.CreateDirectory(modB);

        File.WriteAllText(Path.Combine(modA, "ModInfo.xml"),
            "<?xml version=\"1.0\"?><xml><ModInfo><Name value=\"Core\"/><Version value=\"1.2.3\"/></ModInfo></xml>");
        File.WriteAllText(Path.Combine(modA, "Config", "blocks.xml"), "<blocks><block name=\"a\"/></blocks>");
        byte[] big = new byte[3 * 1024 * 1024 + 777];
        new Random(11).NextBytes(big);
        File.WriteAllBytes(Path.Combine(modB, "assets.bundle"), big);
        File.WriteAllText(Path.Combine(modB, "readme.txt"), "тестовый мод");

        Console.WriteLine("== build + publish ==");
        ChunkStore serverStore = new ChunkStore(serverStoreDir);
        string error;
        ModManifest manifest = ModManifest.Build(modsDir, serverStore, "Wolf Net Test", "1", "", out error);
        Check(error == null, "манифест собран" + (error == null ? "" : ": " + error));
        Check(manifest.files.Count == 4, "файлов в манифесте: " + manifest.files.Count);
        Check(manifest.loadOrder.Count == 2, "модов: " + manifest.loadOrder.Count);
        Console.WriteLine("     хэш набора: " + manifest.ContentHash + ", размер: " + GamePaths.HumanSize(manifest.TotalBytes));

        Json.WriteFile(Path.Combine(publish, "manifest.json"), manifest.ToJson(), true);

        Console.WriteLine("== start http + peer ==");
        MiniHttp http = new MiniHttp();
        http.OnError += delegate(string s) { Console.WriteLine("     http: " + s); };
        http.Map("/manifest.json", ctx => ctx.SendFile(Path.Combine(publish, "manifest.json")));
        http.Map("/chunk", ctx =>
        {
            string sha = ctx.Path.Substring("/chunk".Length).TrimStart('/');
            byte[] data = string.IsNullOrEmpty(sha) ? null : serverStore.Read(sha);
            if (data == null) { ctx.NotFound(); return; }
            ctx.WriteBody(200, "application/octet-stream", data);
        });
        Tracker tracker = new Tracker();
        tracker.Map(http);
        http.Bind(18765);
        Check(http.IsRunning, "HTTP слушает порт " + http.Port);

        PeerNode seed = new PeerNode(serverStore);
        seed.Hash = manifest.ContentHash;
        seed.DisplayName = "seed";
        seed.IsSeed = true;
        Check(seed.Start(16893), "сид открыл P2P-порт " + seed.Port);

        string baseUrl = "http://127.0.0.1:" + http.Port;
        Check(seed.AnnounceOnce(baseUrl), "сид зарегистрировался в трекере");

        Console.WriteLine("== client: manifest ==");
        ChunkStore clientStore = new ChunkStore(clientStoreDir);
        string text = Http.Get(baseUrl + "/manifest.json", 10000);
        ModManifest received = ModManifest.FromJson(text);
        Check(received.files.Count == manifest.files.Count, "клиент получил манифест");
        Check(received.ContentHash == manifest.ContentHash, "хэш совпадает");

        Console.WriteLine("== client: peers from tracker ==");
        List<PeerInfo> peers = PeerNode.QueryPeers(baseUrl, received.ContentHash, 8000);
        Check(peers.Count >= 1, "трекер вернул пиров: " + peers.Count);
        bool sawSeed = false;
        foreach (PeerInfo p in peers) { if (p.isServer && p.port == seed.Port) { sawSeed = true; } }
        Check(sawSeed, "среди пиров есть сид на порту " + seed.Port);

        Console.WriteLine("== client: download via P2P ==");
        List<ChunkRef> all = new List<ChunkRef>();
        foreach (ManifestFile f in received.files) { all.AddRange(f.chunks); }
        Console.WriteLine("     чанков в манифесте: " + all.Count + ", пиров: " + peers.Count);
        ChunkFetcher p2pFetcher = new ChunkFetcher(clientStore, 8000);
        p2pFetcher.OnEvent += delegate(string s) { Console.WriteLine("     [fetch] " + s); };
        int lastDone = 0, lastTotal = 0;
        bool ok = p2pFetcher.Fetch(all, peers, null, delegate(int done, int total, string src)
        {
            lastDone = done;
            lastTotal = total;
        });
        Console.WriteLine("     результат Fetch: " + ok + ", прогресс " + lastDone + "/" + lastTotal +
                          ", в хранилище " + clientStore.TotalSize() + " Б");
        Check(ok, "чанки получены через P2P: " + lastDone + "/" + lastTotal);
        Check(clientStore.TotalSize() > 0, "хранилище клиента: " + GamePaths.HumanSize(clientStore.TotalSize()));

        Console.WriteLine("== client: materialise files ==");
        foreach (ManifestFile f in received.files)
        {
            string target = Path.Combine(clientMods, f.rel.Replace('/', Path.DirectorySeparatorChar));
            Check(clientStore.ExportFile(target, f.chunks, null), "собран " + f.rel);
        }
        Check(ChunkStore.HashOfFile(Path.Combine(clientMods, "1-Vehicles", "assets.bundle")) ==
              ChunkStore.HashOfFile(Path.Combine(modB, "assets.bundle")), "большой файл совпал побайтово");
        Check(ChunkStore.HashOfFile(Path.Combine(clientMods, "0-Core", "ModInfo.xml")) ==
              ChunkStore.HashOfFile(Path.Combine(modA, "ModInfo.xml")), "ModInfo.xml совпал");

        Console.WriteLine("== client: second store fetches via HTTP only ==");
        string httpOnlyDir = Path.Combine(work, "http_only_chunks");
        ChunkStore httpStore = new ChunkStore(httpOnlyDir);
        ChunkFetcher httpFetcher = new ChunkFetcher(httpStore, 8000);
        bool okHttp = httpFetcher.Fetch(all, new List<PeerInfo>(), baseUrl, null);
        Check(okHttp, "чанки получены по HTTP без пиров");
        Check(httpStore.TotalSize() == clientStore.TotalSize(), "размеры хранилищ совпадают");

        Console.WriteLine("== bad data is rejected ==");
        Check(!httpStore.Put(new string('a', 64), new byte[] { 1, 2, 3 }), "чанк с неверным хэшем отвергнут");

        Console.WriteLine("== shutdown ==");
        seed.Stop();
        http.Stop();
        Thread.Sleep(300);
        Check(!http.IsRunning, "HTTP остановлен");

        try { Directory.Delete(work, true); } catch { }

        Console.WriteLine(failures == 0 ? "\nALL NET TESTS PASSED" : "\nFAILURES: " + failures);
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
