using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Wolf.Server
{
    public sealed partial class ServerForm : Form
    {
        private readonly ServerApp app;
        private readonly Dictionary<LogLevel, int> counters = new Dictionary<LogLevel, int>();

        private Label lblState;
        private Label lblServerName;
        private Label lblUptime;
        private Label lblMods;
        private Label lblUrl;
        private Label lblPlayers;
        private Button btnStart;
        private Button btnStop;
        private Button btnRestart;
        private Button btnUpdate;

        private RichTextBox logView;
        private CheckBox chkProblemsOnly;
        private CheckBox chkAutoScroll;
        private Label lblCounters;
        private System.Windows.Forms.Timer clock;
        private TrayIcon tray;

        private DateTime startedUtc = DateTime.MinValue;
        private Label lblPackStale;
        private Button btnRestartStale;
        private bool packStale;
        // Идёт остановка/перезапуск: повторные клики не должны запускать вторую операцию.
        private volatile bool stoppingNow;
        private DateTime lastFreshCheckUtc = DateTime.MinValue;
        private DateTime lastModsScanUtc = DateTime.MinValue;
        private DateTime modsChangedUtc = DateTime.MinValue;

        // Header counter polling: slow on purpose, see PollPlayers().
        private const int PlayersPollSeconds = 20;
        private volatile bool playersPolling;
        private DateTime lastPlayersPollUtc = DateTime.MinValue;
        // Set once the startup checks are done: a server that is merely detected as already
        // running must not produce a "server started" notification on every tool start.
        private bool notifyReady;

        // Сторож telnet-консоли: одна попытка за раз, не чаще настройки, с мягкой паузой
        // после серии неудач. Перезапуск сервера рвёт связь штатно, поэтому это норма.
        private volatile bool telnetBusy;
        private volatile bool telnetEverConnected;
        private DateTime lastTelnetAttemptUtc = DateTime.MinValue;
        private DateTime lastConsoleDownLogUtc = DateTime.MinValue;
        private int telnetFails;

        public ServerForm()
        {
            app = new ServerApp();
            BuildUi();
            tray = new TrayIcon(this, "WolfServer",
                "WolfServer свернулся в трей, сервер продолжает работать. Двойной щелчок по значку вернёт окно.");
            WireEvents();
            LoadState();

            clock = new System.Windows.Forms.Timer();
            clock.Interval = 1000;
            clock.Tick += delegate { Tick(); };
            clock.Start();
        }

        // ---------------- UI ----------------

        private void BuildUi()
        {
            Text = "WolfServer " + WolfInfo.Version + " — управление сервером 7 Days to Die";
            Width = 1180;
            Height = 780;
            MinimumSize = new Size(980, 640);
            StartPosition = FormStartPosition.CenterScreen;
            Theme.Apply(this);

            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 92;
            header.BackColor = Theme.BgPanel;
            Controls.Add(header);

            Label title = Theme.Title("WolfServer", 18, 12);
            title.Font = Theme.Font(16f, FontStyle.Bold);
            header.Controls.Add(title);

            lblServerName = Theme.Dim("сервер не выбран", 20, 42);
            lblServerName.Font = Theme.Font(9.5f);
            header.Controls.Add(lblServerName);

            lblState = new Label();
            lblState.Text = "ОСТАНОВЛЕН";
            lblState.Location = new Point(20, 62);
            lblState.AutoSize = true;
            lblState.ForeColor = Theme.TextDim;
            lblState.Font = Theme.Font(9f, FontStyle.Bold);
            header.Controls.Add(lblState);

            lblUptime = Theme.Dim("", 130, 62);
            header.Controls.Add(lblUptime);

            btnStart = Theme.Button("▶  Запустить", 640, 16, 130, 34, delegate { StartServer(); }, true);
            btnStop = Theme.Button("■  Стоп", 780, 16, 100, 34, delegate { StopServer(); }, false);
            btnRestart = Theme.Button("↻  Перезапуск", 890, 16, 120, 34, delegate { RestartServer(); }, false);
            btnUpdate = Theme.Button("⭳  Обновить сервер", 640, 54, 190, 30, delegate { UpdateServer(); }, false);
            header.Controls.Add(btnStart);
            header.Controls.Add(btnStop);
            header.Controls.Add(btnRestart);
            header.Controls.Add(btnUpdate);

            lblUrl = Theme.Dim("", 842, 60);
            header.Controls.Add(lblUrl);

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Padding = new Point(14, 6);
            Controls.Add(tabs);

            TabPage overviewPage = BuildOverviewTab();
            TabPage logPage = BuildLogTab();
            TabPage chatPage = BuildChatTab();
            TabPage configPage = BuildConfigTab();
            TabPage modsPage = BuildModsTab();
            TabPage playersPage = BuildPlayersTab();
            TabPage inventoryPage = BuildInventoryTab();
            TabPage raidsPage = BuildRaidsTab();
            TabPage statsPage = BuildStatsTab();
            tabs.TabPages.Add(overviewPage);
            tabs.TabPages.Add(logPage);
            tabs.TabPages.Add(chatPage);
            tabs.TabPages.Add(configPage);
            tabs.TabPages.Add(modsPage);
            tabs.TabPages.Add(playersPage);
            tabs.TabPages.Add(inventoryPage);
            tabs.TabPages.Add(raidsPage);
            tabs.TabPages.Add(statsPage);
            tabs.TabPages.Add(BuildHistoryTab());
            tabs.TabPages.Add(BuildBackupsTab());
            tabs.TabPages.Add(BuildScheduleTab());
            tabs.TabPages.Add(BuildSettingsTab());

            // Statistics only change when a player joins or leaves, so without this the tab
            // stays empty until the next poll or a manual refresh.
            tabs.SelectedIndexChanged += delegate
            {
                if (tabs.SelectedTab == statsPage) { RefreshStats(); }
                // Лог рейдов пишется модом на сервере: перечитываем файл при каждом входе,
                // чтобы не держать в памяти давно устаревшую картину.
                if (tabs.SelectedTab == raidsPage) { RefreshRaids(); }
                // Инвентарь тоже готовит серверный мод: при входе на вкладку просим
                // свежий снимок, иначе можно смотреть данные часовой давности.
                if (tabs.SelectedTab == inventoryPage) { RefreshInventory(); }
            };

            Controls.SetChildIndex(tabs, 0);
        }

        private TabPage NewTab(string caption)
        {
            TabPage page = new TabPage(caption);
            page.BackColor = Theme.Bg;
            page.Padding = new Padding(10);
            return page;
        }

        private TabPage BuildOverviewTab()
        {
            TabPage page = NewTab("Обзор");
            Panel card = Theme.Card(12, 12, 720, 286);
            page.Controls.Add(card);

            card.Controls.Add(Theme.Label("Состояние сервера", 16, 12).Bold());
            lblPlayers = Theme.Dim("игроков онлайн: —", 16, 44);
            card.Controls.Add(lblPlayers);
            lblMods = Theme.Dim("модов в раздаче: —", 16, 70);
            card.Controls.Add(lblMods);

            // Набор опубликован позже, чем стартовал игровой сервер: у клиентов уже новые
            // моды, у сервера — старые. Сам сервер не перезапускается, только по кнопке.
            lblPackStale = Theme.Dim("", 16, 96);
            lblPackStale.MaximumSize = new Size(470, 0);
            lblPackStale.ForeColor = Theme.Warn;
            lblPackStale.Visible = false;
            card.Controls.Add(lblPackStale);

            btnRestartStale = Theme.Button("Перезапустить сервер", 500, 94, 204, 30, delegate
            {
                DialogResult answer = MessageBox.Show(this,
                    "Перезапустить игровой сервер сейчас?\n\n" +
                    "Игроки будут отключены, мир сохранится (saveworld + shutdown), " +
                    "затем сервер поднимется уже с новым набором модов.",
                    "WolfServer", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer == DialogResult.Yes) { RestartServer(); }
            }, true);
            btnRestartStale.Visible = false;
            card.Controls.Add(btnRestartStale);

            Label hint = Theme.Dim(
                "Панель управления: запуск/остановка, обновление через steamcmd,\n" +
                "живой лог с подсветкой ERR/EXC/WRN, редактор serverconfig.xml,\n" +
                "раздача модов клиентам (HTTP + P2P), бэкапы сохранений и планировщик.",
                16, 140);
            hint.MaximumSize = new Size(660, 0);
            card.Controls.Add(hint);

            Button btnFolders = Theme.Button("Открыть папку сервера", 16, 196, 200, 32,
                delegate { app.OpenFolder(app.ResolveServerRoot()); }, false);
            card.Controls.Add(btnFolders);

            Button btnLogs = Theme.Button("Открыть логи", 226, 196, 160, 32,
                delegate
                {
                    string root = app.ResolveServerRoot();
                    app.OpenFolder(root == null ? null : Path.Combine(root, "logs"));
                }, false);
            card.Controls.Add(btnLogs);

            Button btnSaves = Theme.Button("Открыть сохранения", 396, 196, 190, 32,
                delegate { app.OpenFolder(app.SavesDir()); }, false);
            card.Controls.Add(btnSaves);

            Panel vk = Theme.Card(752, 12, 380, 250);
            page.Controls.Add(vk);
            vk.Controls.Add(Theme.Label("Сообщество", 16, 12).Bold());
            Label vkHint = Theme.Dim("Ссылка на группу ВК, которую увидят игроки в лаунчере:", 16, 44);
            vkHint.MaximumSize = new Size(340, 0);
            vk.Controls.Add(vkHint);

            TextBox vkBox = Theme.Input(16, 82, 348);
            vkBox.Text = app.Settings.VkUrl;
            vk.Controls.Add(vkBox);

            Button vkSave = Theme.Button("Сохранить", 16, 116, 120, 30,
                delegate
                {
                    app.Settings.VkUrl = vkBox.Text.Trim();
                    app.Save();
                    app.Log.Add("ссылка ВК: " + app.Settings.VkUrl);
                }, true);
            vk.Controls.Add(vkSave);

            Button vkOpen = Theme.Button("Открыть группу", 146, 116, 140, 30,
                delegate { app.OpenUrl(vkBox.Text.Trim()); }, false);
            vk.Controls.Add(vkOpen);

            return page;
        }

        private TabPage BuildLogTab()
        {
            TabPage page = NewTab("Живой лог");

            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 40;
            bar.BackColor = Theme.Bg;
            page.Controls.Add(bar);

            chkProblemsOnly = new CheckBox();
            chkProblemsOnly.Text = "только проблемы (ERR / EXC / WRN)";
            chkProblemsOnly.Location = new Point(4, 8);
            chkProblemsOnly.AutoSize = true;
            chkProblemsOnly.ForeColor = Theme.Text;
            chkProblemsOnly.BackColor = Color.Transparent;
            chkProblemsOnly.Checked = app.Settings.WatchOnlyProblems;
            chkProblemsOnly.CheckedChanged += delegate
            {
                app.Settings.WatchOnlyProblems = chkProblemsOnly.Checked;
                app.Save();
            };
            bar.Controls.Add(chkProblemsOnly);

            chkAutoScroll = new CheckBox();
            chkAutoScroll.Text = "прокручивать";
            chkAutoScroll.Location = new Point(300, 8);
            chkAutoScroll.AutoSize = true;
            chkAutoScroll.Checked = true;
            chkAutoScroll.ForeColor = Theme.Text;
            chkAutoScroll.BackColor = Color.Transparent;
            bar.Controls.Add(chkAutoScroll);

            lblCounters = Theme.Dim("", 420, 10);
            bar.Controls.Add(lblCounters);

            Button clear = Theme.Button("Очистить", 900, 5, 100, 28, delegate
            {
                logView.Clear();
                counters.Clear();
                UpdateCounters();
            }, false);
            clear.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bar.Controls.Add(clear);

            logView = new RichTextBox();
            logView.Dock = DockStyle.Fill;
            logView.BackColor = Color.FromArgb(18, 20, 24);
            logView.ForeColor = Theme.Text;
            logView.BorderStyle = BorderStyle.None;
            logView.Font = new Font("Consolas", 9f);
            logView.WordWrap = false;
            logView.ReadOnly = true;
            logView.DetectUrls = false;
            logView.HideSelection = false;
            page.Controls.Add(logView);
            page.Controls.SetChildIndex(logView, 0);

            return page;
        }

        // ---------------- behaviour ----------------

        private void WireEvents()
        {
            app.Log.OnLine += delegate(string line) { AppendLog(line, LogLevel.Info); };
            app.Controller.OnLogLine += delegate(string line) { AppendLog(line, ServerController.LevelOf(line)); };
            app.Controller.OnLogLine += delegate(string line) { ParseChatLine(line); };
            app.Controller.OnStateChanged += delegate(bool running)
            {
                this.Ui(delegate
                {
                    if (running) { startedUtc = DateTime.UtcNow; }
                    UpdateState();
                });
                if (notifyReady && app.Settings.NotifyServer)
                {
                    app.Notifier.SendAsync(running
                        ? "Сервер 7 Days to Die запущен (" + (app.Settings.PackName ?? "Wolf Server") + ")"
                        : "Сервер 7 Days to Die остановлен", app.Log.Add);
                }
            };
            app.Scheduler.OnFire += delegate(ScheduleTask task) { HandleScheduledTask(task); };
            app.Scheduler.OnWarning += delegate(ScheduleTask task, int minutes)
            {
                if (app.Telnet.Connected)
                {
                    try { app.Telnet.Send(Telnet.SayCommand("[Сервер] перезапуск через " + minutes + " мин.")); } catch { }
                }
            };
            app.Tracker.OnEvent += delegate(string s) { app.Log.Add("[p2p] " + s); };

            // Сервер перезапустился и начал новый файл лога: сообщаем в ленту чата, чтобы
            // было видно, почему история «переехала», и подтягиваем хвост нового файла.
            app.Controller.OnLogChanged += delegate(string file)
            {
                string name = Path.GetFileName(file);
                app.Log.Add("[лог] новый файл журнала сервера: " + name);
                this.Ui(delegate
                {
                    AppendChat(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                        "-non-player-", "Global",
                        "— сервер перезапустился, читаю новый лог " + name + " —");
                    chatLoaded = false;
                    LoadRecentChat();
                });
            };

            // Player statistics are updated by PollPlayers; these events only persist and
            // notify, so the tracker stays responsive even when the Notifier is offline.
            app.Stats.OnJoin += delegate(PlayerSession session)
            {
                app.SaveStats();
                if (app.Settings.NotifyJoin)
                {
                    app.Notifier.SendAsync("Игрок " + session.name + " зашёл на сервер" +
                        (string.IsNullOrEmpty(session.steamId) ? "" : " (" + session.steamId + ")"), app.Log.Add);
                }
            };
            app.Stats.OnLeave += delegate(PlayerSession session)
            {
                app.SaveStats();
                if (app.Settings.NotifyJoin)
                {
                    app.Notifier.SendAsync("Игрок " + session.name + " вышел с сервера" +
                        " (" + WolfInfo.HumanDuration(session.Duration(DateTime.UtcNow)) + ")", app.Log.Add);
                }
            };
        }

        private void LoadState()
        {
            string root = app.ResolveServerRoot();
            string cfg = app.ResolveConfigFile();
            if (cfg != null) { app.Config.Load(cfg); }

            app.Controller.Root = root;
            app.Controller.AutoRestart = app.Settings.AutoRestart;
            ApplyTelnetSettings();

            lblServerName.Text = root ?? "сервер не найден — укажите путь во вкладке «Настройки»";
            // Мгновенно показываем локальный адрес, внешний IP подтягиваем в фоне:
            // раньше этот вызов шёл синхронно и после перезагрузки окно стояло,
            // пока DNS/интернет не начнут отвечать.
            lblUrl.Text = "клиентам: " + app.DisplayBaseUrl();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string url;
                try { url = app.PublicBaseUrl(); } catch { return; }
                this.Ui(delegate { lblUrl.Text = "клиентам: " + url; });
            });
            app.Scheduler.Start();
            RefreshSchedule();   // задачи уже загружены из schedule.json — показать их сразу

            foreach (string line in app.Log.Snapshot()) { AppendLog(line, LogLevel.Info); }

            LoadConfigIntoUi();
            UpdateState();
            RefreshModsSummary();

            // Attach to a server that is already up (start_server1.bat owns it).
            if (app.Controller.DetectExternal())
            {
                UpdateState();
                app.Controller.FollowNewestLog(true);
                app.Log.Add("[лог] читаю журнал сервера: " + (app.Controller.CurrentLog == null
                    ? "файл ещё не найден"
                    : Path.GetFileName(app.Controller.CurrentLog)));
                ConnectTelnet();
            }

            LoadRecentChat();   // вкладка «Чат»: показать последние сообщения из лога сервера

            // Distribution comes up with the tool so players can already switch while ModSync
            // keeps serving 8765: the two listen on different ports and do not interfere.
            StartModsWatcher();
            if (app.Settings.ServeOnStart) { PublishPack(true); }
            notifyReady = true;
        }

        // Telnet settings follow serverconfig.xml: the tool only has to know where to knock.
        private void ApplyTelnetSettings()
        {
            if (!app.Config.Loaded) { return; }
            if (!app.Config.GetBool("TelnetEnabled", false)) { return; }
            app.Settings.TelnetPort = app.Config.GetInt("TelnetPort", app.Settings.TelnetPort);
            app.Settings.TelnetPassword = app.Config.Get("TelnetPassword", app.Settings.TelnetPassword);
            app.Save();
        }

        private void UpdateState()
        {
            bool running = app.Controller.Running;
            lblState.Text = running ? "РАБОТАЕТ" : "ОСТАНОВЛЕН";
            lblState.ForeColor = running ? Theme.Ok : Theme.TextDim;
            // Пока идёт остановка, кнопки неактивны: повторный клик запустил бы вторую
            // операцию поверх незавершённой.
            btnStart.Enabled = !running && !stoppingNow;
            btnStop.Enabled = running && !stoppingNow;
            btnRestart.Enabled = running && !stoppingNow;
            UpdateUptime();
        }

        private void UpdateUptime()
        {
            if (app.Controller.Running && startedUtc != DateTime.MinValue)
            {
                TimeSpan up = DateTime.UtcNow - startedUtc;
                lblUptime.Text = string.Format(CultureInfo.InvariantCulture, "uptime {0:hh\\:mm\\:ss}   сессий: {1}   перезапусков: {2}",
                    up, app.Controller.Sessions, app.Controller.Restarts);
            }
            else
            {
                lblUptime.Text = "";
            }
        }

        private void Tick()
        {
            UpdateUptime();
            PollPlayers();
            CheckPackFreshness();

            // The server normally runs under start_server1.bat, not under this tool.
            if (!app.Controller.OwnsProcess)
            {
                if (app.Controller.DetectExternal())
                {
                    UpdateState();
                }
            }

            // Инструмент мог стартовать ровно в момент перезапуска сервера: DetectExternal()
            // в LoadState вернул false, хвост лога не запустился, и вкладки «Чат» и «Живой
            // лог» оставались пустыми, хотя сервер потом поднялся. Как только сервер виден,
            // подхватываем его журнал.
            if (app.Controller.Running && app.Controller.CurrentLog == null)
            {
                app.Controller.FollowNewestLog(true);
                if (app.Controller.CurrentLog != null)
                {
                    app.Log.Add("[лог] читаю журнал сервера: " + Path.GetFileName(app.Controller.CurrentLog));
                    chatLoaded = true;      // лента старше, чем первый вход на вкладку
                    LoadRecentChat();
                }
            }

            // Связь с консолью держим сами: сервер перезапускается по расписанию и после
            // падений, а каждый перезапуск рвёт telnet. Без этого вкладка «Чат» умирала.
            MaintainConsole();
            UpdateChatStatus();
        }

        // "listplayers" is echoed into the server log by the game itself, so polling it every
        // second would bury the log; the header counter only needs a few words per minute.
        private void PollPlayers()
        {
            bool online = app.Controller.Running || app.Controller.ExternalPid != 0;
            if (!online || !app.Telnet.Connected)
            {
                if (!online) { lblPlayers.Text = "игроков онлайн: —"; }
                return;
            }
            if (playersPolling || (DateTime.UtcNow - lastPlayersPollUtc).TotalSeconds < PlayersPollSeconds) { return; }
            playersPolling = true;
            lastPlayersPollUtc = DateTime.UtcNow;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string reply = app.Telnet.Command("listplayers", 1200);
                    List<GamePlayer> players = Telnet.ParsePlayers(reply);
                    app.LastPlayers = players;
                    bool statsChanged = app.Stats.Observe(players, DateTime.UtcNow);
                    if (statsChanged) { app.SaveStats(); }
                    this.Ui(delegate
                    {
                        lblPlayers.Text = "игроков онлайн: " + players.Count +
                            (players.Count > 0
                                ? "  —  " + string.Join(", ", players.ConvertAll(p => p.Name).ToArray())
                                : "");
                        if (playersList != null && playersList.Items.Count != players.Count) { RefreshPlayers(); }
                        if (statsChanged) { RefreshStats(); }
                    });
                }
                catch { }
                finally { playersPolling = false; }
            });
        }

        private void AppendLog(string line, LogLevel level)
        {
            if (logView == null) { return; }
            if (chkProblemsOnly != null && chkProblemsOnly.Checked &&
                level != LogLevel.Error && level != LogLevel.Exception && level != LogLevel.Warning)
            {
                return;
            }

            int current;
            counters.TryGetValue(level, out current);
            counters[level] = current + 1;
            UpdateCounters();

            Color color = Theme.Text;
            switch (level)
            {
                case LogLevel.Error: color = Theme.Err; break;
                case LogLevel.Exception: color = Color.FromArgb(196, 96, 96); break;
                case LogLevel.Warning: color = Theme.Warn; break;
                case LogLevel.Noise: color = Theme.TextDim; break;
                default: color = Theme.Text; break;
            }

            this.Ui(delegate
            {
                if (logView.IsDisposed) { return; }
                bool atBottom = logView.SelectionStart >= logView.TextLength - 2;
                logView.SelectionStart = logView.TextLength;
                logView.SelectionLength = 0;
                logView.SelectionColor = color;
                logView.AppendText(line + Environment.NewLine);
                if (logView.TextLength > 900000)
                {
                    logView.Select(0, 200000);
                    logView.SelectedText = "";
                }
                if (chkAutoScroll != null && chkAutoScroll.Checked && atBottom)
                {
                    logView.SelectionStart = logView.TextLength;
                    logView.ScrollToCaret();
                }
            });
        }

        private void UpdateCounters()
        {
            int err, exc, warn;
            counters.TryGetValue(LogLevel.Error, out err);
            counters.TryGetValue(LogLevel.Exception, out exc);
            counters.TryGetValue(LogLevel.Warning, out warn);
            if (lblCounters != null && !lblCounters.IsDisposed)
            {
                lblCounters.Text = string.Format(CultureInfo.InvariantCulture, "ERR {0}   EXC {1}   WRN {2}", err, exc, warn);
            }
        }

        // ---------------- actions ----------------

        private void StartServer()
        {
            string root = app.ResolveServerRoot();
            if (root == null)
            {
                MessageBox.Show(this, "Не найден выделенный сервер. Укажите папку во вкладке «Настройки».",
                    "WolfServer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            app.Controller.Root = root;
            app.Controller.AutoRestart = app.Settings.AutoRestart;
            if (app.Controller.DetectExternal() && app.Controller.ExternalPid != 0)
            {
                MessageBox.Show(this,
                    "Сервер уже запущен (PID " + app.Controller.ExternalPid + ").\n\n" +
                    "Он работает под внешним циклом start_server1.bat — второй экземпляр не нужен. " +
                    "Для перезапуска используйте кнопку «Перезапуск».",
                    "WolfServer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                UpdateState();
                return;
            }
            string cfg = app.ResolveConfigFile();
            if (cfg == null)
            {
                MessageBox.Show(this, "Не найден serverconfig.xml. Проверьте путь во вкладке «Настройки».",
                    "WolfServer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            app.Config.Load(cfg);
            List<string> issues = app.Config.Validate();
            if (issues.Count > 0)
            {
                string text = "Проблемы в конфигурации:\n\n- " + string.Join("\n- ", issues.ToArray()) + "\n\nЗапускать всё равно?";
                if (MessageBox.Show(this, text, "Проверка конфига", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    return;
                }
            }

            startedUtc = DateTime.UtcNow;
            app.Controller.LaunchScript = app.Settings.LaunchScript;
            app.Controller.Start(cfg, app.Config.GetBool("TerminalWindowEnabled", false), app.Settings.LaunchViaScript);
            ConnectTelnet();
            UpdateState();
        }

        private void StopServer()
        {
            if (stoppingNow) { return; }
            stoppingNow = true;
            btnStop.Enabled = false;
            btnRestart.Enabled = false;
            app.Log.Add("остановка сервера: saveworld, затем штатный shutdown");
            ThreadPool.QueueUserWorkItem(delegate
            {
                // Полная остановка: цикл start_server1.bat снимаем, сервер закрываем штатно.
                app.Controller.GracefulStop(app.Telnet, false);
                this.Ui(delegate
                {
                    stoppingNow = false;
                    UpdateState();
                });
            });
        }

        // Раз в 5 секунд сверяем время публикации набора со временем старта игрового сервера.
        // Сигнал — самый новый файл в папке модов: перепубликация того же набора времени не
        // меняет, а вот правка/докачка мода меняет. Ничего не перезапускаем сами.
        private void CheckPackFreshness()
        {
            if ((DateTime.UtcNow - lastFreshCheckUtc).TotalSeconds < 5.0) { return; }
            lastFreshCheckUtc = DateTime.UtcNow;

            bool stale = false;
            DateTime? serverStart;
            DateTime modsChanged;
            try
            {
                serverStart = ServerStartUtc();
                modsChanged = ModsChangedUtc();
                if (serverStart.HasValue && modsChanged > DateTime.MinValue &&
                    modsChanged > serverStart.Value.AddSeconds(30))
                {
                    stale = true;
                }
            }
            catch { return; }

            if (stale == packStale) { return; }
            packStale = stale;

            this.Ui(delegate
            {
                if (lblPackStale == null || lblPackStale.IsDisposed) { return; }
                lblPackStale.Visible = stale;
                btnRestartStale.Visible = stale;
                lblPackStale.Text = stale
                    ? "Моды менялись после старта сервера: у игроков новый набор, у сервера — старый. Нужен перезапуск."
                    : "";
            });

            if (stale)
            {
                app.Log.Add("внимание: моды менялись после старта сервера (моды " +
                            modsChanged.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) +
                            ", старт " + (serverStart.HasValue ? serverStart.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "?") +
                            ") — игрокам нужен перезапуск сервера, сам не перезапускаю");
            }
        }

        // Самый новый файл в папке модов; сканируем не чаще раза в минуту.
        private DateTime ModsChangedUtc()
        {
            if ((DateTime.UtcNow - lastModsScanUtc).TotalSeconds < 60.0) { return modsChangedUtc; }
            lastModsScanUtc = DateTime.UtcNow;

            DateTime newest = DateTime.MinValue;
            try
            {
                string dir = app.ModsSourceDir();
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            DateTime t = File.GetLastWriteTimeUtc(file);
                            if (t > newest) { newest = t; }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            modsChangedUtc = newest;
            return newest;
        }

        private DateTime? ServerStartUtc()
        {
            try
            {
                if (app.Controller.ExternalPid != 0)
                {
                    using (Process p = Process.GetProcessById(app.Controller.ExternalPid))
                    {
                        return p.StartTime.ToUniversalTime();
                    }
                }
            }
            catch { }
            if (startedUtc == DateTime.MinValue) { return null; }
            return startedUtc;
        }

        private DateTime PackPublishedUtc()
        {
            string file = Path.Combine(app.PublishFolder(), "manifest.json");
            try { return File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        private void RestartServer()
        {
            if (app.Settings.BackupBeforeRestart)
            {
                string saves = app.SavesDir();
                app.Backups.BackupWorld(saves, app.WorldName(), "перед перезапуском", app.Log.Add);
            }
            // Цикл перезапуска жив в двух случаях: сервер слушает внешний start_server1.bat
            // (ExternalPid) или WolfServer сам запустил батник (cmd-обёртка). Тогда после
            // штатного shutdown сервер поднимется сам, и StartServer звать нельзя — иначе
            // получится второй цикл.
            bool viaScript = app.Controller.ExternalPid != 0 || app.Controller.StartedViaScript;
            if (stoppingNow) { return; }
            stoppingNow = true;
            app.Log.Add(viaScript
                ? "перезапуск сервера: saveworld + shutdown, поднимет цикл start_server1.bat"
                : "перезапуск сервера: saveworld + shutdown");
            btnRestart.Enabled = false;
            btnStop.Enabled = false;
            // Остановка ждёт штатного выхода сервера (до ShutdownTimeoutMs), поэтому её
            // нельзя держать в UI-потоке: окно замёрзло бы на всю минуту.
            ThreadPool.QueueUserWorkItem(delegate
            {
                // keepRestartLoop=true: цикл start_server1.bat не трогаем, он и поднимет сервер.
                app.Controller.GracefulStop(app.Telnet, true);
                Thread.Sleep(15000);
                this.Ui(delegate
                {
                    stoppingNow = false;
                    if (viaScript)
                    {
                        app.Controller.DetectExternal();
                        UpdateState();
                        ConnectTelnet();
                    }
                    else
                    {
                        StartServer();
                    }
                });
            });
        }

        private void UpdateServer()
        {
            string steamcmd = app.ResolveSteamCmd();
            string root = app.ResolveServerRoot();
            if (steamcmd == null || root == null)
            {
                MessageBox.Show(this, "Нужны steamcmd.exe и папка сервера. Укажите их во вкладке «Настройки».",
                    "WolfServer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (app.Controller.Running)
            {
                MessageBox.Show(this, "Сначала остановите сервер — Steam не обновит файлы запущенного процесса.",
                    "WolfServer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            app.Log.Add("steamcmd: app_update 294420 validate");
            Proc p = new Proc();
            p.OnOutput += delegate(string line) { app.Log.Add("[steam] " + line); };
            p.OnExit += delegate(int code)
            {
                app.Log.Add("steamcmd завершён, код " + code);
                this.Ui(RefreshModsSummary);
            };
            SteamCmd.Update(p, steamcmd, GamePaths.AppIdServer, root, true, "anonymous");
        }

        private void ConnectTelnet() { EnsureTelnet("старт"); }

        // ---------------- консоль сервера (telnet) ----------------

        // Консоль отваливается при каждом перезапуске сервера и при любом обрыве telnet-сессии,
        // а раньше подключение делалось один раз при старте тула: вкладка «Чат» до конца
        // работы показывала «не подключена» и не отправляла сообщения в игру.
        private void MaintainConsole()
        {
            if (!app.Settings.TelnetAutoReconnect) { return; }
            if (!app.Config.Loaded) { return; }
            if (!app.Config.GetBool("TelnetEnabled", false)) { return; }
            if (!app.Controller.Running && app.Controller.ExternalPid == 0) { return; }
            if (app.Telnet.Connected) { telnetFails = 0; return; }

            int pause = Math.Max(2, app.Settings.TelnetReconnectSeconds);
            if (telnetFails >= 3) { pause *= 2; }         // сервер ещё поднимается — не долбим каждые 2 с
            if ((DateTime.UtcNow - lastTelnetAttemptUtc).TotalSeconds < pause) { return; }
            EnsureTelnet("сторож");
        }

        // Одна попытка подключения за раз: сторож, кнопка «Переподключить» и запуск сервера
        // не должны открывать несколько telnet-сессий к одному серверу.
        private void EnsureTelnet(string reason)
        {
            if (telnetBusy) { return; }
            telnetBusy = true;
            lastTelnetAttemptUtc = DateTime.UtcNow;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (app.Telnet.Connected) { return; }
                    if (!app.Config.Loaded)
                    {
                        NoteConsoleDown("serverconfig.xml ещё не прочитан");
                        return;
                    }
                    if (!app.Config.GetBool("TelnetEnabled", false))
                    {
                        NoteConsoleDown("TelnetEnabled=false в serverconfig.xml");
                        return;
                    }

                    int port = app.Config.GetInt("TelnetPort",
                        app.Settings.TelnetPort > 0 ? app.Settings.TelnetPort : 8081);
                    string pass = app.Config.Get("TelnetPassword", app.Settings.TelnetPassword);
                    app.Settings.TelnetPort = port;
                    app.Settings.TelnetPassword = pass;

                    string banner;
                    if (app.Telnet.Connect("127.0.0.1", port, pass, 2500, out banner))
                    {
                        telnetFails = 0;
                        app.Log.Add(telnetEverConnected
                            ? "консоль сервера переподключена: порт " + port + " (" + reason + ")"
                            : "консоль сервера подключена: порт " + port + " (" + reason + ")");
                        telnetEverConnected = true;
                        // Консоль зеркалит весь поток лога на каждое соединение: без этого
                        // каждая наша команда возвращалась бы в журнал тула бесконечно.
                        try { app.Telnet.Command("loglevel INF false", 800); } catch { }
                        try { app.Telnet.Command("loglevel WRN false", 800); } catch { }
                        try { app.Telnet.Command("loglevel EXC false", 800); } catch { }
                        this.Ui(delegate
                        {
                            RefreshPlayersTab();
                            UpdateChatStatus();
                        });
                        return;
                    }
                    NoteConsoleDown("telnet не отвечает на 127.0.0.1:" + port +
                                    (string.IsNullOrEmpty(banner) ? "" : " — " + ShortText(banner)));
                }
                catch (Exception ex)
                {
                    NoteConsoleDown(ShortText(ex.Message));
                }
                finally { telnetBusy = false; }
            });
        }

        // Обрыв — штатная ситуация, поэтому в журнал пишем не чаще раза в минуту: иначе
        // сторож забивает историю попытками каждые несколько секунд, пока сервер грузится.
        private void NoteConsoleDown(string message)
        {
            telnetFails++;
            if ((DateTime.UtcNow - lastConsoleDownLogUtc).TotalSeconds < 60) { return; }
            lastConsoleDownLogUtc = DateTime.UtcNow;
            app.Log.Add("консоль сервера не подключена: " + message);
        }

        private static string ShortText(string value)
        {
            if (string.IsNullOrEmpty(value)) { return ""; }
            string s = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= 140 ? s : s.Substring(0, 140) + "…";
        }

        private void HandleScheduledTask(ScheduleTask task)
        {
            switch (task.Kind)
            {
                case ScheduleKind.Backup:
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        app.Backups.BackupWorld(app.SavesDir(), app.WorldName(), "по расписанию", app.Log.Add);
                    });
                    break;
                case ScheduleKind.Broadcast:
                    try { if (app.Telnet.Connected) { app.Telnet.Send(Telnet.SayCommand(task.Text)); } } catch { }
                    break;
                case ScheduleKind.Restart:
                    if (app.Controller.Running) { RestartServer(); }
                    else { StartServer(); }
                    break;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (app.Controller.Running)
            {
                DialogResult r = MessageBox.Show(this,
                    "Сервер запущен. Остановить его при выходе?", "WolfServer",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) { e.Cancel = true; return; }
                if (r == DialogResult.No)
                {
                    // leave the game server running, only detach the tool
                    StopModsWatcher();
                    app.Save();
                    app.SaveStats();
                    clock.Stop();
                    return;
                }
                // Закрытие тула не должно обрывать сервер через taskkill /T: сначала штатный
                // shutdown, иначе не выполняются OnApplicationQuit, сохранения и моды.
                // Таймаут короче обычного: окно закрывается, ждать полторы минуты нельзя.
                app.Controller.GracefulStop(app.Telnet, false, 45000);
            }
            StopModsWatcher();
            clock.Stop();
            app.SaveStats();
            app.Dispose();
            base.OnFormClosing(e);
        }
    }
}



