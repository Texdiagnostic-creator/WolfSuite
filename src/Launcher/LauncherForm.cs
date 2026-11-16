using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Wolf.Launcher
{
    public sealed partial class LauncherForm : Form
    {
        private readonly LauncherSettings settings;
        private readonly string settingsFile;
        private readonly Log log;
        private readonly ChunkStore store;
        private readonly LocalHashCache hashCache;
        private ModSync sync;
        private PeerNode peer;
        private System.Windows.Forms.Timer autoTimer;
        private volatile bool syncing;
        private TrayIcon tray;
        private volatile bool playWhenReady;   // «Играть» нажата: запуск после успешной сверки набора
        private string pendingExe;             // скачанный билд ждёт закрытия лаунчера
        private string pendingStaging;
        // Последние расхождения с сервером: кнопка «Скопировать диагностику» отдаёт их игроку
        // текстом, чтобы он мог прислать отчёт в чат вместо скриншотов.
        private List<DiffMismatch> lastMismatches = new List<DiffMismatch>();
        private string lastServerHash = "";

        // Header banner: compiled into the exe as a resource (see /resource in build.bat), so
        // the launcher stays a single file with nothing extra to ship to players.
        private const int HeroHeight = 336;
        private const int BannerHeight = 199;

        private static Image LoadResource(string name)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (s == null) { return null; }
                    using (MemoryStream ms = new MemoryStream())
                    {
                        byte[] buffer = new byte[16384];
                        int n;
                        while ((n = s.Read(buffer, 0, buffer.Length)) > 0) { ms.Write(buffer, 0, n); }
                        ms.Position = 0;
                        using (Image raw = Image.FromStream(ms)) { return new Bitmap(raw); }
                    }
                }
            }
            catch { return null; }
        }

        private Label lblServerName;
        private Label lblServerNote;
        private Label lblState;
        private Label lblUpdateNote;
        private System.Windows.Forms.Timer pulseTimer;
        private int pulsePhase;
        private Label lblModsCount;
        private Label lblProgress;
        private ProgressBar progress;
        private Button btnSync;
        private Button btnPlay;
        private Button btnCancel;
        private TextBox txtUrl;
        private ListView modList;
        private RichTextBox logView;
        private RichTextBox newsView;

        public LauncherForm()
        {
            string dir = GamePaths.SettingsDir("WolfLauncher");
            settingsFile = Path.Combine(dir, "settings.json");
            settings = LauncherSettings.Load(settingsFile);
            log = new Log(dir, "launcher.log", 1500);
            store = new ChunkStore(Path.Combine(dir, "chunks"));
            hashCache = new LocalHashCache(Path.Combine(dir, "hashes.json"));

            if (string.IsNullOrEmpty(settings.GameRoot))
            {
                settings.GameRoot = GamePaths.ClientRoot(null) ?? "";
            }
            if (string.IsNullOrEmpty(settings.ModsDir) && !string.IsNullOrEmpty(settings.GameRoot))
            {
                settings.ModsDir = GamePaths.ClientModsDir(settings.GameRoot);
            }
            if (string.IsNullOrEmpty(settings.SteamId)) { settings.SteamId = ModSync.DetectSteamId(); }
            if (string.IsNullOrEmpty(settings.VkUrl)) { settings.VkUrl = "https://vk.ru/7daystodieworld"; }

            BuildUi();
            tray = new TrayIcon(this, "WolfLauncher",
                "Лаунчер свернулся в трей и продолжает работать. Двойной щелчок по значку вернёт окно.");
            LoadState();
            StartPeer();
            CheckSelfUpdate();
        }

        // Keeps every player on the build the server publishes. The new build is downloaded next
        // to the launcher and swapped in when the launcher closes; the current session keeps
        // working. Nothing is spawned to do the swap - see LauncherUpdate.ApplyStaged.
        private void CheckSelfUpdate()
        {
            string exe = LauncherUpdate.CurrentExePath();
            LauncherUpdate.CleanStaging(exe);
            if (string.IsNullOrEmpty(settings.ServerUrl)) { return; }

            ThreadPool.QueueUserWorkItem(delegate
            {
                LauncherUpdate.Info info = LauncherUpdate.Fetch(settings.ServerUrl, 15000);
                if (info == null || !LauncherUpdate.IsNewer(info, exe)) { return; }
                log.Add("на сервере новая версия лаунчера (" +
                        (string.IsNullOrEmpty(info.version) ? info.sha.Substring(0, 8) : info.version) +
                        "), скачиваю");
                string staging = LauncherUpdate.Download(info, exe, log.Add, 60000);
                if (staging == null) { return; }
                pendingExe = exe;
                pendingStaging = staging;
                BeginInvoke(new Action(delegate
                {
                    SetState("лаунчер обновится при закрытии", Theme.Warn);
                    ShowUpdateNote();
                }));
            });
        }

        // Крупная пульсирующая строка про обновление лаунчера: без неё игрок не понимает,
        // что скачанная версия применится только после перезапуска.
        private void ShowUpdateNote()
        {
            if (lblUpdateNote == null) { return; }
            lblUpdateNote.Visible = true;
            if (pulseTimer != null) { return; }
            pulsePhase = 0;
            pulseTimer = new System.Windows.Forms.Timer();
            pulseTimer.Interval = 70;
            pulseTimer.Tick += delegate
            {
                pulsePhase = (pulsePhase + 1) % 60;
                double k = (Math.Sin(pulsePhase * Math.PI / 30.0) + 1.0) / 2.0;   // 0..1, период ~2 с
                if (lblUpdateNote != null && lblUpdateNote.Visible)
                {
                    lblUpdateNote.ForeColor = Color.FromArgb(
                        255,
                        (int)(110 + 145 * k),
                        (int)(40 + 100 * k));
                }
            };
            pulseTimer.Start();
        }

        private void BuildUi()
        {
            Text = "WolfLauncher " + WolfInfo.Version + " — 7 Days to Die";
            Width = 1000;
            Height = 860;
            MinimumSize = new Size(900, 760);
            StartPosition = FormStartPosition.CenterScreen;
            Theme.Apply(this);

            Panel hero = new Panel();
            hero.Dock = DockStyle.Top;
            hero.Height = HeroHeight;
            hero.BackColor = Theme.BgPanel;
            Controls.Add(hero);

            PictureBox banner = new PictureBox();
            banner.Location = new Point(0, 0);
            banner.Size = new Size(1000, BannerHeight);
            banner.SizeMode = PictureBoxSizeMode.Zoom;
            banner.BackColor = Theme.BgPanel;
            banner.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            banner.Image = LoadResource("WolfLauncher.Banner.jpg");
            hero.Controls.Add(banner);

            lblServerName = new Label();
            lblServerName.Text = "сервер не выбран";
            lblServerName.Location = new Point(22, 206);
            lblServerName.AutoSize = true;
            lblServerName.Font = Theme.Font(12f, FontStyle.Bold);
            lblServerName.ForeColor = Theme.Text;
            hero.Controls.Add(lblServerName);

            lblServerNote = Theme.Dim("", 22, 232);
            lblServerNote.MaximumSize = new Size(500, 0);
            hero.Controls.Add(lblServerNote);

            lblState = new Label();
            lblState.Text = "готов к работе";
            lblState.Location = new Point(22, 268);
            lblState.AutoSize = true;
            lblState.ForeColor = Theme.TextDim;
            hero.Controls.Add(lblState);

            // Просьба перезапустить лаунчер терялась в мелком сером тексте статуса, и игроки
            // играли со старым лаунчером. Отдельная строка крупным шрифтом, пульсирует красным.
            lblUpdateNote = new Label();
            lblUpdateNote.Text = "⟳  Лаунчер обновится при закрытии — закройте и откройте его заново";
            lblUpdateNote.Location = new Point(22, 296);
            lblUpdateNote.AutoSize = true;
            lblUpdateNote.Font = Theme.Font(11f, FontStyle.Bold);
            lblUpdateNote.ForeColor = Color.FromArgb(255, 90, 78);
            lblUpdateNote.Visible = false;
            hero.Controls.Add(lblUpdateNote);

            // "Играть" — кнопка с фоном из капель крови: текстура встроена в exe рядом с баннером.
            // Красное свечение вокруг — как синее у кнопки ВК (8 px по краям отведено под ореол).
            ImageButton play = new ImageButton();
            play.Text = "▶  Играть";
            play.Font = Theme.Font(12f, FontStyle.Bold);
            play.Location = new Point(692, 196);
            play.Size = new Size(286, 62);
            play.Radius = 8;
            play.GlowInset = 8;
            play.GlowColor = Color.FromArgb(205, 45, 40);
            play.PanelColor = Theme.BgPanel;
            play.BorderColor = Color.FromArgb(255, 100, 84);
            play.FallbackFill = Theme.AccentDark;
            play.Face = LoadResource("WolfLauncher.Blood.png");
            play.Click += delegate { Play(); };
            hero.Controls.Add(play);
            btnPlay = play;

            // Community link next to "Играть": it was in the old header and got lost when the
            // header was rebuilt. Address comes from the server (/server.json, поле vk).
            // NeonButton рисует синее свечение вокруг; запас 10 px по краям — под ореол.
            NeonButton btnVk = new NeonButton("Группа ВК", Color.FromArgb(64, 160, 255), Theme.BgPanel);
            btnVk.Location = new Point(540, 194);
            btnVk.Size = new Size(148, 66);
            btnVk.Click += delegate { OpenVk(); };
            hero.Controls.Add(btnVk);

            // Одна кнопка проверки: сверка всегда по содержимому (через кэш хешей она быстрая).
            // Shift+клик — пересчитать хеши заново, не доверяя кэшу; это нужно, когда файл
            // подменили, сохранив размер и дату изменения.
            btnSync = Theme.Button("⟳  Проверить моды", 620, 262, 132, 30,
                delegate
                {
                    bool force = (ModifierKeys & Keys.Shift) == Keys.Shift;
                    CheckMods(true, true, force);
                }, false);
            hero.Controls.Add(btnSync);

            btnCancel = Theme.Button("Отмена", 762, 262, 96, 30, delegate { CancelSync(); }, false);
            btnCancel.Enabled = false;
            hero.Controls.Add(btnCancel);

            // Отчёт о расхождениях в буфере обмена: игрок вставляет его в чат, и видно,
            // какой именно файл отличается и чем. Заменяет переписку скриншотами.
            // Стоит в одном ряду с проверкой и отменой: ниже панель уже кончается и низ
            // кнопки срезался границей hero.
            Button btnDiag = Theme.Button("Диагностика", 868, 262, 112, 30,
                delegate { CopyDiagnostics(); }, false);
            hero.Controls.Add(btnDiag);

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Padding = new Point(14, 6);
            Controls.Add(tabs);
            Controls.SetChildIndex(tabs, 0);

            tabs.TabPages.Add(BuildMainTab());
            tabs.TabPages.Add(BuildNewsTab());
            tabs.TabPages.Add(BuildLogTab());
            tabs.TabPages.Add(BuildSettingsTab());

            progress = new ProgressBar();
            progress.Dock = DockStyle.Bottom;
            progress.Height = 14;
            // Hidden until something is actually being downloaded: the stock progress bar
            // keeps its light system colours and shows as a white strip in the dark theme.
            progress.Visible = false;
            Controls.Add(progress);

            lblProgress = Theme.Dim("", 0, 0);
            lblProgress.Dock = DockStyle.Bottom;
            lblProgress.Height = 20;
            lblProgress.Padding = new Padding(12, 2, 0, 0);
            Controls.Add(lblProgress);
        }

        private TabPage NewTab(string caption)
        {
            TabPage page = new TabPage(caption);
            page.BackColor = Theme.Bg;
            page.Padding = new Padding(10);
            return page;
        }

        private TabPage BuildMainTab()
        {
            TabPage page = NewTab("Моды");

            // Everything is docked: a fixed strip for the address card, the list fills the rest.
            // Absolute sizes used to overflow the page once the header grew (banner).
            modList = new ListView();
            modList.View = View.Details;
            modList.FullRowSelect = true;
            modList.Dock = DockStyle.Fill;
            modList.Columns.Add("Мод", 460);
            modList.Columns.Add("Версия", 110);
            modList.Columns.Add("Статус", 200);
            modList.Columns.Add("Размер", 120);
            page.Controls.Add(modList);

            lblModsCount = Theme.Dim("", 0, 0);
            lblModsCount.Dock = DockStyle.Top;
            lblModsCount.Padding = new Padding(10, 6, 0, 8);
            page.Controls.Add(lblModsCount);

            Panel top = Theme.Card(0, 0, 960, 80);
            top.Dock = DockStyle.Top;
            page.Controls.Add(top);

            top.Controls.Add(Theme.Label("Адрес сервера", 14, 10));
            txtUrl = Theme.Input(14, 34, 700);
            txtUrl.Text = settings.ServerUrl;
            txtUrl.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            top.Controls.Add(txtUrl);

            Button connect = Theme.Button("Подключиться", 724, 32, 140, 28, delegate { ConnectAndCheck(); }, true);
            connect.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            top.Controls.Add(connect);

            Button openMods = Theme.Button("Открыть Mods", 874, 32, 82, 28,
                delegate { OpenFolder(settings.ModsDir); }, false);
            openMods.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            top.Controls.Add(openMods);

            return page;
        }

        // "Что нового": what the server says about the current pack (name, version, note) and
        // which mods changed compared with the previous published pack.
        private TabPage BuildNewsTab()
        {
            TabPage page = NewTab("Что нового");

            newsView = new RichTextBox();
            newsView.Dock = DockStyle.Fill;
            newsView.BackColor = Color.FromArgb(18, 20, 24);
            newsView.ForeColor = Theme.Text;
            newsView.BorderStyle = BorderStyle.None;
            newsView.Font = Theme.Font(9.5f);
            newsView.WordWrap = true;
            newsView.ReadOnly = true;
            newsView.DetectUrls = true;
            newsView.Text = "Нажмите «Проверить моды», чтобы получить сведения о наборе сервера.";
            page.Controls.Add(newsView);

            return page;
        }

        private void ShowNews(ServerInfo info, ModManifest manifest)
        {
            if (newsView == null) { return; }
            StringBuilder sb = new StringBuilder();
            string name = info == null || string.IsNullOrEmpty(info.Name)
                ? (manifest == null ? "набор сервера" : manifest.serverName)
                : info.Name;
            sb.AppendLine(name);
            if (info != null)
            {
                if (!string.IsNullOrEmpty(info.Version)) { sb.AppendLine("Версия набора: " + info.Version); }
                if (!string.IsNullOrEmpty(info.Built)) { sb.AppendLine("Опубликовано: " + info.Built); }
                if (!string.IsNullOrEmpty(info.World)) { sb.AppendLine("Мир: " + info.World); }
                if (info.Slots > 0) { sb.AppendLine("Слотов: " + info.Slots.ToString(CultureInfo.InvariantCulture)); }
                if (!string.IsNullOrEmpty(info.Note)) { sb.AppendLine(); sb.AppendLine(info.Note); }
            }
            if (manifest != null)
            {
                sb.AppendLine();
                sb.AppendLine("Модов: " + manifest.loadOrder.Count.ToString(CultureInfo.InvariantCulture) +
                              "   файлов: " + manifest.files.Count.ToString(CultureInfo.InvariantCulture) +
                              "   размер: " + GamePaths.HumanSize(manifest.TotalBytes));
            }
            sb.AppendLine();
            sb.AppendLine("Что изменилось в этом наборе:");
            if (info == null || info.Changes.Count == 0)
            {
                sb.AppendLine("• сервер не передал список изменений");
            }
            else
            {
                foreach (string line in info.Changes) { sb.AppendLine("• " + line); }
            }
            newsView.Text = sb.ToString();
            newsView.SelectionStart = 0;
            newsView.ScrollToCaret();
        }

        private TabPage BuildLogTab()
        {
            TabPage page = NewTab("Журнал");

            logView = new RichTextBox();
            logView.Dock = DockStyle.Fill;
            logView.BackColor = Color.FromArgb(18, 20, 24);
            logView.ForeColor = Theme.Text;
            logView.BorderStyle = BorderStyle.None;
            logView.Font = new Font("Consolas", 9f);
            logView.WordWrap = false;
            logView.ReadOnly = true;
            page.Controls.Add(logView);

            return page;
        }

        private TabPage BuildSettingsTab()
        {
            TabPage page = NewTab("Настройки");

            Panel scroll = new Panel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            scroll.BackColor = Theme.Bg;
            page.Controls.Add(scroll);

            int y = 8;
            y = PathRow(scroll, y, "Папка игры", settings.GameRoot, delegate(string v)
            {
                settings.GameRoot = v;
                if (string.IsNullOrEmpty(settings.ModsDir) || settings.ModsDir == GamePaths.ClientModsDir(settings.GameRoot))
                {
                    settings.ModsDir = GamePaths.ClientModsDir(v);
                }
                Save();
            });

            y = PathRow(scroll, y, "Папка Mods", settings.ModsDir, delegate(string v)
            {
                settings.ModsDir = v;
                Save();
            });

            y = TextRow(scroll, y, "SteamID", settings.SteamId, delegate(string v) { settings.SteamId = v; Save(); });
            y = TextRow(scroll, y, "Ник", settings.PlayerName, delegate(string v) { settings.PlayerName = v; Save(); });

            y += 10;
            scroll.Controls.Add(Theme.Label("Запуск игры", 12, y).Bold());
            y += 28;

            y = CheckRow(scroll, y, "использовать EAC", settings.UseEac, delegate(bool v) { settings.UseEac = v; Save(); });
            y = CheckRow(scroll, y, "пропускать интро", settings.SkipIntro, delegate(bool v) { settings.SkipIntro = v; Save(); });
            y = CheckRow(scroll, y, "пропускать новости", settings.SkipNews, delegate(bool v) { settings.SkipNews = v; Save(); });
            y = TextRow(scroll, y, "доп. параметры", settings.ExtraArgs, delegate(string v) { settings.ExtraArgs = v; Save(); });

            y += 10;
            scroll.Controls.Add(Theme.Label("Синхронизация", 12, y).Bold());
            y += 28;

            y = CheckRow(scroll, y, "проверять обновления автоматически", settings.AutoCheck, delegate(bool v)
            {
                settings.AutoCheck = v;
                Save();
                ApplyAutoTimer();
            });

            NumericUpDown interval = new NumericUpDown();
            interval.Minimum = 5;
            interval.Maximum = 240;
            interval.Value = Math.Max(5, Math.Min(240, settings.CheckIntervalMin));
            interval.Location = new Point(300, y);
            interval.Width = 80;
            interval.BackColor = Theme.BgInput;
            interval.ForeColor = Theme.Text;
            interval.ValueChanged += delegate
            {
                settings.CheckIntervalMin = (int)interval.Value;
                Save();
                ApplyAutoTimer();
            };
            scroll.Controls.Add(interval);
            scroll.Controls.Add(Theme.Dim("интервал, минут", 390, y + 4));
            y += 34;

            y = CheckRow(scroll, y, "запускать игру сразу после обновления модов", settings.AutoPlayAfterSync,
                delegate(bool v) { settings.AutoPlayAfterSync = v; Save(); });

            scroll.Controls.Add(Theme.Dim("Перед «Играть» лаунчер всегда сверяет содержимое файлов модов.", 12, y));
            y += 28;

            y = CheckRow(scroll, y, "запускать лаунчер вместе с Windows", settings.RunAtStartup, delegate(bool v)
            {
                settings.RunAtStartup = v;
                Save();
                ApplyStartup();
            });

            scroll.Controls.Add(Theme.Dim("P2P-порт: " + settings.PeerPort.ToString(CultureInfo.InvariantCulture) +
                "   (раздаёт скачанные моды другим игрокам)", 12, y));
            y += 26;

            Button openSettings = Theme.Button("Открыть папку настроек", 12, y, 220, 30,
                delegate { OpenFolder(Path.GetDirectoryName(settingsFile)); }, false);
            scroll.Controls.Add(openSettings);

            Button openVk = Theme.Button("Группа ВК", 242, y, 130, 30, delegate { OpenVk(); }, false);
            scroll.Controls.Add(openVk);

            Button redetect = Theme.Button("Найти игру заново", 382, y, 190, 30, delegate
            {
                string found = GamePaths.ClientRoot(null);
                if (found == null)
                {
                    MessageBox.Show(this, "Игра не найдена. Укажите папку вручную.", "WolfLauncher",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                settings.GameRoot = found;
                settings.ModsDir = GamePaths.ClientModsDir(found);
                Save();
                MessageBox.Show(this, "Найдено: " + found, "WolfLauncher", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }, true);
            scroll.Controls.Add(redetect);

            return page;
        }

        private int PathRow(Control parent, int y, string caption, string value, Action<string> onChanged)
        {
            Label label = Theme.Label(caption, 12, y + 4);
            label.Width = 200;
            parent.Controls.Add(label);

            TextBox box = Theme.Input(220, y, 620);
            box.Text = value ?? "";
            box.Leave += delegate { onChanged(box.Text.Trim()); };
            parent.Controls.Add(box);

            Button browse = Theme.Button("…", 848, y - 1, 34, 26, delegate
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

            return y + 34;
        }

        private int TextRow(Control parent, int y, string caption, string value, Action<string> onChanged)
        {
            Label label = Theme.Label(caption, 12, y + 4);
            label.Width = 200;
            parent.Controls.Add(label);

            TextBox box = Theme.Input(220, y, 420);
            box.Text = value ?? "";
            box.TextChanged += delegate { onChanged(box.Text.Trim()); };
            parent.Controls.Add(box);

            return y + 34;
        }

        private int CheckRow(Control parent, int y, string caption, bool value, Action<bool> onChanged)
        {
            CheckBox chk = new CheckBox();
            chk.Text = caption;
            chk.Checked = value;
            chk.Location = new Point(12, y);
            chk.AutoSize = true;
            chk.ForeColor = Theme.Text;
            chk.BackColor = Color.Transparent;
            chk.CheckedChanged += delegate { onChanged(chk.Checked); };
            parent.Controls.Add(chk);
            return y + 28;
        }

        private void Save()
        {
            settings.Save(settingsFile);
        }

        private void LoadState()
        {
            log.Add("WolfLauncher " + WolfInfo.Version + " запущен");
            log.OnLine += delegate(string line) { AppendLog(line); };
            foreach (string line in log.Snapshot()) { AppendLog(line); }

            lblServerName.Text = string.IsNullOrEmpty(settings.ServerUrl) ? "сервер не выбран" : settings.ServerUrl;
            lblModsCount.Text = string.IsNullOrEmpty(settings.ModsDir)
                ? "папка Mods не найдена — задайте её в настройках"
                : "папка Mods: " + settings.ModsDir;

            if (!string.IsNullOrEmpty(settings.GameRoot) && !string.IsNullOrEmpty(settings.ServerUrl))
            {
                lblState.Text = "готов к работе";
                lblState.ForeColor = Theme.TextDim;
            }

            ApplyAutoTimer();
            ApplyStartup();

            if (!settings.SetupDone)
            {
                settings.SetupDone = true;
                Save();
            }
        }

        private void ApplyAutoTimer()
        {
            if (autoTimer != null) { autoTimer.Stop(); autoTimer.Dispose(); autoTimer = null; }
            if (!settings.AutoCheck || string.IsNullOrEmpty(settings.ServerUrl)) { return; }

            autoTimer = new System.Windows.Forms.Timer();
            autoTimer.Interval = Math.Max(5, settings.CheckIntervalMin) * 60 * 1000;
            autoTimer.Tick += delegate { CheckMods(false, true); };
            autoTimer.Start();
        }

        private void AppendLog(string line)
        {
            if (logView == null) { return; }
            this.Ui(delegate
            {
                if (logView.IsDisposed) { return; }
                logView.SelectionStart = logView.TextLength;
                logView.SelectionColor = Theme.Text;
                logView.AppendText(line + Environment.NewLine);
                if (logView.TextLength > 500000)
                {
                    logView.Select(0, 100000);
                    logView.SelectedText = "";
                }
                logView.SelectionStart = logView.TextLength;
                logView.ScrollToCaret();
            });
        }

        private void SetState(string text, Color color)
        {
            this.Ui(delegate
            {
                lblState.Text = text;
                lblState.ForeColor = color;
            });
        }

        // Текстовый отчёт «что у меня не так» для чата: версия лаунчера, папка Mods,
        // хэш набора сервера и первые расхождения с локальными хэшами.
        private void CopyDiagnostics()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("WolfLauncher " + WolfInfo.Version + "  (версия игры " + GameVersionText() + ")");
            sb.AppendLine("сервер: " + settings.ServerUrl);
            sb.AppendLine("Mods: " + settings.ModsDir);
            sb.AppendLine("набор сервера: " + (string.IsNullOrEmpty(lastServerHash) ? "ещё не проверялся" : lastServerHash));
            sb.AppendLine("кэш хешей: " + hashCache.Count.ToString(CultureInfo.InvariantCulture) + " файлов");
            if (lastMismatches.Count == 0)
            {
                sb.AppendLine("расхождений нет");
            }
            else
            {
                sb.AppendLine("расхождения (" + lastMismatches.Count + "):");
                int shown = 0;
                foreach (DiffMismatch m in lastMismatches)
                {
                    if (shown >= 30) { sb.AppendLine("  ... ещё " + (lastMismatches.Count - shown)); break; }
                    sb.AppendLine("  " + m);
                    shown++;
                }
            }
            try
            {
                Clipboard.SetText(sb.ToString());
                SetState("диагностика скопирована в буфер обмена", Theme.Ok);
                log.Add("диагностика скопирована в буфер обмена");
            }
            catch (Exception ex)
            {
                SetState("не удалось скопировать: " + ex.Message, Theme.Err);
            }
        }

        // Версия установленной игры (build.txt в корне клиента), чтобы в отчёте было видно,
        // совпадает ли билд клиента с билдом сервера.
        private string GameVersionText()
        {
            try
            {
                if (string.IsNullOrEmpty(settings.GameRoot)) { return "не найдена"; }
                string file = Path.Combine(settings.GameRoot, "build.txt");
                if (!File.Exists(file)) { return "неизвестна"; }
                string text = File.ReadAllText(file).Trim();
                return string.IsNullOrEmpty(text) ? "неизвестна" : text;
            }
            catch { return "неизвестна"; }
        }

        private void OpenVk()
        {
            try
            {
                string url = string.IsNullOrEmpty(settings.VkUrl) ? "https://vk.ru/7daystodieworld" : settings.VkUrl;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                log.Add("открыта группа ВК: " + url);
            }
            catch (Exception ex)
            {
                log.Add("не удалось открыть ссылку: " + ex.Message);
            }
        }

        private void OpenFolder(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
                }
            }
            catch { }
        }

        // Autostart lives in HKCU\...\Run, so it needs no administrator rights. Turning the
        // checkbox off removes only our own value; other programs are untouched.
        private void ApplyStartup()
        {
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            const string valueName = "WolfLauncher";
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, true))
                {
                    if (key == null) { return; }
                    if (settings.RunAtStartup)
                    {
                        key.SetValue(valueName, "\"" + Application.ExecutablePath + "\"");
                    }
                    else if (key.GetValue(valueName) != null)
                    {
                        key.DeleteValue(valueName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Add("автозапуск: " + ex.Message);
            }
        }

        private void StartPeer()
        {
            peer = new PeerNode(store);
            peer.SteamId = settings.SteamId;
            peer.DisplayName = string.IsNullOrEmpty(settings.PlayerName) ? settings.SteamId : settings.PlayerName;
            peer.Hash = settings.LastHash;
            if (peer.Start(settings.PeerPort))
            {
                log.Add("P2P-порт " + peer.Port);
                settings.PeerPort = peer.Port;
                Save();
            }
            else
            {
                log.Add("P2P-порт занят, раздача другим игрокам недоступна");
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { if (autoTimer != null) { autoTimer.Stop(); } } catch { }
            try { if (pulseTimer != null) { pulseTimer.Stop(); pulseTimer.Dispose(); } } catch { }
            try { if (peer != null) { peer.Stop(); } } catch { }
            Save();
            try { hashCache.Save(); } catch { }
            // Staged build replaces the running exe on shutdown: renaming a running exe is
            // allowed, overwriting it is not, so this needs no helper process.
            if (!string.IsNullOrEmpty(pendingStaging))
            {
                LauncherUpdate.ApplyStaged(pendingExe, pendingStaging, log.Add);
                pendingStaging = null;
            }
            base.OnFormClosing(e);
        }
    }
}
