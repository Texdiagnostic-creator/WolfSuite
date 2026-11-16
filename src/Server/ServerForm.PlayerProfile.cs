using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Wolf.Server
{
    // Профиль игрока: снимок строки из «listplayers» плюс история сессий из players.json.
    // Кик и бан здесь вызывают те же действия, что и кнопки на вкладке «Игроки», поэтому
    // правила (срок бана, подтверждение причины) не дублируются.
    internal sealed class PlayerProfileForm : Form
    {
        private readonly GamePlayer player;
        private readonly Action onKick;
        private readonly Action onBan;

        public PlayerProfileForm(GamePlayer player, PlayerStats stats, BannedPlayer ban, Action onKick, Action onBan)
        {
            this.player = player;
            this.onKick = onKick;
            this.onBan = onBan;

            Text = "Профиль игрока — " + player.Name;
            Width = 560;
            Height = 560;
            MinimumSize = new Size(520, 520);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            Theme.Apply(this);

            Label title = Theme.Label(player.Name, 16, 12);
            title.Font = Theme.Font(14f, FontStyle.Bold);
            Controls.Add(title);

            Label subtitle = Theme.Dim(StatusLine(), 18, 40);
            Controls.Add(subtitle);

            Panel card = Theme.Card(14, 68, 512, 292);
            card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(card);

            int y = 12;
            AddRow(card, "Платформенный ID", string.IsNullOrEmpty(player.SteamId) ? "—" : player.SteamId, ref y);
            AddRow(card, "Entity id", string.IsNullOrEmpty(player.EntityId) ? "—" : player.EntityId, ref y);
            AddRow(card, "IP", string.IsNullOrEmpty(player.Ip) ? "—" : player.Ip, ref y);
            AddRow(card, "Пинг", string.IsNullOrEmpty(player.Ping) ? "—" : player.Ping, ref y);
            AddRow(card, "Уровень", string.IsNullOrEmpty(player.Level) ? "—" : player.Level, ref y);
            AddRow(card, "Здоровье", string.IsNullOrEmpty(player.Health) ? "—" : player.Health, ref y);
            AddRow(card, "Бан", ban == null
                ? "нет"
                : "да — " + (string.IsNullOrEmpty(ban.Reason) ? "без причины" : ban.Reason) +
                  (string.IsNullOrEmpty(ban.Expires) ? "" : ", до " + ban.Expires), ref y);

            Panel history = Theme.Card(14, 368, 512, 104);
            history.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(history);
            history.Controls.Add(Theme.Label("История на сервере", 12, 8).Bold());

            Label hist = Theme.Dim(HistoryLine(stats, player), 12, 34);
            hist.MaximumSize = new Size(480, 0);
            history.Controls.Add(hist);

            Button kick = Theme.Button("Кикнуть", 14, 482, 116, 32, delegate
            {
                onKick();
                Close();
            }, false);
            kick.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(kick);

            Button banButton = Theme.Button("Забанить", 140, 482, 116, 32, delegate
            {
                onBan();
                Close();
            }, false);
            banButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(banButton);

            Button copy = Theme.Button("Копировать ID", 266, 482, 140, 32, delegate
            {
                string id = string.IsNullOrEmpty(player.SteamId) ? player.EntityId : player.SteamId;
                if (string.IsNullOrEmpty(id)) { return; }
                try { Clipboard.SetText(id); } catch { }
            }, false);
            copy.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(copy);

            Button close = Theme.Button("Закрыть", 416, 482, 110, 32, delegate { Close(); }, true);
            close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            Controls.Add(close);
            CancelButton = close;
        }

        private string StatusLine()
        {
            string where = player.HasPosition
                ? string.Format(CultureInfo.InvariantCulture, "в мире на {0:0}, {1:0}, {2:0}", player.X, player.Y, player.Z)
                : "позиция неизвестна";
            return "онлайн, " + where;
        }

        private static void AddRow(Panel card, string caption, string value, ref int y)
        {
            Label key = Theme.Dim(caption, 12, y);
            key.Width = 150;
            card.Controls.Add(key);

            Label val = Theme.Label(value, 168, y);
            val.MaximumSize = new Size(330, 0);
            card.Controls.Add(val);
            y += 32;
        }

        // Сводка по players.json: сколько раз заходил, сколько всего наиграл, когда видели
        // последний раз. Совпадение — по платформенному id, для старых записей — по нику.
        private static string HistoryLine(PlayerStats stats, GamePlayer player)
        {
            if (stats == null) { return "статистика недоступна"; }

            int sessions = 0;
            double seconds = 0;
            DateTime lastSeen = DateTime.MinValue;
            PlayerSession active = null;

            foreach (PlayerSession s in stats.Sessions)
            {
                bool same;
                if (!string.IsNullOrEmpty(player.SteamId) && !string.IsNullOrEmpty(s.steamId))
                {
                    same = string.Equals(s.steamId, player.SteamId, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    same = string.Equals(s.name, player.Name, StringComparison.OrdinalIgnoreCase);
                }
                if (!same) { continue; }

                sessions++;
                seconds += s.Duration(DateTime.UtcNow).TotalSeconds;
                if (s.lastSeenUtc > lastSeen) { lastSeen = s.lastSeenUtc; }
                if (s.active) { active = s; }
            }

            if (sessions == 0) { return "записей нет — игрок зашёл впервые либо статистика сброшена"; }

            string text = "заходов: " + sessions.ToString(CultureInfo.InvariantCulture) +
                          "   всего в игре: " + WolfInfo.HumanDuration(TimeSpan.FromSeconds(seconds));
            if (active != null)
            {
                text += "   текущая сессия: " + WolfInfo.HumanDuration(active.Duration(DateTime.UtcNow));
            }
            if (lastSeen != DateTime.MinValue)
            {
                text += "\nпоследняя активность: " +
                        lastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            return text;
        }
    }
}
