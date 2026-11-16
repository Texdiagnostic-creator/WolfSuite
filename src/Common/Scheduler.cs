using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace Wolf
{
    public enum ScheduleKind
    {
        Restart = 0,
        Backup = 1,
        Broadcast = 2,
    }

    public sealed class ScheduleTask
    {
        public ScheduleKind Kind = ScheduleKind.Restart;
        public int Hour = 4;
        public int Minute = 0;
        public bool Enabled = true;
        public string Text = "";               // message for Broadcast
        public int WarnMinutes = 5;            // countdown warning before a restart
        public string LastRunDay = "";         // "yyyy-MM-dd" guard so it fires once a day
        // Интервальный режим: 0 — ежедневно в Hour:Minute, иначе «каждые EveryMinutes минут».
        public int EveryMinutes = 0;
        public string LastRunAt = "";          // UTC "yyyy-MM-dd HH:mm:ss" для интервальных задач

        public bool IsInterval { get { return EveryMinutes > 0; } }

        public string Describe()
        {
            string when = Hour.ToString("00", CultureInfo.InvariantCulture) + ":" + Minute.ToString("00", CultureInfo.InvariantCulture);
            if (IsInterval)
            {
                string period = EveryMinutes % 60 == 0
                    ? "каждые " + (EveryMinutes / 60).ToString(CultureInfo.InvariantCulture) + " ч"
                    : "каждые " + EveryMinutes.ToString(CultureInfo.InvariantCulture) + " мин";
                switch (Kind)
                {
                    case ScheduleKind.Restart: return period + ": перезапуск сервера";
                    case ScheduleKind.Backup: return period + ": бэкап сохранений";
                    default: return period + ": сообщение в чат: " + Text;
                }
            }
            switch (Kind)
            {
                case ScheduleKind.Restart: return when + "  перезапуск сервера";
                case ScheduleKind.Backup: return when + "  бэкап сохранений";
                default: return when + "  сообщение в чат: " + Text;
            }
        }

        public Dictionary<string, object> ToJson()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["kind"] = (int)Kind;
            d["hour"] = Hour;
            d["minute"] = Minute;
            d["enabled"] = Enabled;
            d["text"] = Text;
            d["warn"] = WarnMinutes;
            d["last"] = LastRunDay;
            d["every"] = EveryMinutes;
            d["lastAt"] = LastRunAt;
            return d;
        }

        public static ScheduleTask FromJson(Dictionary<string, object> d)
        {
            ScheduleTask t = new ScheduleTask();
            t.Kind = (ScheduleKind)Json.Int(d, "kind", 0);
            t.Hour = Json.Int(d, "hour", 4);
            t.Minute = Json.Int(d, "minute", 0);
            t.Enabled = Json.Bool(d, "enabled", true);
            t.Text = Json.Str(d, "text", "");
            t.WarnMinutes = Json.Int(d, "warn", 5);
            t.LastRunDay = Json.Str(d, "last", "");
            t.EveryMinutes = Json.Int(d, "every", 0);
            t.LastRunAt = Json.Str(d, "lastAt", "");
            return t;
        }
    }

    // Minute-resolution scheduler. Deliberately simple: a task fires once per day at
    // its hour:minute; the day guard is persisted so restarting the tool cannot double-fire.
    public sealed class Scheduler : IDisposable
    {
        private readonly List<ScheduleTask> tasks = new List<ScheduleTask>();
        private readonly object gate = new object();
        private readonly string storePath;
        private Thread worker;
        private volatile bool running;
        private readonly HashSet<string> warnedToday = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public event Action<ScheduleTask> OnFire;
        public event Action<ScheduleTask, int> OnWarning;    // task, minutes left
        public event Action<string> OnEvent;

        public Scheduler(string storePath)
        {
            this.storePath = storePath;
            Load();
        }

        public List<ScheduleTask> Tasks
        {
            get { lock (gate) { return new List<ScheduleTask>(tasks); } }
        }

        public void Add(ScheduleTask task)
        {
            lock (gate) { tasks.Add(task); }
            Save();
        }

        public void Remove(ScheduleTask task)
        {
            lock (gate) { tasks.Remove(task); }
            Save();
        }

        public void Save()
        {
            List<object> list = new List<object>();
            lock (gate)
            {
                foreach (ScheduleTask t in tasks) { list.Add(t.ToJson()); }
            }
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["tasks"] = list;
            try { Json.WriteFile(storePath, root, true); } catch { }
        }

        public void Load()
        {
            Dictionary<string, object> root = Json.ReadFile(storePath);
            List<ScheduleTask> loaded = new List<ScheduleTask>();
            foreach (object o in Json.Arr(root, "tasks"))
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d != null) { loaded.Add(ScheduleTask.FromJson(d)); }
            }
            lock (gate)
            {
                tasks.Clear();
                tasks.AddRange(loaded);
            }
        }

        public void Start()
        {
            if (running) { return; }
            running = true;
            worker = new Thread(Loop) { IsBackground = true, Name = "Scheduler" };
            worker.Start();
        }

        public void Stop()
        {
            running = false;
        }

        public void Dispose() { Stop(); }

        private void Loop()
        {
            while (running)
            {
                try { Tick(); }
                catch (Exception ex)
                {
                    if (OnEvent != null) { OnEvent("планировщик: " + ex.Message); }
                }
                for (int i = 0; i < 20 && running; i++) { Thread.Sleep(1000); }
            }
        }

        private void Tick()
        {
            DateTime now = DateTime.Now;
            string today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            List<ScheduleTask> snapshot;
            lock (gate) { snapshot = new List<ScheduleTask>(tasks); }

            foreach (ScheduleTask t in snapshot)
            {
                if (!t.Enabled) { continue; }

                // Интервальная задача (например «сообщение раз в час»): считаем от
                // LastRunAt в UTC, чтобы смена часового пояса/перевод времени её не сбивали.
                if (t.IsInterval)
                {
                    DateTime lastUtc;
                    if (!DateTime.TryParseExact(t.LastRunAt, "yyyy-MM-dd HH:mm:ss",
                            CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out lastUtc))
                    {
                        // Задачу только что добавили — отсчёт начинается с этого момента.
                        t.LastRunAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                        Save();
                        continue;
                    }
                    if ((DateTime.UtcNow - lastUtc).TotalMinutes < t.EveryMinutes) { continue; }

                    t.LastRunAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    t.LastRunDay = today;
                    Save();
                    if (OnEvent != null) { OnEvent("по расписанию: " + t.Describe()); }
                    if (OnFire != null) { OnFire(t); }
                    continue;
                }

                // Countdown warnings, only for restarts and only once per minute per task.
                if (t.Kind == ScheduleKind.Restart && t.WarnMinutes > 0)
                {
                    DateTime target = new DateTime(now.Year, now.Month, now.Day, t.Hour, t.Minute, 0);
                    double minutesLeft = (target - now).TotalMinutes;
                    if (minutesLeft > 0 && minutesLeft <= t.WarnMinutes)
                    {
                        int mark = (int)Math.Ceiling(minutesLeft);
                        string key = today + "|" + t.Hour + ":" + t.Minute + "|" + mark;
                        bool fresh;
                        lock (gate) { fresh = warnedToday.Add(key); }
                        if (fresh && OnWarning != null) { OnWarning(t, mark); }
                    }
                }

                if (t.LastRunDay == today) { continue; }
                if (now.Hour != t.Hour || now.Minute != t.Minute) { continue; }

                t.LastRunDay = today;
                Save();
                if (OnEvent != null) { OnEvent("по расписанию: " + t.Describe()); }
                if (OnFire != null) { OnFire(t); }
            }

            if (now.Hour == 0 && now.Minute == 0)
            {
                lock (gate) { warnedToday.Clear(); }
            }
        }
    }
}
