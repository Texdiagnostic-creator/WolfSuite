using System;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Wolf
{
    // Version of whichever executable is running (both apps share this file) plus small
    // formatting helpers used by the web status page and the UI.
    public static class WolfInfo
    {
        public static string Version
        {
            get
            {
                try
                {
                    Version v = Assembly.GetExecutingAssembly().GetName().Version;
                    return v == null ? "?" : v.ToString(3);
                }
                catch { return "?"; }
            }
        }

        // Escapes text for the status page. Everything coming from the game (player names,
        // ban reasons) is untrusted and must not be able to inject markup.
        public static string HtmlEscape(string text)
        {
            if (string.IsNullOrEmpty(text)) { return ""; }
            StringBuilder sb = new StringBuilder(text.Length + 16);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        // Human duration for statistics: "3 д 4 ч", "2 ч 13 мин", "47 мин", "<1 мин".
        public static string HumanDuration(TimeSpan span)
        {
            if (span.TotalMinutes < 1) { return "<1 мин"; }
            if (span.TotalDays >= 1)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0} д {1} ч", (int)span.TotalDays, span.Hours);
            }
            if (span.TotalHours >= 1)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0} ч {1} мин", (int)span.TotalHours, span.Minutes);
            }
            return string.Format(CultureInfo.InvariantCulture, "{0} мин", (int)span.TotalMinutes);
        }
    }
}
