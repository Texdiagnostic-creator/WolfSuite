using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Wolf.Server
{
    // Вкладка «Чат»: лента игрового чата из лога сервера + отправка сообщений через telnet.
    // Лог и консоль — один и тот же канал, поэтому отправленное сообщение приходит обратно
    // в ленту строкой "Chat (from '-non-player-', ...)" и дублировать его локально не нужно.
    public sealed partial class ServerForm
    {
        private RichTextBox chatView;
        private TextBox chatInput;
        private Label chatStatus;
        private Button chatRefresh;
        private bool chatLoaded;

        // V3.x: Chat (from 'Name', entity id '123', to 'Global'): text
        // В старых сборках текст был в кавычках — кавычки снимаем отдельно.
        private static readonly Regex ChatRegex = new Regex(
            @"Chat \(from '(?<from>[^']*)', entity id '(?<id>[^']*)', to '(?<to>[^']*)'\):\s*(?<msg>.*)$",
            RegexOptions.Compiled);

        private TabPage BuildChatTab()
        {
            TabPage page = NewTab("Чат");

            // Лента — снимок хвоста журнала, а журнал на каждом запуске сервера новый.
            // Если хвост ушёл на другой файл или позиция потерялась, история выглядела
            // пустой до следующего сообщения в чате. Кнопка перечитывает хвост вручную.
            Panel topBar = new Panel();
            topBar.Dock = DockStyle.Top;
            topBar.Height = 46;
            topBar.BackColor = Theme.Bg;
            page.Controls.Add(topBar);

            chatRefresh = Theme.Button("Обновить", 4, 8, 150, 28, delegate { RefreshChat(); }, false);
            topBar.Controls.Add(chatRefresh);

            Label chatHint = Theme.Dim("лента собирается из журнала сервера", 166, 14);
            chatHint.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            topBar.Controls.Add(chatHint);

            Panel inputBar = new Panel();
            inputBar.Dock = DockStyle.Bottom;
            inputBar.Height = 72;
            inputBar.BackColor = Theme.Bg;
            page.Controls.Add(inputBar);

            chatInput = Theme.Input(10, 10, 900);
            chatInput.Font = Theme.Font(10f);
            chatInput.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;   // иначе WinForms «пикнет» и не отправит
                    SendChat();
                }
            };
            inputBar.Controls.Add(chatInput);

            Button send = Theme.Button("Отправить", 1010, 9, 140, 30, delegate { SendChat(); }, true);
            inputBar.Controls.Add(send);

            // Анкоры считались бы от размера страницы на момент создания (у неё ещё нет
            // реальной ширины), поэтому раскладываем вручную по фактической ширине панели.
            inputBar.Resize += delegate
            {
                send.Left = Math.Max(200, inputBar.Width - send.Width - 10);
                chatInput.Width = Math.Max(120, send.Left - chatInput.Left - 10);
            };

            chatStatus = Theme.Dim("консоль сервера: проверяю...", 10, 46);
            chatStatus.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            // Строка состояния — ещё и кнопка: если связь не поднялась сама, щелчок по ней
            // запускает переподключение немедленно, не дожидаясь сторожа.
            chatStatus.Cursor = Cursors.Hand;
            chatStatus.Click += delegate
            {
                EnsureTelnet("вручную");
                chatStatus.Text = "подключаюсь к консоли сервера…";
                chatStatus.ForeColor = Theme.Warn;
            };
            inputBar.Controls.Add(chatStatus);

            // Статус консоли меняется сам (telnet подключается в LoadState), поэтому обновляем
            // его, пока вкладка открыта.
            System.Windows.Forms.Timer chatClock = new System.Windows.Forms.Timer();
            chatClock.Interval = 2500;
            chatClock.Tick += delegate { if (page.Visible) { UpdateChatStatus(); } };
            chatClock.Start();

            chatView = new RichTextBox();
            chatView.Dock = DockStyle.Fill;
            chatView.BackColor = Color.FromArgb(18, 20, 24);
            chatView.ForeColor = Theme.Text;
            chatView.BorderStyle = BorderStyle.None;
            chatView.Font = Theme.Font(10f);
            chatView.WordWrap = true;
            chatView.ReadOnly = true;
            chatView.DetectUrls = false;
            chatView.HideSelection = false;
            page.Controls.Add(chatView);
            page.Controls.SetChildIndex(chatView, 0);

            // История подтягивается при первом открытии вкладки: на момент сборки формы у
            // контроллера сервера ещё не известен путь к логу (Root читается в LoadState).
            // Если лента всё же пуста (журнал появился позже или хвост переехал на новый
            // файл), догружаем её при следующем входе, чтобы вкладка не оставалась пустой.
            page.Enter += delegate
            {
                if (!chatLoaded)
                {
                    chatLoaded = true;
                    LoadRecentChat();
                    return;
                }
                if (chatView != null && !chatView.IsDisposed && chatView.TextLength == 0)
                {
                    LoadRecentChat();
                }
            };
            return page;
        }

        // Ручное обновление ленты: перечитывает хвост журнала с диска и ставит хвост на
        // конец живого файла. Нужно, когда сервер перезапустился и журнал переехал в новый
        // файл, или когда в ленте долго ничего не появлялось — она выглядела пустой.
        private void RefreshChat()
        {
            chatLoaded = true;
            if (chatRefresh != null && !chatRefresh.IsDisposed) { chatRefresh.Enabled = false; }
            try
            {
                string file = app.Controller.NewestLog();

                // Позицию хвоста сдвигаем ДО чтения: иначе он успеет выдать те же строки
                // ещё раз и в ленте появятся дубли.
                app.Controller.FollowNewestLog(true);

                if (chatView != null && !chatView.IsDisposed) { chatView.Clear(); }
                LoadRecentChat();

                chatStatus.Text = string.IsNullOrEmpty(file)
                    ? "журнал сервера ещё не создан — обновление не помогло"
                    : "обновлено из " + Path.GetFileName(file);
                chatStatus.ForeColor = string.IsNullOrEmpty(file) ? Theme.Warn : Theme.Ok;
            }
            catch (Exception ex)
            {
                chatStatus.Text = "ошибка обновления: " + ex.Message;
                chatStatus.ForeColor = Theme.Err;
            }
            finally
            {
                if (chatRefresh != null && !chatRefresh.IsDisposed) { chatRefresh.Enabled = true; }
            }
        }

        // Лента не должна быть пустой: подтягиваем хвост лога при открытии вкладки.
        private void LoadRecentChat()
        {
            try
            {
                string file = app.Controller.NewestLog();
                if (file == null || !File.Exists(file)) { UpdateChatStatus(); return; }

                const int TailBytes = 262144;
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long start = Math.Max(0, fs.Length - TailBytes);
                    fs.Seek(start, SeekOrigin.Begin);
                    using (StreamReader reader = new StreamReader(fs, Encoding.UTF8))
                    {
                        bool first = start > 0;   // первая строка может быть обрезана посередине
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (first) { first = false; continue; }
                            ParseChatLine(line);
                        }
                    }
                }
            }
            catch { }
            UpdateChatStatus();
        }

        private void UpdateChatStatus()
        {
            if (chatStatus == null || chatStatus.IsDisposed) { return; }

            bool connected = app.Telnet.Connected;
            bool enabled = !app.Config.Loaded || app.Config.GetBool("TelnetEnabled", false);
            bool online = app.Controller.Running || app.Controller.ExternalPid != 0;

            string text;
            Color color;
            if (connected)
            {
                text = "консоль сервера подключена — сообщение увидят все игроки";
                color = Theme.TextDim;
            }
            else if (!enabled)
            {
                text = "консоль сервера выключена (TelnetEnabled=false в serverconfig.xml) — отправка недоступна";
                color = Theme.Warn;
            }
            else if (!online)
            {
                text = "сервер остановлен — консоль подключится сама после запуска";
                color = Theme.Warn;
            }
            else if (app.Settings.TelnetAutoReconnect)
            {
                // Так выглядит нормальный перезапуск сервера: сторож поднимет связь сам,
                // поэтому это не ошибка, а ожидание.
                text = "консоль сервера переподключается… (нажмите, чтобы попробовать сразу)";
                color = Theme.Warn;
            }
            else
            {
                text = "консоль сервера не подключена — нажмите, чтобы подключиться";
                color = Theme.Warn;
            }

            if (chatStatus.Text != text) { chatStatus.Text = text; }
            chatStatus.ForeColor = color;
        }

        private void SendChat()
        {
            string text = (chatInput.Text ?? "").Trim();
            if (text.Length == 0) { return; }

            if (!app.Telnet.Connected)
            {
                // Молча терять набранное сообщение нельзя: запускаем переподключение и
                // оставляем текст в поле, чтобы его можно было отправить через пару секунд.
                EnsureTelnet("отправка в чат");
                UpdateChatStatus();
                chatStatus.Text = "консоль не подключена — переподключаюсь, повторите отправку";
                chatStatus.ForeColor = Theme.Warn;
                return;
            }

            try
            {
                app.Telnet.Send(Telnet.SayCommand(text));
                chatInput.Clear();
                app.Log.Add("[chat] сервер: " + text);
                chatStatus.Text = "отправлено";
                chatStatus.ForeColor = Theme.Ok;
            }
            catch (Exception ex)
            {
                chatStatus.Text = "ошибка отправки: " + ex.Message;
                chatStatus.ForeColor = Theme.Err;
            }
        }

        private void ParseChatLine(string line)
        {
            if (string.IsNullOrEmpty(line)) { return; }
            if (line.IndexOf("Chat (from", StringComparison.Ordinal) < 0) { return; }

            Match match = ChatRegex.Match(line);
            if (!match.Success) { return; }

            string msg = match.Groups["msg"].Value.Trim();
            if (msg.Length > 1 && msg[0] == '\'' && msg[msg.Length - 1] == '\'')
            {
                msg = msg.Substring(1, msg.Length - 2);
            }

            string stamp = StampOf(line);
            string from = match.Groups["from"].Value;
            string to = match.Groups["to"].Value;

            // Игровой чат пишет в лог платформенный id, а ник — в начале текста:
            //   Chat (from 'Steam_7656...', entity id '145114', to 'Global'): 'Red Witch': спасибо
            // Без этого в ленте вместо ника красовался Steam_7656..., и сообщение было
            // не узнать. Ник берём из префикса, остальное — текст.
            if (from.StartsWith("Steam_", StringComparison.OrdinalIgnoreCase) ||
                from.StartsWith("EOS_", StringComparison.OrdinalIgnoreCase))
            {
                Match nick = NickRegex.Match(msg);
                if (nick.Success)
                {
                    string text = nick.Groups["text"].Value.Trim();
                    if (text.Length > 0)
                    {
                        from = nick.Groups["nick"].Value.Trim();
                        msg = text;
                    }
                }
            }

            this.Ui(delegate { AppendChat(stamp, from, to, msg); });
        }

        // "'Red Witch': спасибо" -> ник + текст
        private static readonly Regex NickRegex = new Regex(
            @"^'(?<nick>[^']+)':\s*(?<text>.*)$", RegexOptions.Compiled);

        // "2026-11-04T14:53:46 2483.797 INF ..." -> "14:53:46"
        private static string StampOf(string line)
        {
            if (line.Length >= 19 && line[10] == 'T') { return line.Substring(11, 8); }
            return DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private void AppendChat(string stamp, string from, string to, string text)
        {
            if (chatView == null || chatView.IsDisposed) { return; }

            bool console = string.Equals(from, "-non-player-", StringComparison.OrdinalIgnoreCase);
            string who = console ? "Сервер" : from;

            chatView.SelectionStart = chatView.TextLength;
            chatView.SelectionColor = Theme.TextDim;
            chatView.AppendText("[" + stamp + "] ");

            chatView.SelectionColor = console ? Theme.Info : Theme.Accent;
            chatView.SelectionFont = Theme.Font(10f, FontStyle.Bold);
            chatView.AppendText(who);
            chatView.SelectionFont = Theme.Font(10f);

            chatView.SelectionColor = Theme.TextDim;
            string target = string.IsNullOrEmpty(to) || string.Equals(to, "Global", StringComparison.OrdinalIgnoreCase)
                ? ": "
                : " → " + to + ": ";
            chatView.AppendText(target);

            chatView.SelectionColor = Theme.Text;
            chatView.AppendText(text + Environment.NewLine);

            if (chatView.TextLength > 400000)
            {
                chatView.Select(0, 100000);
                chatView.SelectedText = "";
            }
            chatView.SelectionStart = chatView.TextLength;
            chatView.ScrollToCaret();
        }
    }
}

