using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Wolf.Server
{
    // Public status page served at "/" by the distribution HTTP server. Self-contained
    // (inline CSS, no external requests), refreshes every 15 seconds. Everything that comes
    // from the game is HTML-escaped: a player name must not be able to inject markup.
    public static class StatusPage
    {
        public static string Build(ServerApp app)
        {
            StringBuilder sb = new StringBuilder(8192);
            DateTime now = DateTime.UtcNow;
            bool running = app.Controller.Running || app.Controller.ExternalPid != 0;
            List<GamePlayer> players = app.LastPlayers ?? new List<GamePlayer>();
            PlayerStats stats = app.Stats;
            ModManifest pack = app.Manifest;

            sb.Append("<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\">");
            sb.Append("<meta http-equiv=\"refresh\" content=\"15\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.Append("<title>").Append(WolfInfo.HtmlEscape(string.IsNullOrEmpty(app.Settings.PackName)
                ? "Wolf Server" : app.Settings.PackName)).Append("</title>");
            sb.Append("<style>");
            sb.Append("body{margin:0;background:#181a1f;color:#dfe4ec;font:15px/1.5 Segoe UI,Arial,sans-serif}");
            sb.Append("header{background:#1f2229;border-bottom:1px solid #383e4a;padding:18px 24px}");
            sb.Append("h1{margin:0;font-size:22px}h2{font-size:16px;margin:0 0 10px}");
            sb.Append(".accent{color:#d67a2f}.ok{color:#60be78}.dim{color:#929ba8}.bad{color:#d05d5d}");
            sb.Append("main{max-width:1000px;margin:0 auto;padding:18px;display:grid;gap:14px}");
            sb.Append(".card{background:#1f2229;border:1px solid #383e4a;border-radius:10px;padding:16px}");
            sb.Append("table{width:100%;border-collapse:collapse}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid #2d323c}");
            sb.Append("th{color:#929ba8;font-weight:600;font-size:13px}tr:last-child td{border-bottom:0}");
            sb.Append("a{color:#d67a2f;text-decoration:none}a:hover{text-decoration:underline}");
            sb.Append(".links a{display:inline-block;margin:4px 14px 4px 0}");
            sb.Append(".big{font-size:26px;font-weight:700}");
            sb.Append("</style></head><body>");

            sb.Append("<header><h1><span class=\"accent\">Wolf</span>Server <span class=\"dim\">")
              .Append(WolfInfo.HtmlEscape(WolfInfo.Version)).Append("</span></h1>");
            sb.Append("<div class=\"dim\">").Append(WolfInfo.HtmlEscape(
                string.IsNullOrEmpty(app.Settings.PackName) ? "7 Days to Die" : app.Settings.PackName));
            sb.Append(" &middot; <span class=\"").Append(running ? "ok" : "bad").Append("\">")
              .Append(running ? "сервер работает" : "сервер остановлен").Append("</span>");
            string world = app.WorldName();
            if (!string.IsNullOrEmpty(world)) { sb.Append(" &middot; мир: ").Append(WolfInfo.HtmlEscape(world)); }
            sb.Append("</div></header><main>");

            // ---- online players ----
            sb.Append("<section class=\"card\"><h2>Игроки онлайн <span class=\"accent big\">")
              .Append(players.Count.ToString(CultureInfo.InvariantCulture)).Append("</span></h2>");
            if (players.Count == 0)
            {
                sb.Append("<div class=\"dim\">Сейчас на сервере никого нет.</div>");
            }
            else
            {
                sb.Append("<table><tr><th>Игрок</th><th>SteamID</th><th>Уровень</th><th>Пинг</th></tr>");
                foreach (GamePlayer p in players)
                {
                    sb.Append("<tr><td>").Append(WolfInfo.HtmlEscape(p.Name)).Append("</td><td class=\"dim\">")
                      .Append(WolfInfo.HtmlEscape(string.IsNullOrEmpty(p.SteamId) ? "—" : p.SteamId))
                      .Append("</td><td>").Append(WolfInfo.HtmlEscape(p.Level))
                      .Append("</td><td>").Append(WolfInfo.HtmlEscape(p.Ping)).Append("</td></tr>");
                }
                sb.Append("</table>");
            }
            sb.Append("<div class=\"dim\" style=\"margin-top:8px\">список обновляется инструментом каждые 20 с; страница — каждые 15 с</div></section>");

            // ---- mod pack ----
            sb.Append("<section class=\"card\"><h2>Набор модов</h2>");
            if (pack == null)
            {
                sb.Append("<div class=\"dim\">Набор ещё не опубликован.</div>");
            }
            else
            {
                sb.Append("<table><tr><th>Версия</th><th>Файлов</th><th>Размер</th><th>Хэш</th></tr><tr><td>")
                  .Append(WolfInfo.HtmlEscape(string.IsNullOrEmpty(pack.version) ? "—" : pack.version)).Append("</td><td>")
                  .Append(pack.files.Count.ToString(CultureInfo.InvariantCulture)).Append("</td><td>")
                  .Append(WolfInfo.HtmlEscape(GamePaths.HumanSize(pack.TotalBytes))).Append("</td><td class=\"dim\">")
                  .Append(WolfInfo.HtmlEscape(pack.ContentHash)).Append("</td></tr></table>");
                if (!string.IsNullOrEmpty(pack.note))
                {
                    sb.Append("<div style=\"margin-top:8px\">").Append(WolfInfo.HtmlEscape(pack.note)).Append("</div>");
                }
            }
            sb.Append("<div class=\"links\" style=\"margin-top:10px\">");
            sb.Append("<a href=\"/download\">Скачать лаунчер</a>");
            sb.Append("<a href=\"/manifest.json\">manifest.json</a>");
            sb.Append("<a href=\"/status\">JSON-статус</a>");
            if (!string.IsNullOrEmpty(app.Settings.VkUrl))
            {
                sb.Append("<a href=\"").Append(WolfInfo.HtmlEscape(app.Settings.VkUrl)).Append("\">Группа ВК</a>");
            }
            sb.Append("</div></section>");

            // ---- statistics ----
            sb.Append("<section class=\"card\"><h2>Статистика</h2><table>");
            sb.Append("<tr><td>Игроков всего</td><td class=\"accent\">")
              .Append(stats.TotalPlayers.ToString(CultureInfo.InvariantCulture)).Append("</td>");
            sb.Append("<td>Пик онлайна</td><td class=\"accent\">")
              .Append(stats.PeakOnline.ToString(CultureInfo.InvariantCulture)).Append("</td></tr>");
            sb.Append("<tr><td>Суммарное время в игре</td><td>")
              .Append(WolfInfo.HtmlEscape(WolfInfo.HumanDuration(stats.TotalPlaytime))).Append("</td>");
            sb.Append("<td>Сейчас онлайн</td><td>")
              .Append(stats.OnlineCount.ToString(CultureInfo.InvariantCulture)).Append("</td></tr>");
            sb.Append("</table></section>");

            sb.Append("<div class=\"dim\" style=\"text-align:center;padding-bottom:20px\">WolfSuite &middot; обновлено ")
              .Append(WolfInfo.HtmlEscape(now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)))
              .Append("</div>");
            sb.Append("</main></body></html>");
            return sb.ToString();
        }
    }
}
