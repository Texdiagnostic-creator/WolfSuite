using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Wolf
{
    // Sends short notifications to Telegram and/or VK. Both are optional and configured in
    // WolfServer's settings tab; failures are logged without exposing tokens.
    // Messages are queued and sent from a background thread, so the UI never waits on HTTP.
    public sealed class Notifier : IDisposable
    {
        public string TelegramToken = "";
        public string TelegramChat = "";
        public string VkToken = "";
        public string VkPeer = "";
        public string VkVersion = "5.199";

        private readonly Queue<string> queue = new Queue<string>();
        private readonly object gate = new object();
        private readonly AutoResetEvent signal = new AutoResetEvent(false);
        private Thread worker;
        private volatile bool running;

        public bool TelegramReady
        {
            get { return !string.IsNullOrEmpty(TelegramToken) && !string.IsNullOrEmpty(TelegramChat); }
        }

        public bool VkReady
        {
            get { return !string.IsNullOrEmpty(VkToken) && !string.IsNullOrEmpty(VkPeer); }
        }

        public bool Enabled { get { return TelegramReady || VkReady; } }

        // ---------- message formats (testable) ----------

        public static string TelegramUrl(string token)
        {
            return "https://api.telegram.org/bot" + (token ?? "") + "/sendMessage";
        }

        public static Dictionary<string, string> TelegramFields(string chatId, string text)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>();
            fields["chat_id"] = chatId ?? "";
            fields["text"] = Clip(text);
            fields["disable_web_page_preview"] = "true";
            return fields;
        }

        public const string VkMethodUrl = "https://api.vk.com/method/messages.send";

        public static Dictionary<string, string> VkFields(string token, string peer, string text, int randomId, string version)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>();
            fields["peer_id"] = peer ?? "";
            fields["message"] = Clip(text);
            fields["random_id"] = randomId.ToString(CultureInfo.InvariantCulture);
            fields["access_token"] = token ?? "";
            fields["v"] = string.IsNullOrEmpty(version) ? "5.199" : version;
            return fields;
        }

        // Keeps first and last characters so two different tokens are distinguishable in logs.
        public static string Mask(string token)
        {
            if (string.IsNullOrEmpty(token)) { return ""; }
            if (token.Length <= 8) { return new string('*', token.Length); }
            return token.Substring(0, 4) + "…" + token.Substring(token.Length - 4);
        }

        private static string Clip(string text)
        {
            string s = (text ?? "").Trim();
            // Лимит — 3500 символов ВМЕСТЕ с многоточием: 3499 + "…" = ровно 3500.
            return s.Length <= 3500 ? s : s.Substring(0, 3499) + "…";
        }

        // ---------- delivery ----------

        // Sends synchronously. Returns true when at least one configured channel accepted it.
        public bool SendNow(string text, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(text)) { return false; }
            if (!Enabled)
            {
                error = "уведомления не настроены";
                return false;
            }

            List<string> problems = new List<string>();
            bool sent = false;

            if (TelegramReady)
            {
                try
                {
                    string response = Http.PostForm(TelegramUrl(TelegramToken),
                        TelegramFields(TelegramChat, text), 10000);
                    Dictionary<string, object> d = Json.ParseObject(response);
                    if (Json.Bool(d, "ok", false)) { sent = true; }
                    else { problems.Add("telegram: " + Json.Str(d, "description", "отказ")); }
                }
                catch (Exception ex) { problems.Add("telegram: " + ex.Message); }
            }

            if (!sent && VkReady)
            {
                try
                {
                    int randomId = Math.Abs(Environment.TickCount) ^ text.GetHashCode();
                    string response = Http.PostForm(VkMethodUrl,
                        VkFields(VkToken, VkPeer, text, randomId, VkVersion), 10000);
                    Dictionary<string, object> d = Json.ParseObject(response);
                    Dictionary<string, object> err = Json.Obj(d, "error");
                    if (d != null && d.ContainsKey("error"))
                    {
                        problems.Add("vk: " + Json.Str(err, "error_msg", "код " + Json.Int(err, "error_code", 0)));
                    }
                    else { sent = true; }
                }
                catch (Exception ex) { problems.Add("vk: " + ex.Message); }
            }

            if (!sent) { error = string.Join("; ", problems.ToArray()); }
            return sent;
        }

        // Fire-and-forget for the UI thread.
        public void SendAsync(string text, Action<string> log)
        {
            if (!Enabled || string.IsNullOrEmpty(text)) { return; }
            lock (gate) { queue.Enqueue(text); }
            StartWorker();
            signal.Set();
            if (log != null) { /* nothing to say until it is delivered */ }
        }

        private void StartWorker()
        {
            if (running) { return; }
            running = true;
            worker = new Thread(Loop) { IsBackground = true, Name = "Notifier" };
            worker.Start();
        }

        private void Loop()
        {
            while (running)
            {
                string text = null;
                lock (gate)
                {
                    if (queue.Count > 0) { text = queue.Dequeue(); }
                }
                if (text == null)
                {
                    signal.WaitOne(5000);
                    continue;
                }
                string error;
                if (!SendNow(text, out error))
                {
                    // The log lives on the other side; the notifier keeps no UI. The server
                    // polls FailedCount to show it once instead of on every message.
                    LastError = error;
                    FailedCount++;
                }
            }
        }

        public volatile int FailedCount;
        public volatile string LastError = "";

        public void Dispose()
        {
            running = false;
            signal.Set();
            try { if (worker != null) { worker.Join(1000); } } catch { }
        }
    }
}
