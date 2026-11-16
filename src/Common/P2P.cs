using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Wolf
{
    // Line-delimited JSON messages over TCP. One request, one reply, then the socket
    // may stay open for more requests. Chosen over real BitTorrent on purpose:
    // same content-addressed chunk idea, no protocol complexity, trivially firewalled-in
    // because the seed side always dials out and the peer side only answers on its port.
    internal static class Wire
    {
        public static void Write(Stream s, string payload)
        {
            byte[] data = Encoding.UTF8.GetBytes(payload + "\n");
            s.Write(data, 0, data.Length);
            s.Flush();
        }

        public static string ReadLine(Stream s)
        {
            MemoryStream ms = new MemoryStream(4096);
            int b;
            while ((b = s.ReadByte()) >= 0)
            {
                if (b == '\n') { break; }
                ms.WriteByte((byte)b);
                if (ms.Length > 8 * 1024 * 1024) { throw new IOException("line too long"); }
            }
            if (b < 0 && ms.Length == 0) { return null; }
            return Encoding.UTF8.GetString(ms.ToArray()).Trim();
        }

        public static Dictionary<string, object> ReadJson(Stream s)
        {
            string line = ReadLine(s);
            return line == null ? null : Json.ParseObject(line);
        }
    }

    // Info about one peer that is seeding a mod pack.
    public sealed class PeerInfo
    {
        public string ip;
        public int port;
        public string steamid = "";
        public string name = "";
        public string hash = "";
        public int files;
        public long bytes;
        public DateTime seenUtc = DateTime.UtcNow;
        public bool isServer;

        public Dictionary<string, object> ToJson()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ip"] = ip;
            d["port"] = port;
            d["steamid"] = steamid;
            d["name"] = name;
            d["hash"] = hash;
            d["files"] = files;
            d["bytes"] = bytes;
            d["seen"] = seenUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            d["server"] = isServer;
            return d;
        }

        public static PeerInfo FromJson(Dictionary<string, object> d)
        {
            PeerInfo p = new PeerInfo();
            p.ip = Json.Str(d, "ip", "");
            p.port = Json.Int(d, "port", 0);
            p.steamid = Json.Str(d, "steamid", "");
            p.name = Json.Str(d, "name", "");
            p.hash = Json.Str(d, "hash", "");
            p.files = Json.Int(d, "files", 0);
            p.bytes = Json.Long(d, "bytes", 0);
            p.isServer = Json.Bool(d, "server", false);
            return p;
        }
    }

    // Tracker + registry of who has which chunks. Runs inside the server tool.
    public sealed class Tracker
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, PeerInfo> peers = new Dictionary<string, PeerInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan ttl = TimeSpan.FromMinutes(4);

        public event Action<string> OnEvent;

        public void Register(PeerInfo p)
        {
            if (string.IsNullOrEmpty(p.ip) || p.port <= 0) { return; }
            string key = p.ip + ":" + p.port;
            lock (gate)
            {
                PeerInfo old;
                if (peers.TryGetValue(key, out old) && !string.IsNullOrEmpty(p.name) && old.name != p.name)
                {
                    if (OnEvent != null) { OnEvent("переименовался: " + old.name + " -> " + p.name); }
                }
                peers[key] = p;
            }
        }

        // Peers holding the same content, most recently seen first, excluding the requester.
        public List<PeerInfo> Neighbours(string hash, string selfKey, int max)
        {
            List<PeerInfo> result = new List<PeerInfo>();
            DateTime cutoff = DateTime.UtcNow - ttl;
            lock (gate)
            {
                foreach (KeyValuePair<string, PeerInfo> kv in peers)
                {
                    if (kv.Value.seenUtc < cutoff) { continue; }
                    if (hash != null && !string.IsNullOrEmpty(kv.Value.hash) && kv.Value.hash != hash) { continue; }
                    if (selfKey != null && string.Equals(kv.Key, selfKey, StringComparison.OrdinalIgnoreCase)) { continue; }
                    result.Add(kv.Value);
                }
            }
            // Server seed first: it always has everything.
            return result.OrderByDescending(p => p.isServer).ThenByDescending(p => p.seenUtc).Take(max).ToList();
        }

        public List<PeerInfo> All()
        {
            DateTime cutoff = DateTime.UtcNow - ttl;
            lock (gate)
            {
                return peers.Values.Where(p => p.seenUtc >= cutoff).OrderByDescending(p => p.seenUtc).ToList();
            }
        }

        public void Drop(string ip, int port)
        {
            lock (gate) { peers.Remove(ip + ":" + port); }
        }

        public void Prune()
        {
            DateTime cutoff = DateTime.UtcNow - ttl;
            lock (gate)
            {
                List<string> dead = peers.Where(kv => kv.Value.seenUtc < cutoff).Select(kv => kv.Key).ToList();
                foreach (string k in dead) { peers.Remove(k); }
            }
        }

        // Wires the tracker onto an HTTP server: /announce, /peers, /who.
        public void Map(MiniHttp http)
        {
            http.Map("/announce", ctx =>
            {
                Dictionary<string, object> req = ctx.Method == "POST" ? Json.ParseObject(ctx.Body) : QueryAsObject(ctx);
                PeerInfo p = PeerInfo.FromJson(req);
                if (string.IsNullOrEmpty(p.ip) || p.port <= 0)
                {
                    p.ip = ctx.RemoteIp;
                }
                Register(p);
                List<object> list = new List<object>();
                foreach (PeerInfo n in Neighbours(p.hash, p.ip + ":" + p.port, 25)) { list.Add(n.ToJson()); }
                Dictionary<string, object> res = new Dictionary<string, object>();
                res["interval"] = 60;
                res["peers"] = list;
                ctx.Ok(res);
            });

            http.Map("/peers", ctx =>
            {
                List<object> list = new List<object>();
                foreach (PeerInfo p in All()) { list.Add(p.ToJson()); }
                Dictionary<string, object> res = new Dictionary<string, object>();
                res["count"] = list.Count;
                res["peers"] = list;
                ctx.Ok(res);
            });

            http.Map("/who", ctx =>
            {
                string ip = ctx.RemoteIp;
                List<object> list = new List<object>();
                foreach (PeerInfo p in All().Where(p => p.ip == ip)) { list.Add(p.ToJson()); }
                ctx.Ok(list);
            });
        }

        private static Dictionary<string, object> QueryAsObject(HttpCtx ctx)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ip"] = ctx.Query("ip");
            d["port"] = ctx.Query("port");
            d["steamid"] = ctx.Query("steamid");
            d["name"] = ctx.Query("name");
            d["hash"] = ctx.Query("hash");
            d["server"] = ctx.Query("server");
            d["bytes"] = ctx.Query("bytes");
            return d;
        }
    }

    // TCP endpoint that answers chunk requests from other peers and periodically
    // announces itself to the server tracker. Both the launcher and the server tool run one.
    public sealed class PeerNode : IDisposable
    {
        private readonly ChunkStore store;
        private TcpListener listener;
        private Thread worker;
        private Thread announcer;
        private volatile bool running;

        public string Hash = "";
        public string SteamId = "";
        public string DisplayName = "";
        public bool IsSeed;
        public int Port { get; private set; }

        public event Action<string> OnEvent;
        public event Action<string, bool> OnChunkServed;

        public PeerNode(ChunkStore store) { this.store = store; }

        // Tries the requested port, then walks a short range so a busy port is not fatal.
        public bool Start(int preferredPort)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int port = preferredPort + attempt;
                try
                {
                    TcpListener l = new TcpListener(IPAddress.Any, port);
                    l.Start();
                    listener = l;
                    Port = port;
                    break;
                }
                catch { }
            }
            if (listener == null)
            {
                if (OnEvent != null) { OnEvent("P2P: не удалось открыть порт " + preferredPort + "…" + (preferredPort + 7)); }
                return false;
            }

            running = true;
            worker = new Thread(AcceptLoop) { IsBackground = true, Name = "PeerNode" };
            worker.Start();
            if (OnEvent != null) { OnEvent("P2P: порт " + Port + " открыт"); }
            return true;
        }

        public void AnnounceTo(string serverUrl, int intervalSeconds)
        {
            if (announcer != null) { return; }
            announcer = new Thread(delegate()
            {
                while (running)
                {
                    AnnounceOnce(serverUrl);
                    for (int i = 0; i < intervalSeconds && running; i++) { Thread.Sleep(1000); }
                }
            }) { IsBackground = true, Name = "PeerAnnounce" };
            announcer.Start();
        }

        public bool AnnounceOnce(string serverUrl)
        {
            if (string.IsNullOrEmpty(serverUrl) || !running) { return false; }
            string url = serverUrl.TrimEnd('/') + "/announce"
                + "?port=" + Port.ToString(CultureInfo.InvariantCulture)
                + "&steamid=" + Uri.EscapeDataString(SteamId ?? "")
                + "&name=" + Uri.EscapeDataString(DisplayName ?? "")
                + "&hash=" + Uri.EscapeDataString(Hash ?? "")
                + "&server=" + (IsSeed ? "1" : "0")
                + "&bytes=" + store.TotalSize().ToString(CultureInfo.InvariantCulture);
            string text;
            return Http.TryGet(url, 6000, out text);
        }

        // Asks the tracker who else has this content hash.
        public static List<PeerInfo> QueryPeers(string serverUrl, string hash, int timeoutMs)
        {
            List<PeerInfo> result = new List<PeerInfo>();
            string text;
            if (!Http.TryGet(serverUrl.TrimEnd('/') + "/peers", timeoutMs, out text)) { return result; }
            foreach (object o in Json.Arr(Json.ParseObject(text), "peers"))
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null) { continue; }
                PeerInfo p = PeerInfo.FromJson(d);
                if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(p.hash) || p.hash == hash) { result.Add(p); }
            }
            return result;
        }

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { return; }
                ThreadPool.QueueUserWorkItem(delegate(object state) { Serve((TcpClient)state); }, client);
            }
        }

        private void Serve(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 20000;
                client.SendTimeout = 60000;
                using (client)
                using (NetworkStream ns = client.GetStream())
                {
                    while (running)
                    {
                        Dictionary<string, object> req = Wire.ReadJson(ns);
                        if (req == null) { return; }
                        string cmd = Json.Str(req, "cmd", "");

                        if (cmd == "hello")
                        {
                            Dictionary<string, object> res = new Dictionary<string, object>();
                            res["ok"] = true;
                            res["hash"] = Hash ?? "";
                            res["name"] = DisplayName ?? "";
                            res["seed"] = IsSeed;
                            Wire.Write(ns, Json.Serialize(res));
                            continue;
                        }

                        if (cmd == "want")
                        {
                            List<object> have = new List<object>();
                            foreach (object o in Json.Arr(req, "chunks"))
                            {
                                string sha = Convert.ToString(o);
                                if (!string.IsNullOrEmpty(sha) && store.Has(sha, 0)) { have.Add(sha); }
                            }
                            Dictionary<string, object> res = new Dictionary<string, object>();
                            res["ok"] = true;
                            res["have"] = have;
                            Wire.Write(ns, Json.Serialize(res));
                            continue;
                        }

                        if (cmd == "get")
                        {
                            string sha = Json.Str(req, "h", "");
                            byte[] data = string.IsNullOrEmpty(sha) ? null : store.Read(sha);
                            Dictionary<string, object> res = new Dictionary<string, object>();
                            res["ok"] = data != null;
                            if (data != null)
                            {
                                res["d"] = Convert.ToBase64String(data);
                                res["s"] = (long)data.Length;
                            }
                            if (OnChunkServed != null) { OnChunkServed(sha, data != null); }
                            Wire.Write(ns, Json.Serialize(res));
                            continue;
                        }

                        Dictionary<string, object> bad = new Dictionary<string, object>();
                        bad["ok"] = false;
                        bad["error"] = "unknown cmd";
                        Wire.Write(ns, Json.Serialize(bad));
                    }
                }
            }
            catch { }
        }

        public void Stop()
        {
            running = false;
            try { if (listener != null) { listener.Stop(); } } catch { }
        }

        public void Dispose() { Stop(); }
    }

    // Downloads missing chunks: tries peers (closest first, server seed included),
    // falls back to plain HTTP from the server, verifies every chunk by hash.
    public sealed class ChunkFetcher
    {
        // Сколько чанков тянем с сервера одновременно. 1 МиБ на чанк и 8 потоков дают
        // нормальную загрузку канала вместо ожидания каждого отдельного запроса.
        private const int MaxHttpWorkers = 12;

        private readonly ChunkStore store;
        private readonly int timeoutMs;

        public ChunkFetcher(ChunkStore store, int timeoutMs)
        {
            this.store = store;
            this.timeoutMs = timeoutMs;
        }

        public event Action<string> OnEvent;
        public event Action<long, long, string> OnTransfer;   // bytes done, bytes total, source

        private volatile bool cancelled;

        public bool Cancelled { get { return cancelled; } }
        public void Cancel() { cancelled = true; }

        // Счётчики прогресса общие для потоков сервера и пиров.
        private sealed class Tally
        {
            public readonly object gate = new object();
            public int done;
            public int total;
            public long doneBytes;
            public long totalBytes;
            public Action<int, int, string> progress;
            public Action<long, long, string> transfer;
            public Action<string> log;

            public void Add(ChunkRef c, string source)
            {
                lock (gate)
                {
                    done++;
                    doneBytes += c.size;
                    if (progress != null) { progress(done, total, source); }
                    if (transfer != null) { transfer(doneBytes, totalBytes, source); }
                }
            }

            public void Say(string message) { if (log != null) { log(message); } }
        }

        // Returns true when every chunk ended up in the store.
        // Сервер и пиры работают одновременно: сервер тянет основную часть чанков, пиры —
        // свою долю параллельно. Раньше пиры пробовались до HTTP, и один медленный пир
        // останавливал загрузку целиком.
        public bool Fetch(IList<ChunkRef> needed, IList<PeerInfo> peers, string httpBase, Action<int, int, string> progress)
        {
            if (cancelled) { return false; }
            if (needed == null || needed.Count == 0) { return true; }

            List<ChunkRef> missing = needed.Where(c => !store.Has(c.sha, c.size)).ToList();
            if (missing.Count == 0) { return true; }

            Tally tally = new Tally();
            tally.done = needed.Count - missing.Count;
            tally.total = needed.Count;
            tally.totalBytes = missing.Sum(c => c.size);
            tally.progress = progress;
            tally.transfer = OnTransfer;
            tally.log = OnEvent;

            string baseUrl = string.IsNullOrEmpty(httpBase) ? null : httpBase.TrimEnd('/');
            List<PeerInfo> livePeers = peers == null ? new List<PeerInfo>() : peers.Take(3).ToList();

            List<ChunkRef> httpChunks = new List<ChunkRef>(missing);
            List<ChunkRef> peerChunks = new List<ChunkRef>();
            // Порог в 8 чанков имеет смысл только когда есть и сервер, и пиры: делить
            // маленький остаток между ними невыгодно. Но если HTTP-базы нет вообще
            // (режим «только пиры»), пиры — единственный источник, и порог их отключал:
            // пак меньше 8 чанков не качался совсем (NetTest: 0/0).
            bool peersOnly = baseUrl == null;
            if (livePeers.Count > 0 && missing.Count > 0 && (peersOnly || missing.Count >= 8))
            {
                if (peersOnly)
                {
                    peerChunks = new List<ChunkRef>(missing);
                    httpChunks.Clear();
                }
                else
                {
                    int share = Math.Max(1, missing.Count / 4);   // четверть — пирам, остальное серверу
                    peerChunks = httpChunks.GetRange(0, share);
                    httpChunks.RemoveRange(0, share);
                }
            }

            List<ChunkRef> peerLeft = new List<ChunkRef>();
            object peerGate = new object();
            Thread peerThread = null;
            if (peerChunks.Count > 0)
            {
                peerThread = new Thread(delegate()
                {
                    List<ChunkRef> left = new List<ChunkRef>(peerChunks);
                    foreach (PeerInfo peer in livePeers)
                    {
                        if (left.Count == 0 || cancelled) { break; }
                        left = FromPeer(peer, left, delegate(ChunkRef c) { tally.Add(c, peer.name + " (" + peer.ip + ")"); });
                    }
                    lock (peerGate) { peerLeft.AddRange(left); }
                });
                peerThread.IsBackground = true;
                peerThread.Name = "ChunkPeers";
                peerThread.Start();
            }

            List<ChunkRef> still = baseUrl == null ? new List<ChunkRef>(httpChunks) : FromHttp(httpChunks, baseUrl, tally);

            if (peerThread != null) { peerThread.Join(); }
            lock (peerGate) { still.AddRange(peerLeft); }

            // Добор: что не пришло ни с сервера, ни с пиров (оборванные соединения).
            if (still.Count > 0 && !cancelled && baseUrl != null)
            {
                still = FromHttp(still, baseUrl, tally);
            }

            if (cancelled) { return false; }
            return missing.All(c => store.Has(c.sha, c.size));
        }

        // Чанки с сервера: пул потоков, общий счётчик и общий список неудач.
        private List<ChunkRef> FromHttp(List<ChunkRef> wanted, string baseUrl, Tally tally)
        {
            List<ChunkRef> failed = new List<ChunkRef>();
            if (wanted == null || wanted.Count == 0 || cancelled) { return failed; }

            string source = "сервер (HTTP)";
            object gate = new object();
            int next = 0;
            int workers = Math.Min(MaxHttpWorkers, wanted.Count);
            List<Thread> threads = new List<Thread>();

            for (int w = 0; w < workers; w++)
            {
                Thread t = new Thread(delegate()
                {
                    while (!cancelled)
                    {
                        ChunkRef c;
                        lock (gate)
                        {
                            if (next >= wanted.Count) { return; }
                            c = wanted[next++];
                        }
                        if (store.Has(c.sha, c.size)) { continue; }

                        byte[] data;
                        if (!Http.TryGetBytesRetry(baseUrl + "/chunk/" + c.sha, timeoutMs, 3, out data))
                        {
                            tally.Say("чанк " + Short(c.sha) + " не получен, пропускаю");
                            lock (gate) { failed.Add(c); }
                            continue;
                        }

                        bool stored;
                        try { stored = store.Put(c.sha, data); }
                        catch { stored = store.Has(c.sha, c.size); }   // гонка двух потоков на один чанк
                        if (!stored)
                        {
                            tally.Say("хэш не совпал: " + Short(c.sha));
                            lock (gate) { failed.Add(c); }
                            continue;
                        }

                        tally.Add(c, source);
                    }
                });
                t.IsBackground = true;
                t.Name = "ChunkHttp" + w;
                t.Start();
                threads.Add(t);
            }
            foreach (Thread t in threads) { t.Join(); }
            return failed;
        }

        private List<ChunkRef> FromPeer(PeerInfo peer, List<ChunkRef> wanted, Action<ChunkRef> onGot)
        {
            List<ChunkRef> rest = new List<ChunkRef>(wanted);
            try
            {
                using (TcpClient tcp = new TcpClient())
                {
                    IAsyncResult r = tcp.BeginConnect(peer.ip, peer.port, null, null);
                    if (!r.AsyncWaitHandle.WaitOne(timeoutMs)) { return rest; }
                    tcp.EndConnect(r);
                    tcp.ReceiveTimeout = timeoutMs * 2;
                    tcp.SendTimeout = timeoutMs;
                    using (NetworkStream ns = tcp.GetStream())
                    {
                        // Ask which chunks this peer actually has, then pull them one by one.
                        Dictionary<string, object> want = new Dictionary<string, object>();
                        want["cmd"] = "want";
                        want["chunks"] = rest.Select(x => (object)x.sha).ToList();
                        Wire.Write(ns, Json.Serialize(want));
                        Dictionary<string, object> haveRes = Wire.ReadJson(ns);
                        if (haveRes == null) { return rest; }

                        HashSet<string> have = new HashSet<string>(
                            Json.Arr(haveRes, "have").Select(o => Convert.ToString(o)), StringComparer.OrdinalIgnoreCase);
                        if (have.Count == 0) { return rest; }

                        foreach (ChunkRef c in rest.Where(x => have.Contains(x.sha)).ToList())
                        {
                            if (cancelled) { break; }
                            Dictionary<string, object> get = new Dictionary<string, object>();
                            get["cmd"] = "get";
                            get["h"] = c.sha;
                            Wire.Write(ns, Json.Serialize(get));
                            Dictionary<string, object> res = Wire.ReadJson(ns);
                            if (res == null || !Json.Bool(res, "ok", false)) { continue; }
                            byte[] data;
                            try { data = Convert.FromBase64String(Json.Str(res, "d", "")); }
                            catch { continue; }
                            if (!store.Put(c.sha, data)) { continue; }
                            if (onGot != null) { onGot(c); }
                        }
                    }
                }
            }
            catch
            {
                if (OnEvent != null) { OnEvent("пир " + peer.ip + ":" + peer.port + " недоступен"); }
            }
            return rest.Where(x => !store.Has(x.sha, x.size)).ToList();
        }

        private static string Short(string sha)
        {
            return sha == null || sha.Length <= 8 ? sha : sha.Substring(0, 8);
        }
    }
}


