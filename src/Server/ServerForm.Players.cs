using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace Wolf.Server
{
    // Players tab: online list, kick/ban, teleport, chat broadcast.
    public sealed partial class ServerForm
    {
        private ListView playersList;
        private ListView bansList;
        private TextBox chatBox;
        private Label playersStatus;
        private Label bansStatus;
        private TextBox banDuration;
        private ComboBox banUnit;
        private TextBox consoleBox;

        private TabPage BuildPlayersTab()
        {
            TabPage page = NewTab("Игроки");

            Panel top = Theme.Card(10, 8, 1090, 74);
            top.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(top);

            top.Controls.Add(Theme.Label("Управление игроками", 14, 10).Bold());
            playersStatus = Theme.Dim("telnet не подключён — включите TelnetEnabled в конфиге", 14, 40);
            top.Controls.Add(playersStatus);

            Button refresh = Theme.Button("Обновить", 830, 12, 120, 30, delegate { RefreshPlayers(); }, false);
            top.Controls.Add(refresh);
            Button telnet = Theme.Button("Подключить Telnet", 958, 12, 118, 30, delegate { ConnectTelnet(); }, true);
            top.Controls.Add(telnet);

            playersList = Theme.StyleList(new ListView());
            playersList.View = View.Details;
            playersList.FullRowSelect = true;
            playersList.MultiSelect = false;
            playersList.Location = new Point(10, 90);
            playersList.Size = new Size(560, 240);
            playersList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
            playersList.Columns.Add("Имя", 190);
            playersList.Columns.Add("SteamID", 160);
            playersList.Columns.Add("id", 60);
            playersList.Columns.Add("Координаты", 150);
            playersList.Columns.Add("Ур.", 44);
            playersList.Columns.Add("HP", 44);
            playersList.Columns.Add("Пинг", 50);
            page.Controls.Add(playersList);

            Panel actions = Theme.Card(580, 90, 520, 240);
            actions.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            page.Controls.Add(actions);

            actions.Controls.Add(Theme.Label("Действия с выбранным игроком", 14, 8).Bold());

            Button kick = Theme.Button("Кикнуть", 14, 30, 118, 30, delegate { KickSelected(); }, false);
            actions.Controls.Add(kick);
            Button ban = Theme.Button("Забанить", 136, 30, 118, 30, delegate { BanSelected(); }, false);
            actions.Controls.Add(ban);
            Button profile = Theme.Button("Профиль", 258, 30, 118, 30, delegate { ShowSelectedProfile(); }, true);
            actions.Controls.Add(profile);
            Button unban = Theme.Button("Разбанить", 380, 30, 118, 30, delegate { UnbanSelected(); }, false);
            actions.Controls.Add(unban);

            // Двойной щелчок по строке открывает тот же профиль: так быстрее, чем целиться в кнопку.
            playersList.DoubleClick += delegate { ShowSelectedProfile(); };

            // V3.4 wants "ban add <id> <duration> <unit>": the length is asked up front.
            actions.Controls.Add(Theme.Label("Срок бана", 14, 68));
            banDuration = Theme.Input(120, 64, 60);
            banDuration.Text = "1";
            actions.Controls.Add(banDuration);
            banUnit = new ComboBox();
            banUnit.DropDownStyle = ComboBoxStyle.DropDownList;
            banUnit.Location = new Point(188, 64);
            banUnit.Size = new Size(110, 26);
            banUnit.Items.AddRange(new object[] { "minute(s)", "hour(s)", "day(s)", "week(s)" });
            banUnit.SelectedIndex = 2;
            actions.Controls.Add(banUnit);

            actions.Controls.Add(Theme.Label("Телепорт выбранного игрока", 14, 100));
            TextBox tx = Theme.Input(14, 122, 100);
            tx.Text = "0";
            actions.Controls.Add(tx);
            TextBox ty = Theme.Input(122, 122, 100);
            ty.Text = "64";
            actions.Controls.Add(ty);
            TextBox tz = Theme.Input(230, 122, 100);
            tz.Text = "0";
            actions.Controls.Add(tz);
            Button tp = Theme.Button("Телепорт", 340, 120, 146, 28, delegate
            {
                GamePlayer p = SelectedPlayer();
                if (p == null) { return; }
                SendCommand("teleportplayer " + p.Handle + " " + tx.Text + " " + ty.Text + " " + tz.Text);
            }, false);
            actions.Controls.Add(tp);

            Button tpToMe = Theme.Button("К себе (0,64,0)", 14, 154, 220, 28, delegate
            {
                GamePlayer p = SelectedPlayer();
                if (p == null) { return; }
                SendCommand("teleportplayer " + p.Handle + " 0 64 0");
            }, false);
            actions.Controls.Add(tpToMe);

            actions.Controls.Add(Theme.Label("Сообщение всем в чат", 14, 190));
            chatBox = Theme.Input(14, 212, 360);
            actions.Controls.Add(chatBox);
            Button say = Theme.Button("Отправить", 384, 210, 102, 28, delegate
            {
                if (string.IsNullOrWhiteSpace(chatBox.Text)) { return; }
                SendCommand(Telnet.SayCommand(chatBox.Text));
                app.Log.Add("[chat] " + chatBox.Text);
                chatBox.Clear();
            }, true);
            actions.Controls.Add(say);

            // Quick commands: the most used V3.4 console commands without leaving the tab.
            Panel quick = Theme.Card(10, 340, 1090, 78);
            quick.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(quick);
            quick.Controls.Add(Theme.Label("Быстрые команды", 14, 8).Bold());

            quick.Controls.Add(Theme.Button("☀ День", 14, 32, 100, 28,
                delegate { SendCommand("settime day"); }, false));
            quick.Controls.Add(Theme.Button("☾ Ночь", 122, 32, 100, 28,
                delegate { SendCommand("settime night"); }, false));
            quick.Controls.Add(Theme.Button("Сохранить мир", 230, 32, 130, 28,
                delegate { SendCommand("saveworld"); }, false));
            quick.Controls.Add(Theme.Button("Дождь", 368, 32, 90, 28,
                delegate { SendCommand("weather rain 1"); }, false));
            quick.Controls.Add(Theme.Button("Ясно", 466, 32, 90, 28,
                delegate { SendCommand("weather rain 0"); }, false));

            quick.Controls.Add(Theme.Dim("команда:", 566, 38));
            consoleBox = Theme.Input(634, 32, 300);
            quick.Controls.Add(consoleBox);
            Button run = Theme.Button("Выполнить", 942, 32, 130, 28, delegate
            {
                string cmd = (consoleBox.Text ?? "").Trim();
                if (cmd.Length == 0) { return; }
                SendCommand(cmd);
                consoleBox.Clear();
            }, true);
            quick.Controls.Add(run);

            bansStatus = Theme.Dim("", 10, 428);
            bansStatus.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            page.Controls.Add(bansStatus);

            bansList = Theme.StyleList(new ListView());
            bansList.View = View.Details;
            bansList.FullRowSelect = true;
            bansList.Location = new Point(10, 448);
            bansList.Size = new Size(1090, 100);
            bansList.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            bansList.Columns.Add("Имя", 260);
            bansList.Columns.Add("SteamID", 200);
            bansList.Columns.Add("Причина", 300);
            bansList.Columns.Add("До", 200);
            page.Controls.Add(bansList);

            return page;
        }

        private GamePlayer SelectedPlayer()
        {
            if (playersList.SelectedItems.Count == 0)
            {
                MessageBox.Show(this, "Выберите игрока в списке.", "WolfServer",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
            return playersList.SelectedItems[0].Tag as GamePlayer;
        }

        private void RefreshPlayers()
        {
            if (!app.Telnet.Connected)
            {
                playersStatus.Text = "консоль не подключена — переподключаюсь (TelnetEnabled и TelnetPort в serverconfig.xml)";
                playersStatus.ForeColor = Theme.Warn;
                if (app.Settings.TelnetAutoReconnect) { EnsureTelnet("вкладка «Игроки»"); }
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string reply = app.Telnet.Command("listplayers", 1500);
                    List<GamePlayer> players = Telnet.ParsePlayers(reply);
                    string banReply = app.Telnet.Command("ban list", 1200);
                    List<BannedPlayer> bans = Telnet.ParseBans(banReply);

                    this.Ui(delegate
                    {
                        playersList.Items.Clear();
                        foreach (GamePlayer p in players)
                        {
                            ListViewItem item = new ListViewItem(new[]
                            {
                                p.Name,
                                p.SteamId,
                                p.EntityId,
                                p.HasPosition ? string.Format(CultureInfo.InvariantCulture, "{0:0}, {1:0}, {2:0}", p.X, p.Y, p.Z) : "—",
                                p.Level,
                                p.Health,
                                p.Ping,
                            });
                            item.Tag = p;
                            playersList.Items.Add(item);
                        }
                        bansList.Items.Clear();
                        foreach (BannedPlayer b in bans)
                        {
                            ListViewItem item = new ListViewItem(new[] { b.Name, b.SteamId, b.Reason, b.Expires });
                            item.Tag = b;
                            bansList.Items.Add(item);
                        }
                        playersStatus.Text = "онлайн: " + players.Count + "   забанено: " + bans.Count;
                        playersStatus.ForeColor = Theme.Ok;
                        lblPlayers.Text = "игроков онлайн: " + players.Count;
                    });
                }
                catch (Exception ex)
                {
                    this.Ui(delegate
                    {
                        playersStatus.Text = "ошибка telnet: " + ex.Message;
                        playersStatus.ForeColor = Theme.Err;
                    });
                }
            });
        }

        private void RefreshPlayersTab()
        {
            RefreshPlayers();
        }

        // Профиль выбранного игрока: снимок данных из последнего «listplayers» и история
        // заходов. Кнопки внутри окна ведут себя как кнопки на вкладке, чтобы причина кика
        // и срок бана спрашивались в одном месте.
        private void ShowSelectedProfile()
        {
            GamePlayer p = SelectedPlayer();
            if (p == null) { return; }
            using (PlayerProfileForm form = new PlayerProfileForm(p, app.Stats, FindBan(p),
                delegate { KickSelected(); }, delegate { BanSelected(); }))
            {
                form.ShowDialog(this);
            }
        }

        // «Забанен» ищем по платформенному id, а если его в списке банов нет — по нику:
        // старые записи банов могут хранить только имя.
        private BannedPlayer FindBan(GamePlayer p)
        {
            if (bansList == null) { return null; }
            BannedPlayer byName = null;
            foreach (ListViewItem item in bansList.Items)
            {
                BannedPlayer b = item.Tag as BannedPlayer;
                if (b == null) { continue; }
                if (!string.IsNullOrEmpty(p.SteamId) && !string.IsNullOrEmpty(b.SteamId) &&
                    string.Equals(b.SteamId, p.SteamId, StringComparison.OrdinalIgnoreCase))
                {
                    return b;
                }
                if (byName == null && string.Equals(b.Name, p.Name, StringComparison.OrdinalIgnoreCase))
                {
                    byName = b;
                }
            }
            return byName;
        }

        private void KickSelected()
        {
            GamePlayer p = SelectedPlayer();
            if (p == null) { return; }
            string reason = Prompt.Show("Причина кика:", "WolfServer", "");
            if (reason == null) { return; }
            SendCommand("kick " + p.Handle + (string.IsNullOrEmpty(reason) ? "" : " \"" + reason.Replace("\"", "'") + "\""));
            app.Log.Add("kick: " + p.Name);
            RefreshPlayers();
        }

        // V3.4 requires a duration and prefers a platform id, which also works once the player
        // has already left: "ban add Steam_7656... 1 day(s) \"reason\"".
        private void BanSelected()
        {
            GamePlayer p = SelectedPlayer();
            if (p == null) { return; }
            string reason = Prompt.Show("Причина бана:", "WolfServer", "");
            if (reason == null) { return; }
            int amount;
            if (!int.TryParse(banDuration.Text.Trim(), out amount) || amount <= 0) { amount = 1; }
            string unit = banUnit.SelectedItem == null ? "day(s)" : banUnit.SelectedItem.ToString();
            string target = string.IsNullOrEmpty(p.SteamId) ? p.Handle : p.SteamId;
            SendCommand(Telnet.BanAddCommand(target, amount, unit, reason));
            app.Log.Add("ban: " + p.Name + " (" + target + ") " + amount + " " + unit);
            RefreshPlayers();
        }

        private void UnbanSelected()
        {
            if (bansList.SelectedItems.Count == 0) { return; }
            BannedPlayer b = bansList.SelectedItems[0].Tag as BannedPlayer;
            if (b == null) { return; }
            string target = string.IsNullOrEmpty(b.SteamId) ? b.Name : b.SteamId;
            SendCommand("unban " + target);
            app.Log.Add("разбанен: " + b.Name);
            RefreshPlayers();
        }

        private void SendCommand(string cmd)
        {
            if (!app.Telnet.Connected)
            {
                MessageBox.Show(this, "Telnet не подключён.", "WolfServer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                string reply = app.Telnet.Command(cmd, 1200);
                app.Log.Add("> " + cmd + (string.IsNullOrEmpty(reply) ? "" : "  ->  " + reply.Replace("\n", " | ")));
            }
            catch (Exception ex)
            {
                app.Log.Add("telnet: " + ex.Message);
            }
        }
    }

    // Small single-line prompt used for kick/ban reasons.
    internal static class Prompt
    {
        public static string Show(string caption, string title, string preset)
        {
            using (Form form = new Form())
            {
                form.Text = title;
                form.Width = 460;
                form.Height = 170;
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                Theme.Apply(form);

                Label label = Theme.Label(caption, 14, 14);
                form.Controls.Add(label);

                TextBox box = Theme.Input(14, 40, 416);
                box.Text = preset ?? "";
                form.Controls.Add(box);

                Button ok = Theme.Button("OK", 230, 82, 100, 30, delegate { form.DialogResult = DialogResult.OK; }, true);
                Button cancel = Theme.Button("Отмена", 340, 82, 90, 30, delegate { form.DialogResult = DialogResult.Cancel; }, false);
                form.Controls.Add(ok);
                form.Controls.Add(cancel);
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                return form.ShowDialog() == DialogResult.OK ? box.Text.Trim() : null;
            }
        }
    }
}
