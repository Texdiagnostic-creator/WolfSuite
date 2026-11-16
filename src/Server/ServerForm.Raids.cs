using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Wolf.Server
{
    // Вкладка «Рейды»: история атак на базы. Два источника, оба лежат в папке сохранения
    // текущего мира:
    //   ChaoticRaidLog\incidents.tsv — мод Chaotic's Raid Log (кто ломал чужие лендклеймы);
    //   BlockRaidLog.txt            — наш собственный лог (что именно получил клейм-блок).
    // Файлы только читаются, вкладка ничего не меняет и не мешает модам.
    public sealed partial class ServerForm
    {
        private static readonly Regex BlockRaidRegex = new Regex(
            @"^(?<ts>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2})\s+(?<kind>\w+)\s+pos=(?<pos>\S+)\s+" +
            @"block=(?<block>\S+)\s+dmg=(?<dmg>[-\d.]+)\s+claim='(?<claim>[^']*)'\s+by=(?<by>\S+)\s+" +
            @"(?<etype>\S+)\s+'(?<ename>[^']*)'",
            RegexOptions.Compiled);

        private ListView raidsList;
        private Label raidsStatus;
        private ComboBox raidsSource;
        private TextBox raidsFilter;
        private CheckBox raidsPlayersOnly;
        private string[] raidHeaders = new string[0];
        private readonly List<string[]> raidRows = new List<string[]>();

        private TabPage BuildRaidsTab()
        {
            TabPage page = NewTab("Рейды");

            Panel top = Theme.Card(10, 8, 1090, 84);
            top.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(top);

            top.Controls.Add(Theme.Label("История рейдов на базы", 14, 8).Bold());

            raidsSource = new ComboBox();
            raidsSource.DropDownStyle = ComboBoxStyle.DropDownList;
            raidsSource.Location = new Point(14, 38);
            raidsSource.Size = new Size(330, 26);
            raidsSource.Items.AddRange(new object[]
            {
                "Атаки на базы (Chaotic's Raid Log)",
                "Урон по клейму (наш BlockRaidLog)",
            });
            raidsSource.SelectedIndex = 0;
            raidsSource.SelectedIndexChanged += delegate { RefreshRaids(); };
            top.Controls.Add(raidsSource);

            raidsFilter = Theme.Input(356, 38, 260);
            raidsFilter.TextChanged += delegate { FillRaidsList(); };
            top.Controls.Add(raidsFilter);

            raidsPlayersOnly = new CheckBox();
            raidsPlayersOnly.Text = "только игроки";
            raidsPlayersOnly.Location = new Point(628, 42);
            raidsPlayersOnly.AutoSize = true;
            raidsPlayersOnly.ForeColor = Theme.Text;
            raidsPlayersOnly.BackColor = Color.Transparent;
            raidsPlayersOnly.CheckedChanged += delegate { FillRaidsList(); };
            top.Controls.Add(raidsPlayersOnly);

            Button refresh = Theme.Button("Обновить", 730, 36, 120, 30, delegate { RefreshRaids(); }, true);
            refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            top.Controls.Add(refresh);

            Button open = Theme.Button("Открыть папку", 858, 36, 140, 30,
                delegate { app.OpenFolder(SaveWorldDir()); }, false);
            open.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            top.Controls.Add(open);

            raidsStatus = Theme.Dim("", 10, 100);
            raidsStatus.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            page.Controls.Add(raidsStatus);

            raidsList = Theme.StyleList(new ListView());
            raidsList.View = View.Details;
            raidsList.FullRowSelect = true;
            raidsList.MultiSelect = true;
            raidsList.Location = new Point(10, 124);
            raidsList.Size = new Size(1090, 494);
            raidsList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            page.Controls.Add(raidsList);

            return page;
        }

        // Папка сохранения текущего мира. Основной путь — GameWorld/GameName из конфига,
        // запасной — самый свежий каталог с main.ttw (конфиг мог поменяться, а мир — нет).
        private string SaveWorldDir()
        {
            string saves = app.SavesDir();
            if (string.IsNullOrEmpty(saves) || !Directory.Exists(saves)) { return null; }

            if (app.Config.Loaded)
            {
                string world = app.Config.Get("GameWorld", "");
                string game = app.Config.Get("GameName", "");
                if (!string.IsNullOrEmpty(world) && !string.IsNullOrEmpty(game))
                {
                    string exact = Path.Combine(Path.Combine(saves, world), game);
                    if (Directory.Exists(exact)) { return exact; }
                }
            }

            string best = null;
            DateTime bestTime = DateTime.MinValue;
            try
            {
                foreach (string world in Directory.GetDirectories(saves))
                {
                    foreach (string game in Directory.GetDirectories(world))
                    {
                        string marker = Path.Combine(game, "main.ttw");
                        if (!File.Exists(marker)) { continue; }
                        DateTime t = File.GetLastWriteTimeUtc(marker);
                        if (t > bestTime) { bestTime = t; best = game; }
                    }
                }
            }
            catch { }
            return best;
        }

        private void RefreshRaids()
        {
            if (raidsList == null || raidsList.IsDisposed) { return; }

            raidHeaders = new string[0];
            raidRows.Clear();

            string dir = SaveWorldDir();
            if (dir == null)
            {
                string saves = app.SavesDir();
                SetRaidsStatus("не найдена папка сохранения мира (Saves: " + (saves ?? "?") +
                               ", конфиг: " + (app.Config.Loaded ? "загружен" : "не загружен") + ")", false);
                FillRaidsList();
                return;
            }

            bool chaotic = raidsSource.SelectedIndex == 0;
            string file = chaotic
                ? Path.Combine(Path.Combine(dir, "ChaoticRaidLog"), "incidents.tsv")
                : Path.Combine(dir, "BlockRaidLog.txt");

            try
            {
                if (!File.Exists(file))
                {
                    SetRaidsStatus(chaotic
                        ? "ChaoticRaidLog ещё не писал в этот мир: файл incidents.tsv появится после первой атаки на чужой лендклейм"
                        : "файл BlockRaidLog.txt не найден — мод BlockRaidLog не установлен", false);
                    FillRaidsList();
                    return;
                }

                if (chaotic) { ReadChaoticRaids(file); } else { ReadBlockRaids(file); }

                SetRaidsStatus("записей: " + raidRows.Count.ToString(CultureInfo.InvariantCulture) +
                               "   файл: " + Path.GetFileName(file) +
                               "   изменён: " + File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    true);
            }
            catch (Exception ex)
            {
                SetRaidsStatus("не удалось прочитать лог рейдов: " + ex.Message, false);
            }
            FillRaidsList();
        }

        private void SetRaidsStatus(string text, bool ok)
        {
            raidsStatus.Text = text;
            raidsStatus.ForeColor = ok ? Theme.Ok : Theme.Warn;
        }

        // incidents.tsv: 19 колонок через табуляцию, так их пишет Chaotic's Raid Log:
        // 0 версия, 1-3 позиция клейма, 4-5 владелец, 6-7 атакующий, 8 игрок?,
        // 9-10 начало/конец (ticks UTC), 11 удары, 12 урон, 13 разрушено блоков,
        // 14 взрыв?, 15 владелец был онлайн?, 16-17 центр клейма (x,z), 18 блоки "имя:кол-во;".
        private void ReadChaoticRaids(string file)
        {
            raidHeaders = new[]
            {
                "Начало", "Владелец базы", "Атакующий", "Кто", "Удары", "Урон", "Разрушено",
                "Блоки", "Центр x,z", "Взрыв", "Хозяин онлайн",
            };

            foreach (string line in File.ReadAllLines(file))
            {
                if (line.Length == 0) { continue; }
                string[] f = line.Split('\t');
                if (f.Length < 19) { continue; }

                int hits, damage, destroyed, cx, cz;
                Int32.TryParse(f[11], NumberStyles.Any, CultureInfo.InvariantCulture, out hits);
                Int32.TryParse(f[12], NumberStyles.Any, CultureInfo.InvariantCulture, out damage);
                Int32.TryParse(f[13], NumberStyles.Any, CultureInfo.InvariantCulture, out destroyed);
                Int32.TryParse(f[16], NumberStyles.Any, CultureInfo.InvariantCulture, out cx);
                Int32.TryParse(f[17], NumberStyles.Any, CultureInfo.InvariantCulture, out cz);

                string blocks = f[18] ?? "";
                if (blocks.Length > 70) { blocks = blocks.Substring(0, 70) + "…"; }

                raidRows.Add(new[]
                {
                    TicksToLocal(f[9]),
                    f[5] + (string.IsNullOrEmpty(f[4]) ? "" : " [" + f[4] + "]"),
                    f[7],
                    f[8] == "1" ? "игрок" : "зомби/животное",
                    hits.ToString(CultureInfo.InvariantCulture),
                    damage.ToString(CultureInfo.InvariantCulture),
                    destroyed.ToString(CultureInfo.InvariantCulture),
                    blocks,
                    cx.ToString(CultureInfo.InvariantCulture) + ", " + cz.ToString(CultureInfo.InvariantCulture),
                    f[14] == "1" ? "да" : "—",
                    f[15] == "1" ? "был" : "офлайн",
                });
            }
            raidRows.Reverse();   // свежие сверху
        }

        // BlockRaidLog.txt (наш мод): одна строка на удар по блоку чужого лендклейма.
        private void ReadBlockRaids(string file)
        {
            raidHeaders = new[] { "Время", "Атакующий", "Тип сущности", "Позиция", "Блок", "Урон", "Клейм владельца" };

            foreach (string line in File.ReadLines(file))
            {
                Match m = BlockRaidRegex.Match(line.Trim());
                if (!m.Success) { continue; }

                raidRows.Add(new[]
                {
                    m.Groups["ts"].Value.Replace('T', ' '),
                    m.Groups["ename"].Value,
                    m.Groups["etype"].Value,
                    m.Groups["pos"].Value.Replace(',', ' '),
                    m.Groups["block"].Value,
                    m.Groups["dmg"].Value,
                    m.Groups["claim"].Value,
                });
            }
            raidRows.Reverse();
        }

        // ticks из мода — это DateTime.Ticks в UTC; мусор в поле не должен ронять вкладку.
        private static string TicksToLocal(string text)
        {
            long ticks;
            if (!long.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out ticks)) { return "—"; }
            if (ticks <= 0 || ticks >= DateTime.MaxValue.Ticks) { return "—"; }
            return new DateTime(ticks, DateTimeKind.Utc).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        // Список перестраивается из уже прочитанных строк, поэтому фильтр не трогает диск.
        private void FillRaidsList()
        {
            if (raidsList == null || raidsList.IsDisposed) { return; }

            raidsList.BeginUpdate();
            try
            {
                raidsList.Items.Clear();
                raidsList.Columns.Clear();
                if (raidHeaders.Length == 0) { return; }

                int[] widths = new int[raidHeaders.Length];
                for (int i = 0; i < widths.Length; i++) { widths[i] = 120; }
                int[] preferred = { 130, 150, 150, 100, 60, 70, 90, 240, 110, 70, 100 };
                for (int i = 0; i < widths.Length && i < preferred.Length; i++) { widths[i] = preferred[i]; }
                for (int i = 0; i < raidHeaders.Length; i++)
                {
                    raidsList.Columns.Add(raidHeaders[i], widths[i]);
                }

                string filter = (raidsFilter == null ? "" : raidsFilter.Text.Trim());
                bool playersOnly = raidsPlayersOnly != null && raidsPlayersOnly.Checked;

                foreach (string[] row in raidRows)
                {
                    if (playersOnly && !LooksLikePlayer(row)) { continue; }
                    if (filter.Length > 0 && !RowMatches(row, filter)) { continue; }
                    ListViewItem item = new ListViewItem(row);
                    // Игровые атаки (живые игроки) — самое важное: подсвечиваем их акцентом,
                    // зомби и животных оставляем обычным текстом.
                    item.ForeColor = LooksLikePlayer(row) ? Theme.Accent : Theme.TextDim;
                    raidsList.Items.Add(item);
                }
            }
            finally
            {
                raidsList.EndUpdate();
            }
        }

        // В разных источниках признак игрока стоит в разных колонках, поэтому проверяем
        // весь набор ячеек: у Chaotic это «игрок», у нашего лога — префикс EntityPlayer.
        private static bool LooksLikePlayer(string[] row)
        {
            foreach (string cell in row)
            {
                if (string.Equals(cell, "игрок", StringComparison.OrdinalIgnoreCase)) { return true; }
                if (cell.StartsWith("EntityPlayer", StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        private static bool RowMatches(string[] row, string filter)
        {
            foreach (string cell in row)
            {
                if (!string.IsNullOrEmpty(cell) &&
                    cell.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
            }
            return false;
        }
    }
}
