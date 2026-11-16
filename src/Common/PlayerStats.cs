using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Wolf
{
    // One player session. A player who reconnects gets a new session, but lifetime totals
    // (total players, total playtime, peak online) keep adding up in PlayerStats.
    public sealed class PlayerSession
    {
        public string key = "";
        public string name = "";
        public string steamId = "";
        public DateTime joinedUtc;
        public DateTime lastSeenUtc;
        public DateTime leftUtc;
        public bool active;

        public TimeSpan Duration(DateTime nowUtc)
        {
            DateTime end = active ? nowUtc : (leftUtc == DateTime.MinValue ? lastSeenUtc : leftUtc);
            if (end < joinedUtc) { return TimeSpan.Zero; }
            return end - joinedUtc;
        }

        public Dictionary<string, object> ToJson()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["key"] = key;
            d["name"] = name;
            d["steam"] = steamId;
            d["joined"] = Stamp(joinedUtc);
            d["seen"] = Stamp(lastSeenUtc);
            d["left"] = leftUtc == DateTime.MinValue ? "" : Stamp(leftUtc);
            d["active"] = active;
            return d;
        }

        public static PlayerSession FromJson(Dictionary<string, object> d)
        {
            PlayerSession s = new PlayerSession();
            s.key = Json.Str(d, "key", "");
            s.name = Json.Str(d, "name", "");
            s.steamId = Json.Str(d, "steam", "");
            s.joinedUtc = Parse(Json.Str(d, "joined", ""));
            s.lastSeenUtc = Parse(Json.Str(d, "seen", ""));
            s.leftUtc = Parse(Json.Str(d, "left", ""));
            s.active = Json.Bool(d, "active", false);
            return s;
        }

        private static string Stamp(DateTime utc)
        {
            return utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static DateTime Parse(string text)
        {
            DateTime value;
            if (DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out value))
            {
                return value.ToUniversalTime();
            }
            return DateTime.MinValue;
        }
    }

    // Online tracking for the server tool: sessions, peak online, lifetime playtime.
    // Persisted as JSON; the file is small because only the last KeepSessions sessions stay.
    public sealed class PlayerStats
    {
        public const int KeepSessions = 200;

        public List<PlayerSession> Sessions = new List<PlayerSession>(); // newest first
        public int PeakOnline;
        public int TotalPlayers;
        public long TotalSeconds;

        public event Action<PlayerSession> OnJoin;
        public event Action<PlayerSession> OnLeave;

        public List<PlayerSession> Active
        {
            get { return Sessions.Where(s => s.active).ToList(); }
        }

        public int OnlineCount { get { return Sessions.Count(s => s.active); } }

        public static PlayerStats Load(string file)
        {
            PlayerStats s = new PlayerStats();
            Dictionary<string, object> d = Json.ReadFile(file);
            s.PeakOnline = Json.Int(d, "peak", 0);
            s.TotalPlayers = Json.Int(d, "players", 0);
            s.TotalSeconds = Json.Long(d, "seconds", 0);
            foreach (object o in Json.Arr(d, "sessions"))
            {
                Dictionary<string, object> sd = o as Dictionary<string, object>;
                if (sd != null) { s.Sessions.Add(PlayerSession.FromJson(sd)); }
            }
            // After a tool restart nobody is "currently online": close the sessions at their
            // last seen time so the next poll starts from a clean slate.
            bool closed = false;
            foreach (PlayerSession session in s.Sessions)
            {
                if (session.active)
                {
                    session.active = false;
                    if (session.leftUtc == DateTime.MinValue) { session.leftUtc = session.lastSeenUtc; }
                    TimeSpan duration = session.leftUtc - session.joinedUtc;
                    if (duration.TotalSeconds > 0) { s.TotalSeconds += (long)duration.TotalSeconds; }
                    closed = true;
                }
            }
            // Persist right away, otherwise every restart would count these sessions again.
            if (closed) { s.Save(file); }
            return s;
        }

        public void Save(string file)
        {
            List<object> list = new List<object>();
            foreach (PlayerSession s in Sessions) { list.Add(s.ToJson()); }
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["peak"] = PeakOnline;
            d["players"] = TotalPlayers;
            d["seconds"] = TotalSeconds;
            d["sessions"] = list;
            try { Json.WriteFile(file, d, true); } catch { }
        }

        // Called after every successful "listplayers". Returns true when anything changed,
        // so the caller can save and refresh the UI only when needed.
        public bool Observe(IList<GamePlayer> players, DateTime nowUtc)
        {
            bool changed = false;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (GamePlayer p in players)
            {
                string key = KeyOf(p);
                if (string.IsNullOrEmpty(key)) { continue; }
                seen.Add(key);

                PlayerSession session = Sessions.Find(x => x.active &&
                    string.Equals(x.key, key, StringComparison.OrdinalIgnoreCase));
                if (session == null)
                {
                    // A returning player is not a new player: check the stored history too.
                    bool known = Sessions.Any(x => string.Equals(x.key, key, StringComparison.OrdinalIgnoreCase));
                    session = new PlayerSession();
                    session.key = key;
                    session.name = p.Name;
                    session.steamId = p.SteamId;
                    session.joinedUtc = nowUtc;
                    session.lastSeenUtc = nowUtc;
                    session.active = true;
                    Sessions.Insert(0, session);
                    if (!known) { TotalPlayers++; }
                    changed = true;
                    if (OnJoin != null) { OnJoin(session); }
                }
                else
                {
                    if (!string.Equals(session.name, p.Name, StringComparison.Ordinal) ||
                        !string.Equals(session.steamId, p.SteamId, StringComparison.Ordinal))
                    {
                        session.name = p.Name;
                        session.steamId = p.SteamId;
                        changed = true;
                    }
                    session.lastSeenUtc = nowUtc;
                }
            }

            foreach (PlayerSession session in Sessions)
            {
                if (!session.active || seen.Contains(session.key)) { continue; }
                session.active = false;
                session.leftUtc = nowUtc;
                if (session.lastSeenUtc < nowUtc) { session.lastSeenUtc = nowUtc; }
                TimeSpan duration = session.leftUtc - session.joinedUtc;
                if (duration.TotalSeconds > 0) { TotalSeconds += (long)duration.TotalSeconds; }
                changed = true;
                if (OnLeave != null) { OnLeave(session); }
            }

            int online = OnlineCount;
            if (online > PeakOnline) { PeakOnline = online; changed = true; }

            if (changed) { TrimSessions(); }
            return changed;
        }

        private void TrimSessions()
        {
            if (Sessions.Count <= KeepSessions) { return; }
            List<PlayerSession> finished = Sessions.Where(s => !s.active).ToList();
            while (Sessions.Count > KeepSessions && finished.Count > 0)
            {
                PlayerSession oldest = finished[finished.Count - 1];
                if (!Sessions.Remove(oldest)) { break; }
                finished.RemoveAt(finished.Count - 1);
            }
            // Still too many (everyone is online): drop the oldest inactive anyway.
            while (Sessions.Count > KeepSessions)
            {
                PlayerSession victim = Sessions.LastOrDefault(s => !s.active);
                if (victim == null) { break; }
                Sessions.Remove(victim);
            }
        }

        public void Reset()
        {
            Sessions.Clear();
            PeakOnline = 0;
            TotalPlayers = 0;
            TotalSeconds = 0;
        }

        public static string KeyOf(GamePlayer p)
        {
            if (p == null) { return ""; }
            if (!string.IsNullOrEmpty(p.SteamId)) { return p.SteamId; }
            if (!string.IsNullOrEmpty(p.EntityId)) { return "id:" + p.EntityId; }
            return p.Name ?? "";
        }

        // Finished sessions live in TotalSeconds; sessions still running are counted live,
        // so the number grows while players are online instead of only after they leave.
        public TimeSpan TotalPlaytime
        {
            get
            {
                DateTime now = DateTime.UtcNow;
                double seconds = TotalSeconds;
                foreach (PlayerSession session in Sessions)
                {
                    if (session.active) { seconds += Math.Max(0.0, session.Duration(now).TotalSeconds); }
                }
                return TimeSpan.FromSeconds(seconds);
            }
        }
    }
}
