using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Wolf.Server
{
    // Settings tab: paths, ports, retention, auto restart.
    public sealed partial class ServerForm
    {
        private TabPage BuildSettingsTab()
        {
            TabPage page = NewTab("Настройки");

            Panel scroll = new Panel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            scroll.BackColor = Theme.Bg;
            page.Controls.Add(scroll);

            int y = 10;
            y = AddPathRow(scroll, y, "Папка сервера", app.Settings.ServerRoot, delegate(string v)
            {
                app.Settings.ServerRoot = v;
                app.Save();
                app.Controller.Root = v;
                lblServerName.Text = v;
            });

            y = AddPathRow(scroll, y, "serverconfig.xml", app.Settings.ConfigFile, delegate(string v)
            {
                app.Settings.ConfigFile = v;
                app.Save();
                app.Config.Load(v);
                LoadConfigIntoUi();
            });

            y = AddPathRow(scroll, y, "steamcmd.exe", app.Settings.SteamCmd, delegate(string v)
            {
                app.Settings.SteamCmd = v;
                app.Save();
            });

            y = AddPathRow(scroll, y, "Папка модов для раздачи", app.Settings.ModsSource, delegate(string v)
            {
                app.Settings.ModsSource = v;
                app.Save();
            });

            y = AddPathRow(scroll, y, "Папка бэкапов", app.Settings.BackupFolder, delegate(string v)
            {
                app.Settings.BackupFolder = v;
                app.Save();
            });

            y = AddPathRow(scroll, y, "Скрипт запуска (.bat)", app.Settings.LaunchScript, delegate(string v)
            {
                app.Settings.LaunchScript = v;
                app.Save();
            });

            y += 12;
            scroll.Controls.Add(Theme.Label("Запуск сервера", 12, y).Bold());
            y += 30;

            CheckBox launchViaScript = new CheckBox();
            launchViaScript.Text = "запускать через start_server1.bat (affinity, приоритет High, цикл перезапуска)";
            launchViaScript.Checked = app.Settings.LaunchViaScript;
            launchViaScript.Location = new Point(12, y);
            launchViaScript.AutoSize = true;
            launchViaScript.ForeColor = Theme.Text;
            launchViaScript.BackColor = Color.Transparent;
            launchViaScript.CheckedChanged += delegate
            {
                app.Settings.LaunchViaScript = launchViaScript.Checked;
                app.Save();
                app.Controller.LaunchScript = app.Settings.LaunchScript;
                app.Log.Add(launchViaScript.Checked
                    ? "запуск сервера: через скрипт " + (app.Controller.ResolveLaunchScript() ?? "(не найден)") 
                    : "запуск сервера: напрямую 7DaysToDieServer.exe");
            };
            scroll.Controls.Add(launchViaScript);
            y += 28;

            y += 12;
            scroll.Controls.Add(Theme.Label("Раздача клиентам", 12, y).Bold());
            y += 30;

            y = AddNumberRow(scroll, y, "HTTP-порт (манифест и чанки)", app.Settings.PublishPort, 1024, 65535, delegate(int v)
            {
                app.Settings.PublishPort = v;
                app.Save();
                lblUrl.Text = "клиентам: " + app.DisplayBaseUrl();
            });

            y = AddNumberRow(scroll, y, "P2P-порт (обмен между игроками)", app.Settings.PeerPort, 1024, 65535, delegate(int v)
            {
                app.Settings.PeerPort = v;
                app.Save();
            });

            CheckBox serveOnStart = new CheckBox();
            serveOnStart.Text = "поднимать раздачу при запуске программы";
            serveOnStart.Checked = app.Settings.ServeOnStart;
            serveOnStart.Location = new Point(12, y);
            serveOnStart.AutoSize = true;
            serveOnStart.ForeColor = Theme.Text;
            serveOnStart.BackColor = Color.Transparent;
            serveOnStart.CheckedChanged += delegate
            {
                app.Settings.ServeOnStart = serveOnStart.Checked;
                app.Save();
            };
            scroll.Controls.Add(serveOnStart);
            y += 28;

            CheckBox autoPublish = new CheckBox();
            autoPublish.Text = "пересобирать набор при изменении папки Mods (как auto_publish в ModSync)";
            autoPublish.Checked = app.Settings.AutoPublish;
            autoPublish.Location = new Point(12, y);
            autoPublish.AutoSize = true;
            autoPublish.ForeColor = Theme.Text;
            autoPublish.BackColor = Color.Transparent;
            autoPublish.CheckedChanged += delegate
            {
                app.Settings.AutoPublish = autoPublish.Checked;
                app.Save();
                StartModsWatcher();
            };
            scroll.Controls.Add(autoPublish);
            y += 28;

            y = AddNumberRow(scroll, y, "Задержка автопубликации, с", app.Settings.AutoPublishDelaySec, 5, 600, delegate(int v)
            {
                app.Settings.AutoPublishDelaySec = v;
                app.Save();
            });

            CheckBox shipServerOnly = new CheckBox();
            shipServerOnly.Text = "раздавать и серверные моды (ServerOnlyMods античита) — по умолчанию выключено";
            shipServerOnly.Checked = app.Settings.ShipServerOnlyMods;
            shipServerOnly.Location = new Point(12, y);
            shipServerOnly.AutoSize = true;
            shipServerOnly.ForeColor = Theme.Text;
            shipServerOnly.BackColor = Color.Transparent;
            shipServerOnly.CheckedChanged += delegate
            {
                app.Settings.ShipServerOnlyMods = shipServerOnly.Checked;
                app.Save();
            };
            scroll.Controls.Add(shipServerOnly);
            y += 28;

            Button openExclude = Theme.Button("Свой список исключений…", 12, y, 250, 30, delegate
            {
                string path = Path.Combine(Path.GetDirectoryName(app.SettingsFile), "exclude.txt");
                if (!File.Exists(path))
                {
                    try
                    {
                        File.WriteAllText(path,
                            "# Моды, которые НЕ раздаются клиентам: по одному имени папки на строку.\r\n" +
                            "# Строки с * или ? трактуются как шаблоны пути внутри Mods.\r\n" +
                            "# Правила ModSync (exclude_patterns) применяются всегда и здесь не дублируются.\r\n",
                            new System.Text.UTF8Encoding(false));
                    }
                    catch { }
                }
                try { Proc.StartDetached("notepad.exe", "\"" + path + "\"", null); } catch { }
            }, false);
            scroll.Controls.Add(openExclude);

            Button pickLauncher = Theme.Button("Файл лаунчера…", 274, y, 180, 30, delegate
            {
                using (OpenFileDialog dlg = new OpenFileDialog())
                {
                    dlg.Title = "WolfLauncher.exe для раздачи игрокам";
                    dlg.Filter = "WolfLauncher|WolfLauncher.exe|Все exe|*.exe";
                    string current = app.LauncherFile();
                    if (current != null) { dlg.InitialDirectory = Path.GetDirectoryName(current); }
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        app.Settings.LauncherPath = dlg.FileName;
                        app.Save();
                        app.Log.Add("лаунчер для раздачи: " + dlg.FileName);
                        PublishPack(true);
                    }
                }
            }, false);
            scroll.Controls.Add(pickLauncher);
            y += 38;

            y = AddTextRow(scroll, y, "Название набора", app.Settings.PackName, delegate(string v)
            {
                app.Settings.PackName = v;
                app.Save();
            });

            y = AddTextRow(scroll, y, "Версия набора", app.Settings.PackVersion, delegate(string v)
            {
                app.Settings.PackVersion = v;
                app.Save();
            });

            y += 12;
            scroll.Controls.Add(Theme.Label("Уведомления (Telegram / VK)", 12, y).Bold());
            y += 30;

            scroll.Controls.Add(Theme.Dim("Пустой токен или чат отключает канал. Сообщения: вход/выход игроков, публикация набора, запуск и остановка сервера.", 12, y));
            y += 26;

            y = AddTextRow(scroll, y, "Telegram: токен бота", app.Settings.TelegramToken, delegate(string v)
            {
                app.Settings.TelegramToken = v;
                app.Save();
                app.ApplyNotifier();
            });

            y = AddTextRow(scroll, y, "Telegram: chat_id", app.Settings.TelegramChat, delegate(string v)
            {
                app.Settings.TelegramChat = v;
                app.Save();
                app.ApplyNotifier();
            });

            y = AddTextRow(scroll, y, "VK: токен сообщества", app.Settings.VkToken, delegate(string v)
            {
                app.Settings.VkToken = v;
                app.Save();
                app.ApplyNotifier();
            });

            y = AddTextRow(scroll, y, "VK: peer_id (беседа)", app.Settings.VkPeer, delegate(string v)
            {
                app.Settings.VkPeer = v;
                app.Save();
                app.ApplyNotifier();
            });

            CheckBox notifyJoin = new CheckBox();
            notifyJoin.Text = "сообщать о входе и выходе игроков";
            notifyJoin.Checked = app.Settings.NotifyJoin;
            notifyJoin.Location = new Point(12, y);
            notifyJoin.AutoSize = true;
            notifyJoin.ForeColor = Theme.Text;
            notifyJoin.BackColor = Color.Transparent;
            notifyJoin.CheckedChanged += delegate { app.Settings.NotifyJoin = notifyJoin.Checked; app.Save(); };
            scroll.Controls.Add(notifyJoin);
            y += 28;

            CheckBox notifyPack = new CheckBox();
            notifyPack.Text = "сообщать о публикации нового набора модов";
            notifyPack.Checked = app.Settings.NotifyPack;
            notifyPack.Location = new Point(12, y);
            notifyPack.AutoSize = true;
            notifyPack.ForeColor = Theme.Text;
            notifyPack.BackColor = Color.Transparent;
            notifyPack.CheckedChanged += delegate { app.Settings.NotifyPack = notifyPack.Checked; app.Save(); };
            scroll.Controls.Add(notifyPack);
            y += 28;

            CheckBox notifyServer = new CheckBox();
            notifyServer.Text = "сообщать о запуске и остановке сервера";
            notifyServer.Checked = app.Settings.NotifyServer;
            notifyServer.Location = new Point(12, y);
            notifyServer.AutoSize = true;
            notifyServer.ForeColor = Theme.Text;
            notifyServer.BackColor = Color.Transparent;
            notifyServer.CheckedChanged += delegate { app.Settings.NotifyServer = notifyServer.Checked; app.Save(); };
            scroll.Controls.Add(notifyServer);
            y += 34;

            Button testNotify = Theme.Button("Отправить тестовое уведомление", 12, y, 280, 30, delegate
            {
                app.ApplyNotifier();
                if (!app.Notifier.Enabled)
                {
                    MessageBox.Show(this, "Укажите токен и чат (peer_id) хотя бы для одного канала.",
                        "WolfServer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                string text = "Проверка уведомлений WolfServer " + WolfInfo.Version +
                              " · " + (string.IsNullOrEmpty(app.Settings.PackName) ? "сервер" : app.Settings.PackName);
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string error;
                    bool sent = app.Notifier.SendNow(text, out error);
                    app.Log.Add(sent ? "тестовое уведомление отправлено" : "тестовое уведомление не отправлено: " + error);
                    this.Ui(delegate
                    {
                        MessageBox.Show(this, sent ? "Уведомление отправлено." : "Не удалось отправить:\n" + error,
                            "WolfServer", MessageBoxButtons.OK, sent ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    });
                });
            }, false);
            scroll.Controls.Add(testNotify);

            Label notifyHint = Theme.Dim("Токены хранятся в settings.json в папке настроек; в журнал они не пишутся.", 302, y + 6);
            scroll.Controls.Add(notifyHint);
            y += 46;

            y += 12;
            scroll.Controls.Add(Theme.Label("Консоль сервера (telnet)", 12, y).Bold());
            y += 30;

            Label telnetHint = Theme.Dim(
                "Через консоль уходит сообщение в игровой чат и команды игрокам (кик, бан, телепорт). " +
                "Порт и пароль берутся из serverconfig.xml (TelnetPort, TelnetPassword).",
                12, y);
            telnetHint.MaximumSize = new Size(1000, 0);
            scroll.Controls.Add(telnetHint);
            y += 30;

            CheckBox telnetAuto = new CheckBox();
            telnetAuto.Text = "сам переподключать консоль после перезапуска сервера и обрыва связи";
            telnetAuto.Checked = app.Settings.TelnetAutoReconnect;
            telnetAuto.Location = new Point(12, y);
            telnetAuto.AutoSize = true;
            telnetAuto.ForeColor = Theme.Text;
            telnetAuto.BackColor = Color.Transparent;
            telnetAuto.CheckedChanged += delegate
            {
                app.Settings.TelnetAutoReconnect = telnetAuto.Checked;
                app.Save();
                if (telnetAuto.Checked) { EnsureTelnet("включено в настройках"); }
            };
            scroll.Controls.Add(telnetAuto);
            y += 28;

            y = AddNumberRow(scroll, y, "Проверять связь каждые, с", app.Settings.TelnetReconnectSeconds, 2, 120, delegate(int v)
            {
                app.Settings.TelnetReconnectSeconds = v;
                app.Save();
            });

            Button telnetNow = Theme.Button("Переподключить сейчас", 12, y, 230, 30,
                delegate { EnsureTelnet("вручную"); }, false);
            scroll.Controls.Add(telnetNow);
            y += 44;

            y += 12;
            scroll.Controls.Add(Theme.Label("Обслуживание", 12, y).Bold());
            y += 30;

            y = AddNumberRow(scroll, y, "Хранить последних бэкапов", app.Settings.BackupKeepLast, 1, 500, delegate(int v)
            {
                app.Settings.BackupKeepLast = v;
                app.Backups.KeepLast = v;
                app.Save();
            });

            y = AddNumberRow(scroll, y, "Хранить дней истории", app.Settings.BackupKeepDays, 1, 365, delegate(int v)
            {
                app.Settings.BackupKeepDays = v;
                app.Backups.KeepDays = v;
                app.Save();
            });

            CheckBox autoRestart = new CheckBox();
            autoRestart.Text = "автоматически перезапускать сервер после падения";
            autoRestart.Checked = app.Settings.AutoRestart;
            autoRestart.Location = new Point(12, y);
            autoRestart.AutoSize = true;
            autoRestart.ForeColor = Theme.Text;
            autoRestart.BackColor = Color.Transparent;
            autoRestart.CheckedChanged += delegate
            {
                app.Settings.AutoRestart = autoRestart.Checked;
                app.Controller.AutoRestart = autoRestart.Checked;
                app.Save();
            };
            scroll.Controls.Add(autoRestart);
            y += 30;

            CheckBox backupBeforeRestart = new CheckBox();
            backupBeforeRestart.Text = "делать бэкап перед каждым перезапуском";
            backupBeforeRestart.Checked = app.Settings.BackupBeforeRestart;
            backupBeforeRestart.Location = new Point(12, y);
            backupBeforeRestart.AutoSize = true;
            backupBeforeRestart.ForeColor = Theme.Text;
            backupBeforeRestart.BackColor = Color.Transparent;
            backupBeforeRestart.CheckedChanged += delegate
            {
                app.Settings.BackupBeforeRestart = backupBeforeRestart.Checked;
                app.Save();
            };
            scroll.Controls.Add(backupBeforeRestart);
            y += 44;

            Label paths = Theme.Dim("Папка настроек: " + Path.GetDirectoryName(app.SettingsFile), 12, y);
            scroll.Controls.Add(paths);
            y += 24;

            Button openSettings = Theme.Button("Открыть папку настроек", 12, y, 220, 30,
                delegate { app.OpenFolder(Path.GetDirectoryName(app.SettingsFile)); }, false);
            scroll.Controls.Add(openSettings);

            Button rescan = Theme.Button("Найти всё заново", 242, y, 180, 30, delegate
            {
                app.Settings.ServerRoot = "";
                app.Settings.ConfigFile = "";
                app.Settings.SteamCmd = "";
                app.Save();
                LoadState();
                MessageBox.Show(this, "Поиск завершён. Проверьте пути.", "WolfServer",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }, true);
            scroll.Controls.Add(rescan);

            return page;
        }

        private int AddPathRow(Control parent, int y, string caption, string value, Action<string> onChanged)
        {
            Label label = Theme.Label(caption, 12, y + 4);
            label.Width = 240;
            parent.Controls.Add(label);

            TextBox box = Theme.Input(260, y, 700);
            box.Text = value ?? "";
            parent.Controls.Add(box);

            Button browse = Theme.Button("…", 968, y - 1, 34, 26, delegate
            {
                using (FolderBrowserDialog dlg = new FolderBrowserDialog())
                {
                    dlg.Description = caption;
                    dlg.SelectedPath = Directory.Exists(box.Text) ? box.Text : "";
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        box.Text = dlg.SelectedPath;
                        onChanged(dlg.SelectedPath);
                    }
                }
            }, false);
            parent.Controls.Add(browse);

            box.Leave += delegate { onChanged(box.Text.Trim()); };
            return y + 34;
        }

        private int AddTextRow(Control parent, int y, string caption, string value, Action<string> onChanged)
        {
            Label label = Theme.Label(caption, 12, y + 4);
            label.Width = 240;
            parent.Controls.Add(label);

            TextBox box = Theme.Input(260, y, 400);
            box.Text = value ?? "";
            box.TextChanged += delegate { onChanged(box.Text.Trim()); };
            parent.Controls.Add(box);

            return y + 34;
        }

        private int AddNumberRow(Control parent, int y, string caption, int value, int min, int max, Action<int> onChanged)
        {
            Label label = Theme.Label(caption, 12, y + 4);
            label.Width = 240;
            parent.Controls.Add(label);

            NumericUpDown num = new NumericUpDown();
            num.Minimum = min;
            num.Maximum = max;
            num.Value = Math.Max(min, Math.Min(max, value));
            num.Location = new Point(260, y);
            num.Width = 110;
            num.BackColor = Theme.BgInput;
            num.ForeColor = Theme.Text;
            num.ValueChanged += delegate { onChanged((int)num.Value); };
            parent.Controls.Add(num);

            return y + 34;
        }
    }
}

