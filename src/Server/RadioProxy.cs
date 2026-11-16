using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml;

namespace Wolf.Server
{
    // Прокси интернет-радио для мода ZZZ_OnlineRadio.
    //
    // Клиент 7DTD не может тянуть чужой MP3/AAC-поток: UnityWebRequest требует
    // разрешения http в Player Settings, а оно в игре выключено ("Non-secure
    // network connections disabled in Player Settings"). Поэтому клиент качает
    // поток обычным TcpClient, а WolfServer отдаёт СЫРОЙ PCM без контейнера —
    // его достаточно лить прямо в AudioClip.Create(PCMReaderCallback).
    //
    //   GET /radio/<индекс>.pcm   — s16le, 44100, 2 канала, тело до закрытия
    //   GET /radio/<индекс>.ogg   — прежний OGG/Vorbis (только для отладки браузером)
    //
    // На каждый запрос поднимается свой ffmpeg: у каждого клиента поток начинается
    // заново. Одновременных потоков немного (слушают рядом с приёмником), плюс
    // есть жёсткий лимит.
    public sealed class RadioProxy
    {
        public sealed class Station
        {
            public string Name;
            public string Url;
        }

        private readonly ServerApp app;
        private readonly object gate = new object();

        private List<Station> stations = new List<Station>();
        private DateTime stationsStamp = DateTime.MinValue;
        private string stationsFile = "";

        private int active;
        private MiniHttp mappedOn;

        public RadioProxy(ServerApp app)
        {
            this.app = app;
        }

        // Маршруты добавляются один раз на конкретный экземпляр MiniHttp: StartServing
        // вызывается и при повторной публикации, а после Stop/Bind маршруты теряются.
        public void Map(MiniHttp http)
        {
            if (http == null) { return; }
            lock (gate)
            {
                if (mappedOn == http) { return; }
                mappedOn = http;
            }
            http.Map("/radio", Handle);
        }

        private void Handle(HttpCtx ctx)
        {
            if (!app.Settings.RadioEnabled)
            {
                ctx.Text("radio disabled", 404);
                return;
            }

            int index;
            bool pcm;
            if (!ParseIndex(ctx.Path, out index, out pcm))
            {
                ctx.NotFound();
                return;
            }

            List<Station> list = LoadStations();
            if (index >= list.Count)
            {
                ctx.NotFound();
                return;
            }

            string ffmpeg = FindFfmpeg();
            if (ffmpeg == null)
            {
                app.Log.Add("[radio] ffmpeg не найден, поток " + index + " недоступен");
                ctx.Text("ffmpeg not found", 500);
                return;
            }

            int limit = app.Settings.RadioMaxClients;
            if (limit < 1) { limit = 1; }
            if (Interlocked.Increment(ref active) > limit)
            {
                Interlocked.Decrement(ref active);
                ctx.Text("too many radio streams", 503);
                return;
            }

            try
            {
                Stream(ctx, ffmpeg, list[index], pcm);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        private void Stream(HttpCtx ctx, string ffmpeg, Station station, bool pcm)
        {
            Process p = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ffmpeg);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.Arguments = BuildArgs(station.Url, pcm);

                p = new Process();
                p.StartInfo = psi;
                p.Start();

                // stderr вычитываем, иначе ffmpeg залипает на заполненном канале.
                Thread errReader = new Thread(delegate()
                {
                    try
                    {
                        while (p.StandardError.ReadLine() != null) { }
                    }
                    catch { }
                });
                errReader.IsBackground = true;
                errReader.Start();

                // PCM идёт без длины и без chunked: клиент читает сокет напрямую.
                if (pcm)
                {
                    if (!ctx.BeginStream(200, "audio/L16;rate=44100;channels=2"))
                    {
                        Kill(p);
                        return;
                    }

                    byte[] raw = new byte[16 * 1024];
                    int rn;
                    while ((rn = p.StandardOutput.BaseStream.Read(raw, 0, raw.Length)) > 0)
                    {
                        if (!ctx.WriteRaw(raw, rn)) { break; }
                    }
                    return;
                }

                if (!ctx.BeginChunked(200, "audio/ogg"))
                {
                    Kill(p);
                    return;
                }

                byte[] buffer = new byte[8 * 1024];
                int n;
                while ((n = p.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (!ctx.WriteChunk(buffer, n)) { break; }
                }

                ctx.EndChunked();
            }
            catch (Exception ex)
            {
                app.Log.Add("[radio] " + station.Name + ": " + ex.Message);
                try { ctx.Text("radio error", 500); } catch { }
            }
            finally
            {
                Kill(p);
            }
        }

        private static string BuildArgs(string url, bool pcm)
        {
            // -reconnect* относится к http-протоколу: без него станция, дёрнувшая
            // соединение, обрывала поток до перезапроса клиента.
            StringBuilder sb = new StringBuilder();
            sb.Append("-hide_banner -loglevel error ");
            sb.Append("-user_agent \"Mozilla/5.0\" ");
            sb.Append("-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5 ");
            sb.Append("-i \"").Append(url.Replace("\"", "")).Append("\" ");
            sb.Append("-vn -ac 2 -ar 44100 ");
            if (pcm)
            {
                // Сырой поток без контейнера: клиент делает из него PCM-reader
                // для AudioClip.Create. Порядок байт little-endian (s16le).
                sb.Append("-f s16le -acodec pcm_s16le ");
                sb.Append("-flush_packets 1 ");
                sb.Append("pipe:1");
                return sb.ToString();
            }
            sb.Append("-c:a libvorbis -b:a 96k ");
            sb.Append("-flush_packets 1 ");
            sb.Append("-f ogg pipe:1");
            return sb.ToString();
        }

        private static void Kill(Process p)
        {
            if (p == null) { return; }
            try
            {
                if (!p.HasExited) { p.Kill(); }
            }
            catch { }
            try { p.Dispose(); } catch { }
        }

        // /radio/<index>.pcm или /radio/<index>.ogg — маршрут уже отсечён префиксом /radio.
        private static bool ParseIndex(string path, out int index, out bool pcm)
        {
            index = -1;
            pcm = false;

            string tail = path ?? "";
            int slash = tail.LastIndexOf('/');
            if (slash >= 0) { tail = tail.Substring(slash + 1); }

            if (tail.EndsWith(".pcm", StringComparison.OrdinalIgnoreCase))
            {
                pcm = true;
                tail = tail.Substring(0, tail.Length - 4);
            }
            else if (tail.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                tail = tail.Substring(0, tail.Length - 4);
            }

            int value;
            if (!int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) { return false; }
            if (value < 0 || value > 63) { return false; }
            index = value;
            return true;
        }

        // Список станций читается из файла мода и кэшируется по времени изменения.
        public List<Station> LoadStations()
        {
            string file = StationsFile();
            DateTime stamp = DateTime.MinValue;
            try
            {
                if (File.Exists(file)) { stamp = File.GetLastWriteTimeUtc(file); }
            }
            catch { }

            lock (gate)
            {
                if (stamp == stationsStamp && stationsFile == file) { return stations; }

                List<Station> parsed = new List<Station>();
                try
                {
                    if (stamp != DateTime.MinValue)
                    {
                        XmlDocument doc = new XmlDocument();
                        doc.Load(file);
                        XmlNodeList nodes = doc.SelectNodes("//station");
                        if (nodes != null)
                        {
                            foreach (XmlNode node in nodes)
                            {
                                if (node.Attributes == null) { continue; }
                                XmlAttribute name = node.Attributes["name"];
                                XmlAttribute url = node.Attributes["url"];
                                if (name == null || url == null) { continue; }
                                if (string.IsNullOrEmpty(name.Value) || string.IsNullOrEmpty(url.Value)) { continue; }
                                Station st = new Station();
                                st.Name = name.Value;
                                st.Url = url.Value;
                                parsed.Add(st);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    app.Log.Add("[radio] stations.xml: " + ex.Message);
                }

                stations = parsed;
                stationsFile = file;
                stationsStamp = stamp;
                return stations;
            }
        }

        private string StationsFile()
        {
            // 1) установленный мод в Mods (обычный случай);
            // 2) папка исходников рядом с сервером — чтобы прокси работал до установки
            //    мода и во время отладки;
            // 3) exe WolfServer рядом с модом.
            List<string> candidates = new List<string>();
            try
            {
                string mods = app.ModsSourceDir();
                if (!string.IsNullOrEmpty(mods))
                {
                    candidates.Add(Path.Combine(Path.Combine(Path.Combine(mods, "ZZZ_OnlineRadio"), "Config"), "stations.xml"));
                }
            }
            catch { }

            string root = null;
            try { root = app.ResolveServerRoot(); } catch { }
            if (!string.IsNullOrEmpty(root))
            {
                candidates.Add(Path.Combine(Path.Combine(Path.Combine(root, "_mods_src"), "ZZZ_OnlineRadio\\Config"), "stations.xml"));
            }
            candidates.Add(Path.Combine(AppDirMods(), "ZZZ_OnlineRadio\\Config\\stations.xml"));

            foreach (string c in candidates)
            {
                try
                {
                    if (!string.IsNullOrEmpty(c) && File.Exists(c)) { return c; }
                }
                catch { }
            }
            return candidates.Count > 0 ? candidates[0] : "";
        }

        private static string AppDirMods()
        {
            try { return Path.Combine(GamePaths.AppDir, "Mods"); }
            catch { return ""; }
        }

        // ffmpeg ищется один раз и запоминается в настройках.
        public string FindFfmpeg()
        {
            string configured = app.Settings.RadioFfmpeg;
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) { return configured; }

            List<string> candidates = new List<string>();
            candidates.Add(Path.Combine(GamePaths.AppDir, "ffmpeg.exe"));
            candidates.Add(Path.Combine(Path.GetDirectoryName(app.SettingsFile) ?? "", "ffmpeg.exe"));
            candidates.Add(Path.Combine(GamePaths.AppDir, "tools\\ffmpeg.exe"));
            // Известное место в дереве сервера (папка справочного мода).
            string root = null;
            try { root = app.ResolveServerRoot(); } catch { }
            if (!string.IsNullOrEmpty(root))
            {
                candidates.Add(Path.Combine(root, "_radio_ref\\ZZZ_Alter_RadioPlayers\\_Convert\\ffmpeg.exe"));
            }
            candidates.Add(Path.Combine(GamePaths.AppDir, "..\\_radio_ref\\ZZZ_Alter_RadioPlayers\\_Convert\\ffmpeg.exe"));

            foreach (string c in candidates)
            {
                try
                {
                    if (!string.IsNullOrEmpty(c) && File.Exists(c))
                    {
                        app.Settings.RadioFfmpeg = Path.GetFullPath(c);
                        app.Save();
                        app.Log.Add("[radio] ffmpeg: " + app.Settings.RadioFfmpeg);
                        return app.Settings.RadioFfmpeg;
                    }
                }
                catch { }
            }
            return null;
        }

        public int StationCount
        {
            get { return LoadStations().Count; }
        }
    }
}
