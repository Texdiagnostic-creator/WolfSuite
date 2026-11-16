using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Wolf
{
    // HTTP/1.1 server written directly on TcpListener.
    // Deliberately avoids HttpListener: that one needs an urlacl reservation (admin rights)
    // to bind a non-loopback address, and both the server tool and every peer must be
    // reachable from the LAN/Internet while running as a normal user.
    public sealed class MiniHttp : IDisposable
    {
        private TcpListener listener;
        private readonly Dictionary<string, HttpHandler> routes = new Dictionary<string, HttpHandler>(StringComparer.OrdinalIgnoreCase);
        private volatile bool running;
        private Thread worker;

        public delegate void HttpHandler(HttpCtx ctx);

        public event Action<string> OnError;

        public int Port { get; private set; }
        public bool IsRunning { get { return running; } }

        public void Map(string path, HttpHandler handler)
        {
            lock (routes) { routes[Normalize(path)] = handler; }
        }

        // Serves a folder; used for the manifest and for hash-named chunk files.
        public void MapFolder(string path, string folder)
        {
            string prefix = Normalize(path);
            Map(path, ctx =>
            {
                string rel = ctx.Path.Length > prefix.Length ? ctx.Path.Substring(prefix.Length).TrimStart('/') : "";
                if (rel.Contains("..")) { ctx.NotFound(); return; }
                if (string.IsNullOrEmpty(rel)) { ctx.NotFound(); return; }
                string root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
                string full = Path.GetFullPath(root + rel.Replace('/', '\\'));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                {
                    ctx.NotFound();
                    return;
                }
                ctx.SendFile(full);
            });
        }

        public void Bind(int port)
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            Port = port;
            running = true;
            worker = new Thread(AcceptLoop) { IsBackground = true, Name = "MiniHttp" };
            worker.Start();
        }

        public void Stop()
        {
            running = false;
            try { if (listener != null) { listener.Stop(); } } catch { }
        }

        public void Dispose() { Stop(); }

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
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 120000;
                using (client)
                using (NetworkStream ns = client.GetStream())
                {
                    HttpCtx ctx = HttpCtx.Read(ns, client);
                    if (ctx == null) { return; }
                    Route(ctx, ns);
                }
            }
            catch (Exception ex)
            {
                if (OnError != null) { OnError("http: " + ex.Message); }
            }
        }

        private void Route(HttpCtx ctx, NetworkStream ns)
        {
            try
            {
                HttpHandler handler;
                string path = Normalize(ctx.Path);
                if (!routes.TryGetValue(path, out handler))
                {
                    lock (routes)
                    {
                        foreach (KeyValuePair<string, HttpHandler> kv in routes)
                        {
                            if (path.StartsWith(kv.Key + "/", StringComparison.OrdinalIgnoreCase))
                            {
                                handler = kv.Value;
                                break;
                            }
                        }
                    }
                }
                if (handler == null) { ctx.NotFound(); return; }
                handler(ctx);
            }
            catch (Exception ex)
            {
                if (OnError != null) { OnError(ctx.Path + " -> " + ex.Message); }
                try { ctx.Error(ex.Message); } catch { }
            }
            finally
            {
                try { ns.Flush(); } catch { }
            }
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) { return "/"; }
            string p = path.Trim();
            if (!p.StartsWith("/")) { p = "/" + p; }
            if (p.EndsWith("/")) { p = p.Substring(0, p.Length - 1); }
            return p.Length == 0 ? "/" : p;
        }

        internal static string GuessMime(string file)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            switch (ext)
            {
                case ".json": return "application/json; charset=utf-8";
                case ".xml": return "application/xml; charset=utf-8";
                case ".txt": return "text/plain; charset=utf-8";
                case ".html":
                case ".htm": return "text/html; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".ico": return "image/x-icon";
                case ".zip": return "application/zip";
                default: return "application/octet-stream";
            }
        }
    }

    // Parsed request plus the stream used to answer it.
    public sealed class HttpCtx
    {
        private readonly NetworkStream ns;
        private bool responded;

        public string Method = "GET";
        public string Path = "/";
        public string RemoteIp = "";
        public string RawQuery = "";
        private byte[] body = new byte[0];
        private readonly Dictionary<string, string> args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private HttpCtx(NetworkStream stream) { ns = stream; }

        public static HttpCtx Read(NetworkStream ns, TcpClient client)
        {
            HttpCtx ctx = new HttpCtx(ns);
            try { ctx.RemoteIp = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString(); } catch { }

            List<byte> head = new List<byte>(2048);
            int matched = 0;
            while (head.Count < 64 * 1024)
            {
                int b = ns.ReadByte();
                if (b < 0) { return null; }
                head.Add((byte)b);
                byte[] marker = { 13, 10, 13, 10 };
                matched = b == marker[matched] ? matched + 1 : (b == marker[0] ? 1 : 0);
                if (matched == 4) { break; }
            }

            string text = Encoding.ASCII.GetString(head.ToArray());
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0) { return null; }
            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) { return null; }
            ctx.Method = requestLine[0].ToUpperInvariant();
            string target = requestLine[1];
            int q = target.IndexOf('?');
            if (q >= 0)
            {
                ctx.Path = UrlDecode(target.Substring(0, q));
                ctx.RawQuery = target.Substring(q + 1);
                foreach (string pair in ctx.RawQuery.Split('&'))
                {
                    if (pair.Length == 0) { continue; }
                    int eq = pair.IndexOf('=');
                    if (eq < 0) { ctx.args[UrlDecode(pair)] = ""; }
                    else { ctx.args[UrlDecode(pair.Substring(0, eq))] = UrlDecode(pair.Substring(eq + 1)); }
                }
            }
            else
            {
                ctx.Path = UrlDecode(target);
            }

            int length = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) { break; }
                int colon = line.IndexOf(':');
                if (colon <= 0) { continue; }
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out length);
                }
            }

            if (length > 0)
            {
                ctx.body = new byte[length];
                int done = 0;
                while (done < length)
                {
                    int n = ns.Read(ctx.body, done, length - done);
                    if (n <= 0) { break; }
                    done += n;
                }
            }
            return ctx;
        }

        public string Query(string name)
        {
            string v;
            return args.TryGetValue(name, out v) ? v : null;
        }

        public string Body { get { return Encoding.UTF8.GetString(body); } }

        private void WriteHead(int status, string contentType, long length)
        {
            string reason = status == 200 ? "OK" : status == 404 ? "Not Found" : status == 400 ? "Bad Request" : "Error";
            StringBuilder sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("Cache-Control: no-cache\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            ns.Write(head, 0, head.Length);
        }

        public void WriteBody(int status, string contentType, byte[] data)
        {
            if (responded) { return; }
            responded = true;
            WriteHead(status, contentType, data.LongLength);
            if (data.Length > 0) { ns.Write(data, 0, data.Length); }
            ns.Flush();
        }

        public void Json(object value, int status)
        {
            WriteBody(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Wolf.Json.Serialize(value)));
        }

        public void Ok(object value) { Json(value, 200); }
        public void Text(string text, int status) { WriteBody(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text ?? "")); }
        public void Html(string html) { WriteBody(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html ?? "")); }
        public void NotFound() { Text("not found", 404); }
        public void Error(string message) { Text(message ?? "error", 500); }

        // Chunked response for streams whose length is unknown (radio proxy).
        // UnityWebRequest / any HTTP client handles chunked, and audio starts playing
        // as soon as the first chunk arrives instead of after the whole file.
        public bool BeginChunked(int status, string contentType)
        {
            if (responded) { return false; }
            responded = true;
            StringBuilder sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(" OK\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Cache-Control: no-cache\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Transfer-Encoding: chunked\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            ns.Write(head, 0, head.Length);
            ns.Flush();
            return true;
        }

        // Ответ без длины и без chunked: тело идёт до закрытия соединения.
        // Нужен для сырого PCM-потока радио, где клиент читает сокет напрямую
        // и не разбирает framing.
        public bool BeginStream(int status, string contentType)
        {
            if (responded) { return false; }
            responded = true;
            StringBuilder sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(" OK\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Cache-Control: no-cache\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            ns.Write(head, 0, head.Length);
            ns.Flush();
            return true;
        }

        public bool WriteRaw(byte[] data, int count)
        {
            if (count <= 0) { return true; }
            try
            {
                ns.Write(data, 0, count);
                ns.Flush();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool WriteChunk(byte[] data, int count)
        {
            if (count <= 0) { return true; }
            try
            {
                byte[] size = Encoding.ASCII.GetBytes(count.ToString("X", CultureInfo.InvariantCulture) + "\r\n");
                ns.Write(size, 0, size.Length);
                ns.Write(data, 0, count);
                ns.Write(new byte[] { 13, 10 }, 0, 2);
                ns.Flush();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void EndChunked()
        {
            try
            {
                byte[] tail = Encoding.ASCII.GetBytes("0\r\n\r\n");
                ns.Write(tail, 0, tail.Length);
                ns.Flush();
            }
            catch { }
        }

        public void SendFile(string full)
        {
            if (responded) { return; }
            responded = true;
            long size = new FileInfo(full).Length;
            WriteHead(200, MiniHttp.GuessMime(full), size);
            using (FileStream fs = File.OpenRead(full))
            {
                byte[] buffer = new byte[128 * 1024];
                int n;
                while ((n = fs.Read(buffer, 0, buffer.Length)) > 0) { ns.Write(buffer, 0, n); }
            }
            ns.Flush();
        }

        private static string UrlDecode(string raw)
        {
            try { return Uri.UnescapeDataString(raw ?? ""); }
            catch { return raw ?? ""; }
        }
    }

    public static class Http
    {
        static Http()
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)4080 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls; }
            catch { }
            ServicePointManager.DefaultConnectionLimit = 32;
        }

        public static string Get(string url, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "WolfSuite/1.0";
            req.KeepAlive = false;
            // ModSync answers gzip-encoded JSON; without this the body would arrive as binary.
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
            using (StreamReader r = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
            {
                return r.ReadToEnd();
            }
        }

        // Downloads a binary resource (a chunk) with progress reporting.
        public static byte[] GetBytes(string url, int timeoutMs, Action<long> onProgress)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs * 4;
            req.UserAgent = "WolfSuite/1.0";
            // Чанки тянутся пачкой потоков: держим соединение, иначе на каждый мегабайт
            // уходит новый TCP-хендшейк.
            req.KeepAlive = true;
            using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
            using (Stream s = res.GetResponseStream())
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buffer = new byte[64 * 1024];
                long done = 0;
                int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, n);
                    done += n;
                    if (onProgress != null) { onProgress(done); }
                }
                return ms.ToArray();
            }
        }

        public static string Post(string url, string body, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "WolfSuite/1.0";
            req.ContentType = "application/json; charset=utf-8";
            req.KeepAlive = false;
            byte[] data = Encoding.UTF8.GetBytes(body ?? "");
            req.ContentLength = data.Length;
            using (Stream s = req.GetRequestStream()) { s.Write(data, 0, data.Length); }
            using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
            using (StreamReader r = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
            {
                return r.ReadToEnd();
            }
        }

        // application/x-www-form-urlencoded POST, used by the Telegram and VK APIs.
        public static string PostForm(string url, IDictionary<string, string> fields, int timeoutMs)
        {
            StringBuilder body = new StringBuilder();
            if (fields != null)
            {
                foreach (KeyValuePair<string, string> kv in fields)
                {
                    if (body.Length > 0) { body.Append('&'); }
                    body.Append(Uri.EscapeDataString(kv.Key ?? ""));
                    body.Append('=');
                    body.Append(Uri.EscapeDataString(kv.Value ?? ""));
                }
            }
            byte[] data = Encoding.UTF8.GetBytes(body.ToString());
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "WolfSuite/1.0";
            req.ContentType = "application/x-www-form-urlencoded; charset=utf-8";
            req.KeepAlive = false;
            req.ContentLength = data.Length;
            using (Stream s = req.GetRequestStream()) { s.Write(data, 0, data.Length); }
            using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
            using (StreamReader r = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
            {
                return r.ReadToEnd();
            }
        }

        // Retries transient network/5xx failures; a 404 is final (the file is really gone).
        public static string GetRetry(string url, int timeoutMs, int attempts)
        {
            attempts = Math.Max(1, attempts);
            Exception last = null;
            for (int i = 1; i <= attempts; i++)
            {
                try { return Get(url, timeoutMs); }
                catch (Exception ex)
                {
                    last = ex;
                    if (!Retryable(ex) || i == attempts) { break; }
                    Thread.Sleep(250 * i);
                }
            }
            throw last ?? new Exception("request failed");
        }

        public static bool TryGetRetry(string url, int timeoutMs, int attempts, out string text)
        {
            try { text = GetRetry(url, timeoutMs, attempts); return true; }
            catch { text = null; return false; }
        }

        public static bool TryGetBytesRetry(string url, int timeoutMs, int attempts, out byte[] data)
        {
            attempts = Math.Max(1, attempts);
            data = null;
            for (int i = 1; i <= attempts; i++)
            {
                try { data = GetBytes(url, timeoutMs, null); return true; }
                catch (Exception ex)
                {
                    data = null;
                    if (!Retryable(ex) || i == attempts) { break; }
                    Thread.Sleep(250 * i);
                }
            }
            return false;
        }

        private static bool Retryable(Exception ex)
        {
            WebException web = ex as WebException;
            if (web != null)
            {
                HttpWebResponse res = web.Response as HttpWebResponse;
                if (res != null) { return (int)res.StatusCode >= 500; }
                return true;
            }
            return ex is IOException;
        }

        public static bool TryGet(string url, int timeoutMs, out string text)
        {
            try { text = Get(url, timeoutMs); return true; }
            catch { text = null; return false; }
        }

        public static bool TryGetBytes(string url, int timeoutMs, out byte[] data)
        {
            try { data = GetBytes(url, timeoutMs, null); return true; }
            catch { data = null; return false; }
        }

        // Local IPv4 addresses advertised to peers so they can pull chunks from us.
        // ВАЖНО: раньше здесь был Dns.GetHostEntry(Dns.GetHostName()). Сразу после
        // перезагрузки служба DNS ещё не поднялась, и такой вызов висит десятки секунд
        // (а UI в этот момент стоит), из-за чего WolfServer казался зависшим на старте.
        // Перечисление через NetworkInterface работает без DNS и мгновенно.
        public static List<string> LocalIps()
        {
            List<string> result = new List<string>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) { continue; }
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) { continue; }
                    IPInterfaceProperties props;
                    try { props = nic.GetIPProperties(); }
                    catch { continue; }
                    foreach (UnicastIPAddressInformation info in props.UnicastAddresses)
                    {
                        IPAddress a = info.Address;
                        if (a == null || a.AddressFamily != AddressFamily.InterNetwork) { continue; }
                        if (IPAddress.IsLoopback(a)) { continue; }
                        string s = a.ToString();
                        if (s.StartsWith("169.254.")) { continue; }
                        if (!result.Contains(s)) { result.Add(s); }
                    }
                }
            }
            catch { }
            return result;
        }

        // Внешний IP. Вызов сетевой, поэтому: кэш (в памяти + на диске) и короткий таймаут.
        // UI никогда не должен ждать этот метод — см. ServerApp.LocalBaseUrl().
        private static readonly object publicIpGate = new object();
        private static string publicIpCache;
        private static DateTime publicIpCacheUtc = DateTime.MinValue;
        private static readonly TimeSpan PublicIpTtl = TimeSpan.FromMinutes(30);

        // Значение из кэша без обращения к сети: "" — ещё не знаем.
        public static string PublicIpCached()
        {
            lock (publicIpGate)
            {
                if (publicIpCache != null && DateTime.UtcNow - publicIpCacheUtc < PublicIpTtl) { return publicIpCache; }
            }
            return null;
        }

        public static string PublicIp()
        {
            string cached = PublicIpCached();
            if (!string.IsNullOrEmpty(cached)) { return cached; }

            foreach (string svc in new[] { "https://api.ipify.org", "https://ipv4.icanhazip.com", "http://checkip.amazonaws.com" })
            {
                string text;
                if (TryGet(svc, 2500, out text))
                {
                    string ip = (text ?? "").Trim();
                    IPAddress parsed;
                    if (IPAddress.TryParse(ip, out parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
                    {
                        lock (publicIpGate) { publicIpCache = ip; publicIpCacheUtc = DateTime.UtcNow; }
                        return ip;
                    }
                }
            }
            return null;
        }

        public static bool TcpAlive(string host, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult r = c.BeginConnect(host, port, null, null);
                    if (!r.AsyncWaitHandle.WaitOne(timeoutMs)) { return false; }
                    c.EndConnect(r);
                    return c.Connected;
                }
            }
            catch { return false; }
        }
    }
}
