using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Wolf.Server
{
    // Statistics tab: lifetime totals, peak online and the session history collected by
    // PollPlayers (every 20 s). Data survives tool and server restarts (players.json).
    public sealed partial class ServerForm
    {
        private Label statsTotals;
        private Label statsTime;
        private ListView statsList;

        private TabPage BuildStatsTab()
        {
            TabPage page = NewTab("Статистика");

            Panel top = Theme.Card(10, 8, 1090, 110);
            top.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(top);

            top.Controls.Add(Theme.Label("Статистика онлайна", 14, 8).Bold());
            statsTotals = Theme.Dim("", 14, 40);
            top.Controls.Add(statsTotals);
            statsTime = Theme.Dim("", 14, 68);
            top.Controls.Add(statsTime);

            Button refresh = Theme.Button("Обновить", 830, 12, 120, 30, delegate { RefreshStats(); }, false);
            top.Controls.Add(refresh);
            Button reset = Theme.Button("Сбросить", 958, 12, 118, 30, delegate
            {
                if (MessageBox.Show(this, "Удалить всю статистику игроков?\n\nЭто не влияет на сервер и сохранения.",
                    "WolfServer", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    return;
                }
                app.Stats.Reset();
                app.SaveStats();
                app.Log.Add("статистика игроков сброшена");
                RefreshStats();
            }, false);
            top.Controls.Add(reset);

            statsList = Theme.StyleList(new ListView());
            statsList.View = View.Details;
            statsList.FullRowSelect = true;
            statsList.Location = new Point(10, 128);
            statsList.Size = new Size(1090, 490);
            statsList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            statsList.Columns.Add("Игрок", 190);
            statsList.Columns.Add("SteamID", 170);
            statsList.Columns.Add("Зашёл", 150);
            statsList.Columns.Add("Играл", 110);
            statsList.Columns.Add("Статус", 90);
            statsList.Columns.Add("Последний раз", 160);
            page.Controls.Add(statsList);

            return page;
        }

        private void RefreshStats()
        {
            if (statsList == null) { return; }
            PlayerStats stats = app.Stats;
            DateTime now = DateTime.UtcNow;

            statsTotals.Text = "игроков всего: " + stats.TotalPlayers.ToString(CultureInfo.InvariantCulture) +
                               "   сейчас онлайн: " + stats.OnlineCount.ToString(CultureInfo.InvariantCulture) +
                               "   пик онлайна: " + stats.PeakOnline.ToString(CultureInfo.InvariantCulture) +
                               "   сессий в истории: " + stats.Sessions.Count.ToString(CultureInfo.InvariantCulture);
            statsTime.Text = "суммарное время в игре: " + WolfInfo.HumanDuration(stats.TotalPlaytime) +
                             "   (включая текущие сессии)";

            statsList.Items.Clear();
            foreach (PlayerSession s in stats.Sessions)
            {
                TimeSpan duration = s.Duration(now);
                ListViewItem item = new ListViewItem(new[]
                {
                    s.name,
                    string.IsNullOrEmpty(s.steamId) ? "—" : s.steamId,
                    s.joinedUtc == DateTime.MinValue ? "—" : s.joinedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    WolfInfo.HumanDuration(duration),
                    s.active ? "онлайн" : "вышел",
                    s.active ? "сейчас" : (s.lastSeenUtc == DateTime.MinValue
                        ? "—"
                        : s.lastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                });
                item.ForeColor = s.active ? Theme.Ok : Theme.Text;
                statsList.Items.Add(item);
            }
        }
    }
}
