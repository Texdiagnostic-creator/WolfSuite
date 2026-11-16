using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Wolf.Server
{
    // Вкладка «Инвентарь»: пояс, рюкзак и экипировка выбранного игрока с иконками.
    //
    // У игры нет консольной команды, читающей инвентарь, поэтому данные собирает
    // наш серверный мод WolfInventory: команда `wolfinv` выгружает слоты всех игроков
    // онлайн в <папку сейва>\WolfInventory\<имя>.json. Вкладка только читает эти файлы
    // и рисует сетку; иконки берутся из Data\ItemIcons (имя файла = имя предмета).
    public sealed partial class ServerForm
    {
        private ComboBox invPlayers;
        private Label invStatus;
        private Panel invBoard;
        private readonly Dictionary<string, Image> invIcons = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> invIconIndex;
        private string invSelectedName = "";
        private bool invBusy;

        private const int InvCardW = 78;
        private const int InvCardH = 98;
        private const int InvIconSize = 52;
        private const int InvColumns = 12;

        private TabPage BuildInventoryTab()
        {
            TabPage page = NewTab("Инвентарь");

            Panel top = Theme.Card(10, 8, 1090, 84);
            top.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(top);

            top.Controls.Add(Theme.Label("Инвентарь игрока", 14, 8).Bold());
            top.Controls.Add(Theme.Dim("игрок:", 14, 44));

            invPlayers = new ComboBox();
            invPlayers.DropDownStyle = ComboBoxStyle.DropDownList;
            invPlayers.Location = new Point(70, 40);
            invPlayers.Size = new Size(300, 26);
            invPlayers.SelectedIndexChanged += delegate { LoadSelectedInventory(); };
            top.Controls.Add(invPlayers);

            Button refresh = Theme.Button("Обновить", 382, 38, 120, 30, delegate { RefreshInventory(); }, true);
            top.Controls.Add(refresh);

            Button open = Theme.Button("Открыть папку", 510, 38, 140, 30,
                delegate { app.OpenFolder(InventoryDir()); }, false);
            top.Controls.Add(open);

            top.Controls.Add(Theme.Dim("иконки: Data\\ItemIcons", 660, 16));

            invStatus = Theme.Dim("", 14, 62);
            invStatus.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            top.Controls.Add(invStatus);

            invBoard = new Panel();
            invBoard.Location = new Point(10, 100);
            invBoard.Size = new Size(1090, 518);
            invBoard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            invBoard.BackColor = Theme.BgPanel;
            invBoard.AutoScroll = true;
            page.Controls.Add(invBoard);

            return page;
        }

        private string InventoryDir()
        {
            string world = SaveWorldDir();
            return world == null ? null : Path.Combine(world, "WolfInventory");
        }

        // Команда мода выгружает всех игроков онлайн сразу: список в панели и файлы
        // должны соответствовать друг другу, иначе легко показать устаревший снимок.
        private void RefreshInventory()
        {
            if (invBusy) { return; }
            invBusy = true;
            SetInvStatus("запрашиваю у сервера инвентари игроков…", Theme.TextDim);

            ThreadPool.QueueUserWorkItem(delegate
            {
                List<GamePlayer> players = new List<GamePlayer>();
                string error = null;
                try
                {
                    if (app.Telnet.Connected)
                    {
                        players = Telnet.ParsePlayers(app.Telnet.Command("listplayers", 1500));
                        app.Telnet.Command("wolfinv", 2000);
                    }
                    else
                    {
                        error = "telnet не подключён — показан последний сохранённый снимок";
                    }
                }
                catch (Exception ex)
                {
                    error = "telnet: " + ex.Message;
                }

                this.Ui(delegate
                {
                    invBusy = false;
                    FillInventoryPlayers(players);
                    LoadSelectedInventory();
                    if (error != null && app.Telnet.Connected) { SetInvStatus(error, Theme.Warn); }
                });
            });
        }

        // Список игроков для выбора. Выбор сохраняется между обновлениями: админ смотрит
        // одного и того же человека и не хочет каждый раз целиться в выпадающий список.
        private void FillInventoryPlayers(List<GamePlayer> players)
        {
            if (invPlayers == null || invPlayers.IsDisposed) { return; }

            string keep = invSelectedName;
            if (string.IsNullOrEmpty(keep) && invPlayers.SelectedItem != null)
            {
                keep = Convert.ToString(invPlayers.SelectedItem, CultureInfo.InvariantCulture);
            }

            invPlayers.BeginUpdate();
            try
            {
                invPlayers.Items.Clear();
                foreach (GamePlayer p in players)
                {
                    if (string.IsNullOrEmpty(p.Name)) { continue; }
                    invPlayers.Items.Add(p.Name);
                }
                if (invPlayers.Items.Count == 0 && keep.Length > 0)
                {
                    invPlayers.Items.Add(keep);
                }

                int index = -1;
                for (int i = 0; i < invPlayers.Items.Count; i++)
                {
                    if (string.Equals(Convert.ToString(invPlayers.Items[i], CultureInfo.InvariantCulture),
                                      keep, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
                }
                if (index < 0 && invPlayers.Items.Count > 0) { index = 0; }
                if (index >= 0) { invPlayers.SelectedIndex = index; }
            }
            finally
            {
                invPlayers.EndUpdate();
            }
        }

        private void LoadSelectedInventory()
        {
            if (invPlayers == null || invPlayers.SelectedItem == null)
            {
                invSelectedName = "";
                RenderInventory(null, null);
                SetInvStatus("нет игроков онлайн — выгрузка появятся после входа игрока", Theme.TextDim);
                return;
            }
            invSelectedName = Convert.ToString(invPlayers.SelectedItem, CultureInfo.InvariantCulture);

            string dir = InventoryDir();
            if (dir == null)
            {
                RenderInventory(null, null);
                SetInvStatus("папка мира не найдена — проверьте UserDataFolder/GameName в конфиге", Theme.Warn);
                return;
            }

            Dictionary<string, object> data = null;
            string file = FindDumpFile(dir, invSelectedName);
            if (file != null) { data = Json.ReadFile(file); }

            RenderInventory(data, invSelectedName);

            if (data == null)
            {
                SetInvStatus("снимок для «" + invSelectedName + "» ещё не создан — нажмите «Обновить», " +
                             "когда игрок в игре и мод WolfInventory установлен", Theme.Warn);
            }
        }

        // Файл ищем по полю name, а не по имени файла: ники с недопустимыми для Windows
        // символами мод заменяет на подчёркивания, и попасть в имя напрямую нельзя.
        private static string FindDumpFile(string dir, string playerName)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { return null; }
            string[] files;
            try { files = Directory.GetFiles(dir, "*.json"); }
            catch { return null; }

            string freshest = null;
            DateTime freshestTime = DateTime.MinValue;
            foreach (string f in files)
            {
                if (freshestTime == DateTime.MinValue)
                {
                    try { freshestTime = File.GetLastWriteTimeUtc(f); freshest = f; } catch { }
                }
                try
                {
                    Dictionary<string, object> head = Json.ReadFile(f);
                    if (string.Equals(Json.Str(head, "name", ""), playerName, StringComparison.OrdinalIgnoreCase))
                    {
                        return f;
                    }
                    DateTime t = File.GetLastWriteTimeUtc(f);
                    if (t > freshestTime) { freshestTime = t; freshest = f; }
                }
                catch { }
            }
            return null;
        }

        private void RenderInventory(Dictionary<string, object> data, string playerName)
        {
            if (invBoard == null || invBoard.IsDisposed) { return; }

            invBoard.SuspendLayout();
            try
            {
                invBoard.Controls.Clear();

                if (data == null)
                {
                    invBoard.ResumeLayout();
                    return;
                }

                List<Dictionary<string, object>> hotbar = new List<Dictionary<string, object>>();
                List<Dictionary<string, object>> backpack = new List<Dictionary<string, object>>();
                List<Dictionary<string, object>> equipment = new List<Dictionary<string, object>>();
                int items = 0;

                foreach (object o in Json.Arr(data, "pockets"))
                {
                    Dictionary<string, object> slot = o as Dictionary<string, object>;
                    if (slot == null) { continue; }
                    string where = Json.Str(slot, "where", "");
                    if (!Json.Bool(slot, "empty", false)) { items++; }
                    if (string.Equals(where, "hotbar", StringComparison.Ordinal)) { hotbar.Add(slot); }
                    else { backpack.Add(slot); }
                }
                foreach (object o in Json.Arr(data, "equipment"))
                {
                    Dictionary<string, object> slot = o as Dictionary<string, object>;
                    if (slot == null) { continue; }
                    if (!Json.Bool(slot, "empty", false)) { items++; }
                    equipment.Add(slot);
                }

                int y = 8;
                y = AddInventorySection("Пояс", hotbar, y);
                y = AddInventorySection("Рюкзак", backpack, y);
                y = AddInventorySection("Экипировка", equipment, y);

                string when = Json.Str(data, "generatedUtc", "");
                int level = Json.Int(data, "level", 0);
                string pos = Json.Str(data, "position", "");

                string summary = "предметов: " + items.ToString(CultureInfo.InvariantCulture) +
                                 "   уровень: " + level.ToString(CultureInfo.InvariantCulture) +
                                 (pos.Length > 0 ? "   позиция: " + pos : "") +
                                 (when.Length > 0 ? "   снимок: " + when.Replace('T', ' ').TrimEnd('Z') + " UTC" : "");
                SetInvStatus(summary, Theme.Ok);
            }
            finally
            {
                invBoard.ResumeLayout();
            }
        }

        private int AddInventorySection(string title, List<Dictionary<string, object>> slots, int y)
        {
            Label head = Theme.Label(title, 10, y).Bold();
            head.AutoSize = true;
            invBoard.Controls.Add(head);

            Label count = Theme.Dim("—", 140, y + 1);
            count.AutoSize = true;
            invBoard.Controls.Add(count);

            if (slots.Count == 0) { count.Text = "нет данных"; }

            y += 24;
            int used = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                if (!Json.Bool(slots[i], "empty", false)) { used++; }
            }
            count.Text = used.ToString(CultureInfo.InvariantCulture) + " / " +
                         slots.Count.ToString(CultureInfo.InvariantCulture);

            int col = 0;
            int row = 0;
            foreach (Dictionary<string, object> slot in slots)
            {
                AddInventoryCard(slot, 10 + col * (InvCardW + 6), y + row * (InvCardH + 6));
                col++;
                if (col >= InvColumns) { col = 0; row++; }
            }

            int rows = slots.Count == 0 ? 0 : row + 1;
            return y + rows * (InvCardH + 6) + 12;
        }

        private void AddInventoryCard(Dictionary<string, object> slot, int x, int y)
        {
            bool empty = Json.Bool(slot, "empty", false);
            string icon = Json.Str(slot, "icon", "");
            string label = Json.Str(slot, "label", "");
            string name = Json.Str(slot, "name", "");
            int count = Json.Int(slot, "count", 0);
            int quality = Json.Int(slot, "quality", 0);
            int mods = Json.Int(slot, "mods", 0);
            double durability = Json.Long(slot, "durability", -1);
            string where = Json.Str(slot, "where", "");

            Panel card = new Panel();
            card.Location = new Point(x, y);
            card.Size = new Size(InvCardW, InvCardH);
            card.BackColor = empty ? Theme.Bg : Theme.BgInput;
            invBoard.Controls.Add(card);

            PictureBox pic = new PictureBox();
            pic.Location = new Point((InvCardW - InvIconSize) / 2, 4);
            pic.Size = new Size(InvIconSize, InvIconSize);
            pic.SizeMode = PictureBoxSizeMode.Zoom;
            pic.BackColor = Color.Transparent;
            Image image = empty ? null : LoadItemIcon(icon, name);
            if (image != null) { pic.Image = image; }
            card.Controls.Add(pic);

            Label title = new Label();
            title.AutoSize = false;
            title.Location = new Point(2, InvIconSize + 6);
            title.Size = new Size(InvCardW - 4, 18);
            title.TextAlign = ContentAlignment.MiddleCenter;
            title.ForeColor = empty ? Theme.TextDim : Theme.Text;
            title.Font = Theme.Font(7.5f);
            title.Text = empty ? "—" : (count > 1 ? "x" + count.ToString(CultureInfo.InvariantCulture) : ShortName(label));
            card.Controls.Add(title);

            Label sub = new Label();
            sub.AutoSize = false;
            sub.Location = new Point(2, InvIconSize + 24);
            sub.Size = new Size(InvCardW - 4, 16);
            sub.TextAlign = ContentAlignment.MiddleCenter;
            sub.ForeColor = Theme.TextDim;
            sub.Font = Theme.Font(7f);
            sub.Text = empty ? "" : Subtitle(quality, durability);
            card.Controls.Add(sub);

            ToolTip tip = new ToolTip();
            tip.SetToolTip(card, empty
                ? "пустой слот (" + where + ")"
                : label + (quality > 0 ? "\nкачество: " + quality.ToString(CultureInfo.InvariantCulture) : "") +
                  (durability >= 0 ? "\nпрочность: " + durability.ToString("0", CultureInfo.InvariantCulture) + "%" : "") +
                  (mods > 0 ? "\nмодификаций: " + mods.ToString(CultureInfo.InvariantCulture) : "") +
                  "\nслот: " + where);
        }

        private static string Subtitle(int quality, double durability)
        {
            if (durability >= 0 && quality > 0)
            {
                return quality.ToString(CultureInfo.InvariantCulture) + " · " +
                       durability.ToString("0", CultureInfo.InvariantCulture) + "%";
            }
            if (durability >= 0) { return durability.ToString("0", CultureInfo.InvariantCulture) + "%"; }
            if (quality > 0) { return "ур. " + quality.ToString(CultureInfo.InvariantCulture); }
            return "";
        }

        private static string ShortName(string label)
        {
            if (string.IsNullOrEmpty(label)) { return ""; }
            string s = label.Trim();
            return s.Length <= 12 ? s : s.Substring(0, 11) + "…";
        }

        // Иконки кэшируются в памяти: сетка перерисовывается на каждое обновление,
        // и читать с диска 60 PNG каждый раз незачем.
        private Image LoadItemIcon(string icon, string fallback)
        {
            string key = string.IsNullOrEmpty(icon) ? fallback : icon;
            if (string.IsNullOrEmpty(key)) { return null; }

            Image cached;
            if (invIcons.TryGetValue(key, out cached)) { return cached; }

            string file = FindIconFile(key);
            Image result = null;
            try
            {
                if (file != null)
                {
                    using (Image raw = Image.FromFile(file))
                    {
                        result = new Bitmap(raw, InvIconSize, InvIconSize);
                    }
                }
            }
            catch { result = null; }

            invIcons[key] = result;
            return result;
        }

        // Ванильные иконки лежат в Data\ItemIcons, а иконки модовых предметов —
        // в ассетах самих модов (Mods\<мод>\UIAtlases\ItemIconAtlas). Индекс
        // собирается один раз, чтобы не обходить папки модов на каждую карточку.
        private string FindIconFile(string key)
        {
            string root = app.ResolveServerRoot() ?? "";
            string file = Path.Combine(root, "Data", "ItemIcons", key + ".png");
            if (File.Exists(file)) { return file; }

            if (invIconIndex == null) { BuildIconIndex(root); }

            string found;
            return invIconIndex.TryGetValue(key, out found) ? found : null;
        }

        private void BuildIconIndex(string root)
        {
            invIconIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string mods = Path.Combine(root, "Mods");
            if (!Directory.Exists(mods)) { return; }

            string[] dirs;
            try { dirs = Directory.GetDirectories(mods); }
            catch { return; }

            foreach (string mod in dirs)
            {
                string atlas = Path.Combine(mod, "UIAtlases", "ItemIconAtlas");
                if (!Directory.Exists(atlas)) { continue; }
                string[] pngs;
                try { pngs = Directory.GetFiles(atlas, "*.png"); }
                catch { continue; }
                foreach (string png in pngs)
                {
                    string name = Path.GetFileNameWithoutExtension(png);
                    if (!invIconIndex.ContainsKey(name)) { invIconIndex[name] = png; }
                }
            }
        }

        private void SetInvStatus(string text, Color color)
        {
            if (invStatus == null || invStatus.IsDisposed) { return; }
            invStatus.Text = text;
            invStatus.ForeColor = color;
        }
    }
}
