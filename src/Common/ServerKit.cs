using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Wolf
{
    // Telnet/RCON client for the dedicated server console (TelnetEnabled + TelnetPort in
    // serverconfig.xml). This is the stable admin interface: listplayers, ban, kick,
    // teleport, say, gettime, getgamestats. Without a password the game binds the socket
    // to loopback only, which is exactly what we want for a local tool.
    public sealed class Telnet : IDisposable
    {
        private TcpClient tcp;
        private NetworkStream stream;
        private readonly object gate = new object();
        private string password = "";
        // Кэш последней проверки: сторож и UI спрашивают Connected часто, а опрашивать
        // сокет нельзя, пока другой поток ждёт ответ в Command() (там держится gate).
        private volatile bool aliveCache;

        // TcpClient.Connected сам по себе бесполезен: после закрытия соединения на той
        // стороне он остаётся true до первой неудачной операции, поэтому панель ещё долго
        // показывала «консоль подключена», хотя чат уже не работал. Кроме флага опрашиваем
        // сокет неблокирующим Poll: «готов читать и данных нет» = связь закрыта.
        public bool Connected
        {
            get
            {
                if (!Monitor.TryEnter(gate, 50)) { return aliveCache; }
                try
                {
                    aliveCache = ProbeLocked();
                    return aliveCache;
                }
                finally { Monitor.Exit(gate); }
            }
        }

        private bool ProbeLocked()
        {
            if (tcp == null) { return false; }
            try
            {
                if (!tcp.Connected) { return false; }
                Socket socket = tcp.Client;
                if (socket == null) { return false; }
                return !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
            }
            catch { return false; }
        }

        // 7DTD answers with a trailing "OK\n" / error text; we read until the prompt settles.
        public bool Connect(string host, int port, string pass, int timeoutMs, out string banner)
        {
            banner = "";
            Disconnect();
            try
            {
                TcpClient client = new TcpClient();
                IAsyncResult r = client.BeginConnect(host, port, null, null);
                if (!r.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    try { client.Close(); } catch { }
                    return false;
                }
                client.EndConnect(r);
                client.NoDelay = true;
                client.ReceiveTimeout = timeoutMs;
                client.SendTimeout = timeoutMs;

                lock (gate)
                {
                    tcp = client;
                    stream = client.GetStream();
                    aliveCache = true;
                }

                banner = ReadUntilQuiet(600);
                password = pass ?? "";
                if (!string.IsNullOrEmpty(password))
                {
                    string reply = Command(password, 2000);
                    if (reply.ToLowerInvariant().Contains("error") || reply.ToLowerInvariant().Contains("invalid"))
                    {
                        banner += "\n" + reply;
                        // Неверный пароль: сокет открыт, но администратором мы не стали —
                        // держать такое соединение нельзя, иначе «подключено» будет врать.
                        Disconnect();
                        return false;
                    }
                    banner += "\n" + reply;
                }
                return true;
            }
            catch (Exception ex)
            {
                banner = ex.Message;
                Disconnect();
                return false;
            }
        }

        public void Disconnect()
        {
            lock (gate)
            {
                try { if (stream != null) { stream.Close(); } } catch { }
                try { if (tcp != null) { tcp.Close(); } } catch { }
                stream = null;
                tcp = null;
                aliveCache = false;
            }
        }

        // Sends one command and returns everything the server printed for it.
        public string Command(string cmd, int waitMs)
        {
            lock (gate)
            {
                if (stream == null) { aliveCache = false; throw new InvalidOperationException("telnet not connected"); }
                byte[] data = Encoding.UTF8.GetBytes(cmd + "\n");
                try { stream.Write(data, 0, data.Length); }
                catch
                {
                    // Обрыв обычно всплывает именно на записи: помечаем связь мёртвой, чтобы
                    // сторож в WolfServer переподключился, а не ждал следующего перезапуска.
                    aliveCache = false;
                    throw;
                }
                return ReadUntilQuiet(waitMs);
            }
        }

        // Fire-and-forget is enough for chat broadcasts.
        public void Send(string cmd)
        {
            try { Command(cmd, 400); } catch { }
        }

        private string ReadUntilQuiet(int waitMs)
        {
            StringBuilder sb = new StringBuilder();
            Stopwatch sw = Stopwatch.StartNew();
            byte[] buffer = new byte[8192];
            int idleMs = 180;
            stream.ReadTimeout = Math.Max(50, idleMs);
            while (sw.ElapsedMilliseconds < waitMs)
            {
                int n;
                try { n = stream.Read(buffer, 0, buffer.Length); }
                catch (IOException) { break; }        // timeout == no more data coming
                catch { break; }
                if (n <= 0) { break; }
                sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
                stream.ReadTimeout = Math.Max(50, idleMs);
            }
            return Clean(sb.ToString());
        }

        // The game echoes the prompt and a lot of \r; normalise for display and parsing.
        private static string Clean(string raw)
        {
            if (string.IsNullOrEmpty(raw)) { return ""; }
            string s = raw.Replace("\r\n", "\n").Replace("\r", "\n");
            List<string> lines = new List<string>();
            foreach (string line in s.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length == 0) { continue; }
                if (t == "OK") { continue; }
                lines.Add(t);
            }
            return string.Join("\n", lines.ToArray());
        }

        public void Dispose() { Disconnect(); }

        // ---------- typed helpers over the raw console ----------

        // V3.3 printed "0: Wolf (SteamID=76561198164237936) at (1234, 64, -5678)";
        // V3.4 switched to "0. id=115020, Wolf, pos=(-846.6, 38.1, -748.8), ..., pltfmid=Steam_765...".
        // Both are accepted, and the same helper also reads "listplayerids" ("1. id=115020, Wolf").
        public static List<GamePlayer> ParsePlayers(string text)
        {
            List<GamePlayer> result = new List<GamePlayer>();
            if (string.IsNullOrEmpty(text)) { return result; }
            foreach (string line in text.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length == 0) { continue; }
                // The console shares its socket with the log stream, so log lines have to be
                // dropped before the "<index>." test: they can carry a PltfmId of their own.
                if (LogLineRegex.IsMatch(t)) { continue; }
                int sep = t.IndexOf('.');
                int colon = t.IndexOf(':');
                int cut = sep;
                if (cut < 0 || (colon >= 0 && colon < sep)) { cut = colon; }
                if (cut <= 0) { continue; }
                string index = t.Substring(0, cut).Trim();
                bool numeric = true;
                foreach (char c in index) { if (!char.IsDigit(c)) { numeric = false; break; } }
                if (!numeric) { continue; }

                GamePlayer p = new GamePlayer();
                p.EntityId = Token(t, "id=");
                p.SteamId = Token(t, "pltfmid=");
                if (string.IsNullOrEmpty(p.SteamId)) { p.SteamId = Token(t, "SteamID="); }
                p.CrossId = Token(t, "crossid=");
                p.Ip = Token(t, "ip=");
                p.Ping = Token(t, "ping=");
                p.Level = Token(t, "level=");
                p.Health = Token(t, "health=");

                if (string.IsNullOrEmpty(p.EntityId) && string.IsNullOrEmpty(p.SteamId)) { continue; }

                string rest = t.Substring(cut + 1).Trim();
                p.Name = PlayerName(rest, p);
                string pos = Between(rest, "pos=(", ")");
                if (string.IsNullOrEmpty(pos)) { pos = Between(rest, "at (", ")"); }
                if (!string.IsNullOrEmpty(pos))
                {
                    string[] parts = pos.Split(',');
                    double x, y, z;
                    if (parts.Length >= 3 &&
                        double.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out x) &&
                        double.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out y) &&
                        double.TryParse(parts[2].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out z))
                    {
                        p.X = x;
                        p.Y = y;
                        p.Z = z;
                        p.HasPosition = true;
                    }
                }
                p.Raw = t;
                // An unnamed entry (or a format we only partly understood) is still a player:
                // show the platform id instead of dropping the row and ending up with an empty list.
                if (string.IsNullOrEmpty(p.Name))
                {
                    p.Name = !string.IsNullOrEmpty(p.SteamId) ? p.SteamId : "#" + p.EntityId;
                }
                if (!string.IsNullOrEmpty(p.Name)) { result.Add(p); }
            }
            return result;
        }

        // V3.4 "say" takes only the first word unless the message is quoted. Inner quotes are
        // replaced because the console has no escape syntax of its own.
        public static string SayCommand(string text)
        {
            string s = (text ?? "").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ").Trim();
            return "say \"" + s + "\"";
        }

        // "ban add <name / id / Steam_...> <duration> <unit> [reason]" — the V3.4 syntax.
        public static string BanAddCommand(string target, int amount, string unit, string reason)
        {
            string s = "ban add " + target + " " + amount.ToString(CultureInfo.InvariantCulture) + " " + unit;
            if (!string.IsNullOrEmpty(reason)) { s += " \"" + reason.Replace("\"", "'") + "\""; }
            return s;
        }

        // The name sits between "id=<n>, " and the first structural marker; it may contain spaces
        // and even commas, so cutting on the marker beats splitting on commas.
        private static string PlayerName(string rest, GamePlayer p)
        {
            string s = rest;
            if (!string.IsNullOrEmpty(p.EntityId))
            {
                string head = "id=" + p.EntityId;
                int i = s.IndexOf(head, StringComparison.OrdinalIgnoreCase);
                if (i >= 0) { s = s.Substring(i + head.Length).TrimStart(',', ' '); }
            }
            int marker = s.IndexOf("pos=", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0) { return s.Substring(0, marker).Trim().TrimEnd(',').Trim(); }
            int paren = s.IndexOf('(');
            if (paren == 0) { return ""; }
            if (paren > 0) { return s.Substring(0, paren).Trim(); }
            int comma = s.IndexOf(", ", StringComparison.Ordinal);
            if (comma > 0) { s = s.Substring(0, comma); }
            return s.Trim();
        }

        private static string Token(string source, string key)
        {
            // "id=" must not match inside "pltfmid=", so require a word boundary before the key.
            int i = 0;
            while (i < source.Length)
            {
                int at = source.IndexOf(key, i, StringComparison.OrdinalIgnoreCase);
                if (at < 0) { return ""; }
                bool boundary = at == 0 || !char.IsLetterOrDigit(source[at - 1]);
                if (boundary)
                {
                    string tail = source.Substring(at + key.Length);
                    int end = tail.IndexOfAny(new[] { ',', ')', ' ' });
                    return (end > 0 ? tail.Substring(0, end) : tail).Trim();
                }
                i = at + 1;
            }
            return "";
        }

        // "at (1234, 64, -5678)" -> "1234, 64, -5678"
        private static string Between(string source, string open, string close)
        {
            int i = source.IndexOf(open, StringComparison.OrdinalIgnoreCase);
            if (i < 0) { return ""; }
            int start = i + open.Length;
            int end = source.IndexOf(close, start, StringComparison.Ordinal);
            if (end < 0) { return ""; }
            return source.Substring(start, end - start).Trim();
        }

        // V3.3: "0: Name (SteamID=7656...) reason=cheating expires=..."
        // V3.4: "Banned until - UserID (name) - Reason" as a header, then
        //       "11/4/26 12:00:00 AM - Steam_76561198000000000 (Name) - cheating".
        public static List<BannedPlayer> ParseBans(string text)
        {
            List<BannedPlayer> result = new List<BannedPlayer>();
            if (string.IsNullOrEmpty(text)) { return result; }
            foreach (string line in text.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length == 0) { continue; }
                string lower = t.ToLowerInvariant();
                if (lower.Contains("ban list entries") || lower.Contains("no banned players") ||
                    lower.Contains("banned until") || lower.Contains("list of banned players"))
                {
                    continue;
                }
                // Telnet echoes the log stream on the same socket; skip those lines.
                if (LogLineRegex.IsMatch(t)) { continue; }

                BannedPlayer b = new BannedPlayer();
                if (t.IndexOf("SteamID=", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    int colon = t.IndexOf(':');
                    string rest = colon > 0 ? t.Substring(colon + 1).Trim() : t;
                    int paren = rest.IndexOf('(');
                    b.Name = paren > 0 ? rest.Substring(0, paren).Trim() : rest;
                    b.SteamId = Token(rest, "SteamID=");
                    b.Reason = Token(rest, "reason=");
                    b.Expires = Token(rest, "expires=");
                }
                else
                {
                    int open = t.IndexOf('(');
                    int close = t.LastIndexOf(')');
                    if (open <= 0 || close < open) { continue; }
                    b.Name = t.Substring(open + 1, close - open - 1).Trim();
                    string head = t.Substring(0, open).Trim();
                    int dash = head.LastIndexOf(" - ", StringComparison.Ordinal);
                    if (dash >= 0)
                    {
                        b.Expires = head.Substring(0, dash).Trim();
                        b.SteamId = head.Substring(dash + 3).Trim();
                    }
                    else
                    {
                        b.SteamId = head.Trim();
                    }
                    string tail = t.Substring(close + 1).Trim();
                    if (tail.StartsWith("-", StringComparison.Ordinal)) { tail = tail.Substring(1).Trim(); }
                    b.Reason = tail;
                }
                b.Raw = t;
                if (!string.IsNullOrEmpty(b.Name) || !string.IsNullOrEmpty(b.SteamId)) { result.Add(b); }
            }
            return result;
        }

        // "2026-11-04T00:01:34 7556.639 INF ..." or "22:02 1684.450 INF ..."
        private static readonly Regex LogLineRegex = new Regex(
            @"^\d{2,4}[-/T:]\S*\s+\S*\s+(INF|WRN|ERR|EXC)\b", RegexOptions.Compiled);
    }

    public sealed class GamePlayer
    {
        public string Name = "";
        public string SteamId = "";
        public string EntityId = "";
        public string CrossId = "";
        public string Ip = "";
        public string Ping = "";
        public string Level = "";
        public string Health = "";
        public double X, Y, Z;
        public bool HasPosition;
        public string Raw = "";

        // Console commands (kick/ban/teleport/pm) accept the entity id, and names with spaces
        // are ambiguous there, so the id is preferred whenever it is known.
        public string Handle
        {
            get { return string.IsNullOrEmpty(EntityId) ? Name : EntityId; }
        }

        public override string ToString()
        {
            return string.IsNullOrEmpty(SteamId) ? Name : Name + "  [" + SteamId + "]";
        }
    }

    public sealed class BannedPlayer
    {
        public string Name = "";
        public string SteamId = "";
        public string Reason = "";
        public string Expires = "";
        public string Raw = "";

        public override string ToString() { return Name + (string.IsNullOrEmpty(Reason) ? "" : "  (" + Reason + ")"); }
    }

    // Dedicated server process controller: start/stop, crash restart, log tailing.
    public sealed class ServerController : IDisposable
    {
        private readonly Proc proc = new Proc();
        private readonly object gate = new object();
        private string currentLog;
        private long logPosition;
        private Thread tailer;
        private volatile bool tailRunning;
        private volatile bool wantRunning;
        // Идёт наша остановка: события выхода обёртки/сервера в это время не новость.
        private volatile bool stopping;
        private DateTime lastStartUtc = DateTime.MinValue;
        private int restarts;
        private int sessions;
        private int sweepTicks;                        // счётчик итераций хвоста до проверки нового лога

        public string Root { get; set; }
        public bool AutoRestart = true;
        public int MinUptimeBeforeRestart = 60;       // seconds; a crash loop must not spin

        public event Action<string> OnLogLine;        // every line, already tagged
        public event Action<bool> OnStateChanged;     // running / stopped
        public event Action<string> OnEvent;
        public event Action<string> OnLogChanged;     // tailer moved to another log file

        // Скрипт запуска (start_server1.bat). Именно он держит цикл «сервер упал/вышел → поднять
        // через 15 секунд», выставляет приоритет High и affinity, чистит старые логи и вообще
        // повторяет то, что админ делает руками. Раньше WolfServer запускал 7DaysToDieServer.exe
        // напрямую и получал сервер «непонятно чего»: без affinity, без цикла и с другими
        // аргументами, чем в батнике. Теперь запуск по умолчанию идёт через этот скрипт.
        public string LaunchScript { get; set; }

        // Полный путь к скрипту запуска: явная настройка, иначе start_server1.bat рядом с сервером.
        public string ResolveLaunchScript()
        {
            try
            {
                if (!string.IsNullOrEmpty(LaunchScript) && File.Exists(LaunchScript)) { return LaunchScript; }
                if (!string.IsNullOrEmpty(Root))
                {
                    string bat = Path.Combine(Root, "start_server1.bat");
                    if (File.Exists(bat)) { return bat; }
                }
            }
            catch { }
            return null;
        }

        public bool Running { get { return proc.Running || ExternalPid != 0; } }
        public int Pid { get { return proc.Running ? proc.Pid : ExternalPid; } }
        public int Restarts { get { return restarts; } }
        public int Sessions { get { return sessions; } }
        public string CurrentLog { get { return currentLog; } }

        // PID of a 7DaysToDieServer.exe started outside this tool (the start_server1.bat loop
        // keeps the server alive between restarts, so the tool usually attaches to a foreign process).
        public int ExternalPid { get; private set; }
        public bool OwnsProcess { get { return proc.Running; } }

        // Finds the server process of this installation. Returns true when the running state changed.
        public bool DetectExternal()
        {
            int found = 0;
            try
            {
                foreach (Process p in Process.GetProcessesByName("7DaysToDieServer"))
                {
                    try
                    {
                        string dir = null;
                        try { dir = Path.GetDirectoryName(p.MainModule.FileName); } catch { }
                        if (!string.IsNullOrEmpty(Root) && !string.IsNullOrEmpty(dir) &&
                            !string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(Root).TrimEnd('\\'),
                                           StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        found = p.Id;
                        break;
                    }
                    finally { p.Dispose(); }
                }
            }
            catch { }

            if (found == ExternalPid) { return false; }
            ExternalPid = found;
            if (found != 0 && OnEvent != null) { OnEvent("сервер уже запущен извне, PID " + found); }
            if (OnStateChanged != null) { OnStateChanged(Running); }
            return true;
        }

        public ServerController()
        {
            proc.OnOutput += delegate(string line) { Emit(line); };
            proc.OnExit += delegate(int code) { HandleExit(code); };
        }

        public string ExePath
        {
            get { return string.IsNullOrEmpty(Root) ? null : Path.Combine(Root, "7DaysToDieServer.exe"); }
        }

        public bool Start(string configPath, bool withTerminal)
        {
            return Start(configPath, withTerminal, true);
        }

        // useScript=false — старый путь: прямой запуск 7DaysToDieServer.exe (без цикла и affinity).
        // Оставлен как аварийный вариант, штатный запуск идёт через start_server1.bat.
        public bool Start(string configPath, bool withTerminal, bool useScript)
        {
            string exe = ExePath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                if (OnEvent != null) { OnEvent("не найден 7DaysToDieServer.exe в " + Root); }
                return false;
            }
            if (ExternalPid != 0)
            {
                if (OnEvent != null) { OnEvent("сервер уже работает (PID " + ExternalPid + "), второй экземпляр не нужен"); }
                return false;
            }

            string script = useScript ? ResolveLaunchScript() : null;

            string newest = NewestLog();
            lock (gate)
            {
                currentLog = newest;
                logPosition = newest != null && File.Exists(newest) ? new FileInfo(newest).Length : 0;
            }

            lock (gate) { wantRunning = true; }
            sessions++;
            lastStartUtc = DateTime.UtcNow;

            bool ok;
            if (script != null)
            {
                // Батник запускает сервер сам: affinity 0xFFFF, приоритет High, свой цикл
                // перезапуска и очистка старых логов. Наш процесс — cmd.exe с /c, поэтому
                // остановка (taskkill /T) снимает и цикл, и сам сервер.
                string args = "/c \"" + script + "\"";
                ok = proc.Start("cmd.exe", args, Root, true, false);
                if (ok && OnEvent != null) { OnEvent("сервер запущен через " + Path.GetFileName(script)); }
            }
            else
            {
                // Only -configfile is passed: whether a console window appears is decided by
                // TerminalWindowEnabled in serverconfig.xml, which our config editor controls.
                StringBuilder args = new StringBuilder();
                if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
                {
                    args.Append("-configfile=\"").Append(Path.GetFullPath(configPath)).Append('"');
                }
                ok = proc.Start(exe, args.ToString(), Root, true, false);
            }

            if (!ok)
            {
                lock (gate) { wantRunning = false; }
                if (OnEvent != null) { OnEvent("не удалось запустить сервер"); }
                return false;
            }
            if (OnStateChanged != null) { OnStateChanged(true); }
            StartTailing();
            if (OnEvent != null)
            {
                if (script == null) { OnEvent("сервер запущен, PID " + proc.Pid); }
            }
            return true;
        }

        public void Stop()
        {
            lock (gate) { wantRunning = false; }
            tailRunning = false;
            proc.Stop();
            if (ExternalPid != 0)
            {
                // Started outside this tool: killing it here would fight the restart loop.
                if (OnEvent != null) { OnEvent("процесс запущен извне (PID " + ExternalPid + ") — остановите его через telnet shutdown"); }
            }
            else
            {
                if (OnEvent != null) { OnEvent("сервер остановлен"); }
            }
            if (OnStateChanged != null) { OnStateChanged(false); }
        }

        // Запущен ли процесс через скрипт (тогда живым держится ещё и цикл cmd.exe).
        public bool StartedViaScript { get { return proc.Running && proc.IsCmdWrapper; } }

        // Сколько ждать штатного выхода сервера после shutdown, прежде чем бить процесс.
        public int ShutdownTimeoutMs = 90000;

        // Остановка по кнопке «Стоп»: сервер должен закрыться сам.
        public void GracefulStop(Telnet telnet)
        {
            GracefulStop(telnet, false);
        }

        // Штатная остановка через консоль сервера.
        //
        // keepRestartLoop=true  — перезапуск: сервер гасим, а цикл start_server1.bat остаётся
        //                         жить и поднимает его снова через 15 секунд.
        // keepRestartLoop=false — полная остановка: сначала снимаем цикл перезапуска, иначе
        //                         он поднимет сервер сразу после shutdown, и только потом
        //                         гасим сам сервер.
        //
        // Порядок: saveworld → снять цикл → shutdown → дождаться выхода → kill (резерв).
        // Жёсткий taskkill /T по дереву (как было раньше) обрывал OnApplicationQuit:
        // не писались vehicles.dat/drones.dat/players.xml, моды не сохраняли состояние,
        // а выход шёл с кодом 1/-1.
        public void GracefulStop(Telnet telnet, bool keepRestartLoop)
        {
            GracefulStop(telnet, keepRestartLoop, ShutdownTimeoutMs);
        }

        public void GracefulStop(Telnet telnet, bool keepRestartLoop, int timeoutMs)
        {
            tailRunning = false;
            stopping = true;
            try
            {
                GracefulStopCore(telnet, keepRestartLoop, timeoutMs);
            }
            finally
            {
                stopping = false;
            }
        }

        private void GracefulStopCore(Telnet telnet, bool keepRestartLoop, int timeoutMs)
        {
            // Автоперезапуск средствами самого контроллера больше не нужен: при
            // keepRestartLoop сервер поднимет внешний цикл, иначе он остаётся лежать.
            lock (gate) { wantRunning = false; }

            // PID именно сервера: при запуске через скрипт proc.Pid — это cmd.exe-обёртка.
            DetectExternal();
            int serverPid = ExternalPid != 0 ? ExternalPid : (proc.Running && !proc.IsCmdWrapper ? proc.Pid : 0);

            bool console = telnet != null && telnet.Connected;

            // Сервер уже не работает: остаётся только прибрать цикл перезапуска.
            if (serverPid == 0)
            {
                if (!keepRestartLoop) { RemoveRestartLoop(); }
                if (OnStateChanged != null) { OnStateChanged(false); }
                return;
            }

            // 1. Сохранить мир. Штатный выход сохраняет и сам, но saveworld гарантирует,
            //    что прогресс не потеряется, даже если сервер придётся добивать.
            if (console)
            {
                try { telnet.Command("saveworld", 60000); } catch { }
            }

            // 2. Снять цикл перезапуска, не трогая сам сервер.
            if (!keepRestartLoop) { RemoveRestartLoop(); }

            // 3. Штатный shutdown: OnApplicationQuit, флаш регионов, сохранение модов.
            if (console)
            {
                try { telnet.Command("shutdown 10", 5000); } catch { }
            }

            // 4. Дождаться, пока сервер закроется сам.
            if (serverPid != 0 && WaitForExit(serverPid, timeoutMs))
            {
                if (OnEvent != null) { OnEvent(console ? "сервер остановлен штатно" : "сервер завершился"); }
                ExternalPid = 0;
                if (OnStateChanged != null) { OnStateChanged(false); }
                return;
            }

            // 5. Консоль не ответила или сервер не закрылся — снимаем принудительно.
            if (OnEvent != null)
            {
                OnEvent(console
                    ? "сервер не завершился за " + (timeoutMs / 1000) + " с — принудительное завершение"
                    : "консоль недоступна — принудительное завершение");
            }

            if (proc.Running) { proc.Stop(); }
            if (serverPid != 0) { Proc.KillPid(serverPid); }
            ExternalPid = 0;
            if (OnStateChanged != null) { OnStateChanged(false); }
        }

        // Убирает цикл автоматического перезапуска (start_server1.bat), оставляя сервер живым.
        // Свой процесс — это cmd.exe с /c "<скрипт>": снимаем только его, без /T, иначе
        // taskkill убьёт и дочерний 7DaysToDieServer.exe. Внешний процесс — ищем родителя
        // по WMI: им оказывается cmd.exe, который крутит цикл.
        private void RemoveRestartLoop()
        {
            if (proc.Running && proc.IsCmdWrapper)
            {
                proc.StopWrapperOnly();
                if (OnEvent != null) { OnEvent("цикл start_server1.bat снят"); }
                return;
            }

            if (ExternalPid == 0) { return; }
            // Родитель сервера после `start /wait` — это cmd.exe, который крутит цикл
            // start_server1.bat. Убиваем только его (без /T): в дереве висит живой сервер.
            int parent = ParentPid(ExternalPid);
            if (parent != 0 && IsLoopScriptHost(parent))
            {
                Proc.KillPid(parent, false);
                if (OnEvent != null) { OnEvent("внешний цикл cmd.exe (PID " + parent + ") остановлен"); }
            }
            else if (parent != 0)
            {
                if (OnEvent != null) { OnEvent("внешний цикл не найден (родитель PID " + parent + ") — сервер может подняться снова"); }
            }
        }

        // Родитель должен быть именно cmd.exe, запускающим наш батник: чужой cmd (например,
        // родитель самого WolfServer) убивать нельзя.
        private bool IsLoopScriptHost(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    if (!string.Equals(p.ProcessName, "cmd", StringComparison.OrdinalIgnoreCase)) { return false; }
                }
            }
            catch { return false; }

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + pid.ToString(CultureInfo.InvariantCulture)))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string cmdLine = mo["CommandLine"] as string;
                        if (string.IsNullOrEmpty(cmdLine)) { return false; }
                        string script = ResolveLaunchScript();
                        if (!string.IsNullOrEmpty(script))
                        {
                            if (cmdLine.IndexOf(Path.GetFileName(script), StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
                        }
                        // Батник мог быть переименован/запущен вручную: ориентируемся на .bat.
                        if (cmdLine.IndexOf(".bat", StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
                        return false;
                    }
                }
            }
            catch { }
            return false;
        }

        private static int ParentPid(int pid)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT ParentProcessId FROM Win32_Process WHERE ProcessId=" + pid.ToString(CultureInfo.InvariantCulture)))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        object v = mo["ParentProcessId"];
                        if (v != null) { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
                    }
                }
            }
            catch { }
            return 0;
        }

        private static bool WaitForExit(int pid, int timeoutMs)
        {
            const int step = 250;
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (!Proc.IsAlive(pid)) { return true; }
                Thread.Sleep(step);
                waited += step;
            }
            return !Proc.IsAlive(pid);
        }

        public void Dispose()
        {
            tailRunning = false;
            lock (gate) { wantRunning = false; }
            proc.Stop();
        }

        private void HandleExit(int code)
        {
            tailRunning = false;

            // Умерла только обёртка, а сервер жив: так бывает при остановке цикла
            // перезапуска и при ручном закрытии консоли батника. Это не остановка сервера —
            // автоперезапуск не нужен, сообщать «сервер завершился» тоже нельзя.
            // Во время нашей собственной остановки молчим: GracefulStop сам расскажет итог.
            if (!stopping)
            {
                DetectExternal();
                if (ExternalPid != 0)
                {
                    if (OnEvent != null) { OnEvent("обёртка процесса завершилась (код " + code + "), сервер работает, PID " + ExternalPid); }
                    StartTailing();
                    return;
                }
            }

            bool shouldRestart;
            lock (gate) { shouldRestart = wantRunning && AutoRestart; }

            // Во время нашей остановки выхода «обёртки» достаточно: итог сообщит GracefulStop,
            // а код здесь — код cmd.exe, а не самого сервера.
            if (stopping) { return; }

            if (OnEvent != null) { OnEvent("процесс сервера завершился, код " + code); }
            if (OnStateChanged != null) { OnStateChanged(false); }

            if (!shouldRestart) { return; }

            double uptime = (DateTime.UtcNow - lastStartUtc).TotalSeconds;
            if (uptime < MinUptimeBeforeRestart)
            {
                restarts++;
                if (restarts >= 5)
                {
                    if (OnEvent != null) { OnEvent("5 быстрых падений подряд — автоперезапуск отключён, проверьте лог"); }
                    lock (gate) { wantRunning = false; }
                    return;
                }
            }
            else
            {
                restarts = 0;
            }

            if (OnEvent != null) { OnEvent("автоперезапуск через 8 секунд"); }
            Thread.Sleep(8000);
            if (!wantRunning) { return; }
            Start(GamePaths.ServerConfig(Root), false, LaunchScript != null || ResolveLaunchScript() != null);
        }

        // ---------- log tailing ----------

        private string liveLogPath;
        private DateTime liveLogProbeUtc = DateTime.MinValue;

        // Лог из командной строки живого процесса сервера (start_server1.bat передаёт
        // -logfile "..."). Ответ кэшируется на 10 секунд: WMI-запрос не бесплатный, а хвост
        // спрашивает путь часто. Если сервер не запущен — null, и работает поиск по файлам.
        public string RunningLogPath()
        {
            if (string.IsNullOrEmpty(Root)) { return null; }
            if ((DateTime.UtcNow - liveLogProbeUtc).TotalSeconds < 10) { return liveLogPath; }
            liveLogProbeUtc = DateTime.UtcNow;
            liveLogPath = null;

            string exe = Path.Combine(Root, "7DaysToDieServer.exe");
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process WHERE Name='7DaysToDieServer.exe'"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        try
                        {
                            string path = mo["ExecutablePath"] as string;
                            string cmd = mo["CommandLine"] as string;
                            if (string.IsNullOrEmpty(cmd)) { continue; }
                            if (!string.IsNullOrEmpty(path) && !SamePath(path, exe)) { continue; }
                            string log = LogFileArgument(cmd);
                            if (string.IsNullOrEmpty(log) || !File.Exists(log)) { continue; }
                            liveLogPath = log;
                            return liveLogPath;
                        }
                        finally { mo.Dispose(); }
                    }
                }
            }
            catch { }
            return null;
        }

        private static string LogFileArgument(string cmdLine)
        {
            Match m = Regex.Match(cmdLine, "-logfile\\s+(?:\"(?<q>[^\"]+)\"|(?<u>\\S+))", RegexOptions.IgnoreCase);
            if (!m.Success) { return null; }
            string value = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["u"].Value;
            value = (value ?? "").Trim().Trim('"');
            return value.Length == 0 ? null : value;
        }

        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public string NewestLog()
        {
            if (string.IsNullOrEmpty(Root)) { return null; }
            // Точный лог работающего сервера важнее любых эвристик: start_server1.bat
            // передаёт путь в -logfile. Когда сессий несколько (цикл перезапуска после
            // падений, случайно запущенный второй цикл), «самый свежий файл» оказывается
            // логом сессии, которая уже умерла, и вкладка «Чат» показывала пустоту.
            string live = RunningLogPath();
            if (!string.IsNullOrEmpty(live)) { return live; }

            // Только основной лог самого сервера. Раньше в выбор попадали и служебные файлы
            // logs\service_log.txt, connection_log*, stats_log*, configstore_log*: чата и строк
            // сервера в них нет, зато обновляются они чаще основного лога — хвост уезжал в
            // служебный файл, и вкладки «Чат» и «Живой лог» оставались пустыми, пока основной
            // лог снова не станет свежее.
            List<string> candidates = new List<string>();
            foreach (string pattern in new[] { "output_log_dedi__*.txt", "output_log*.txt" })
            {
                candidates.AddRange(GamePaths.SafeFiles(Root, pattern, SearchOption.TopDirectoryOnly));
            }
            candidates.AddRange(GamePaths.SafeFiles(Path.Combine(Root, "logs"), "output_log*.txt", SearchOption.TopDirectoryOnly));

            string best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (string f in candidates)
            {
                try
                {
                    DateTime t = File.GetLastWriteTimeUtc(f);
                    if (t > bestTime) { bestTime = t; best = f; }
                }
                catch { }
            }

            if (best == null)
            {
                string flat = Path.Combine(Root, "output_log_dedi.txt");
                if (File.Exists(flat)) { best = flat; }
            }
            return best;
        }

        // Opens the newest log and starts from its current end, so attaching to a running
        // server does not replay hours of history.
        public void FollowNewestLog(bool fromEnd)
        {
            string newest = NewestLog();
            if (newest == null) { return; }
            lock (gate)
            {
                currentLog = newest;
                logPosition = fromEnd ? new FileInfo(newest).Length : 0;
            }
            StartTailing();
        }

        // Переключает хвост на лог живого сервера или на более свежий файл. Вызывается из
        // потока хвоста, поэтому пишет состояние под gate и только потом зовёт подписчиков.
        private void FollowNewestIfChanged()
        {
            string current;
            lock (gate) { current = currentLog; }

            // Живой процесс знает свой лог точно — это главнее «самого свежего файла».
            string live = RunningLogPath();
            if (!string.IsNullOrEmpty(live))
            {
                if (string.Equals(live, current, StringComparison.OrdinalIgnoreCase)) { return; }
                SwitchTo(live);
                return;
            }

            string newest = NewestLog();
            if (newest == null) { return; }
            if (string.Equals(newest, current, StringComparison.OrdinalIgnoreCase)) { return; }

            if (current != null && File.Exists(current))
            {
                try
                {
                    // Более старый файл не берём: NewestLog может вернуть другой путь к тому же
                    // логу (logs\ против корня), а прыжки назад по времени только путают ленту.
                    if (File.GetLastWriteTimeUtc(newest) <= File.GetLastWriteTimeUtc(current)) { return; }
                }
                catch { return; }
            }

            SwitchTo(newest);
        }

        private void SwitchTo(string file)
        {
            lock (gate) { currentLog = file; logPosition = 0; }
            Action<string> h = OnLogChanged;
            if (h != null) { try { h(file); } catch { } }
        }

        public void WatchLog(string file)
        {
            lock (gate)
            {
                currentLog = file;
                logPosition = 0;
            }
            StartTailing();
        }

        private void StartTailing()
        {
            if (tailRunning) { return; }
            tailRunning = true;
            tailer = new Thread(TailLoop) { IsBackground = true, Name = "LogTail" };
            tailer.Start();
        }

        private void TailLoop()
        {
            while (tailRunning)
            {
                // Раз в ~3 секунды проверяем, не начался ли новый файл лога: start_server1.bat
                // создаёт output_log_dedi__ДД-ММ-ГГГГ__ЧЧ-ММ-СС.txt на каждый запуск сервера,
                // поэтому после перезапуска хвост старого файла больше не растёт и вкладки
                // «Чат» и «Живой лог» молча замирали до перезапуска самого WolfServer.
                if (++sweepTicks >= 6)
                {
                    sweepTicks = 0;
                    FollowNewestIfChanged();
                }

                string file;
                long pos;
                lock (gate) { file = currentLog; pos = logPosition; }
                if (string.IsNullOrEmpty(file))
                {
                    string found = NewestLog();
                    lock (gate) { currentLog = found; }
                    Thread.Sleep(1000);
                    continue;
                }

                try
                {
                    if (!File.Exists(file)) { Thread.Sleep(1500); continue; }
                    using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        if (fs.Length < pos) { pos = 0; }          // log rotated or truncated
                        fs.Seek(pos, SeekOrigin.Begin);
                        using (StreamReader r = new StreamReader(fs, Encoding.UTF8))
                        {
                            string line;
                            while ((line = r.ReadLine()) != null)
                            {
                                pos += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                                Emit(line);
                            }
                        }
                    }
                    lock (gate) { logPosition = pos; }
                }
                catch (IOException) { }
                catch (Exception) { }
                Thread.Sleep(500);
            }
        }

        private void Emit(string line)
        {
            if (string.IsNullOrEmpty(line)) { return; }
            Action<string> h = OnLogLine;
            if (h != null) { h(line.TrimEnd()); }
        }

        // Classification used for colouring and for the "problems" counter.
        public static LogLevel LevelOf(string line)
        {
            if (string.IsNullOrEmpty(line)) { return LogLevel.Info; }
            // Shader/graphics spam is noise on every start; the reference script filters it too.
            if (line.IndexOf("Shader", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("GfxDevice", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("d3d11", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("UnityException", StringComparison.OrdinalIgnoreCase) >= 0 && line.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return LogLevel.Noise;
            }
            if (Starts(line, "EXC") || line.IndexOf(" Exception", StringComparison.OrdinalIgnoreCase) >= 0) { return LogLevel.Exception; }
            if (Starts(line, "ERR") || line.IndexOf(" Error", StringComparison.OrdinalIgnoreCase) >= 0) { return LogLevel.Error; }
            if (Starts(line, "WRN") || line.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0) { return LogLevel.Warning; }
            return LogLevel.Info;
        }

        private static bool Starts(string line, string tag)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '[' || line[i] == '-')) { i++; }
            return i + tag.Length <= line.Length && string.CompareOrdinal(line, i, tag, 0, tag.Length) == 0;
        }
    }

    public enum LogLevel
    {
        Noise = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
        Exception = 4,
    }
}
