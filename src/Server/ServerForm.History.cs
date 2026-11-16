using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace Wolf.Server
{
    // History of published packs: every manifest is snapshotted, so the admin can see what
    // changed and serve an older version again without touching the Mods folder.
    public sealed partial class ServerForm
    {
        private Label historySummary;
        private ListView historyList;

        private TabPage BuildHistoryTab()
        {
            TabPage page = NewTab("История наборов");

            Panel top = Theme.Card(10, 8, 1090, 110);
            top.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(top);

            top.Controls.Add(Theme.Label("История опубликованных наборов", 14, 8).Bold());
            historySummary = Theme.Dim("", 14, 40);
            top.Controls.Add(historySummary);
            top.Controls.Add(Theme.Dim("Выберите версию и нажмите «Опубликовать снова», чтобы вернуть её игрокам.", 14, 68));

            Button refresh = Theme.Button("Обновить", 706, 12, 120, 30, delegate { RefreshHistory(); }, false);
            top.Controls.Add(refresh);
            Button republish = Theme.Button("Опубликовать снова", 836, 12, 180, 30, delegate { RepublishSelected(); }, true);
            top.Controls.Add(republish);

            historyList = Theme.StyleList(new ListView());
            historyList.View = View.Details;
            historyList.FullRowSelect = true;
            historyList.MultiSelect = false;
            historyList.Location = new Point(10, 128);
            historyList.Size = new Size(1090, 490);
            historyList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            historyList.Columns.Add("Версия", 90);
            historyList.Columns.Add("Опубликован", 150);
            historyList.Columns.Add("Файлов", 70);
            historyList.Columns.Add("Размер", 90);
            historyList.Columns.Add("Что изменилось", 500);
            historyList.Columns.Add("Хэш", 90);
            historyList.DoubleClick += delegate { RepublishSelected(); };
            page.Controls.Add(historyList);

            RefreshHistory();
            return page;
        }

        private void RefreshHistory()
        {
            if (historyList == null) { return; }
            PackHistory history = app.History;
            historyList.Items.Clear();
            foreach (PackEntry e in history.Entries)
            {
                ListViewItem item = new ListViewItem(new[]
                {
                    string.IsNullOrEmpty(e.version) ? "?" : e.version,
                    e.built,
                    e.files.ToString(CultureInfo.InvariantCulture),
                    GamePaths.HumanSize(e.bytes),
                    ManifestDiff.Summary(e.changes),
                    e.hash,
                });
                item.Tag = e;
                bool current = app.Manifest != null &&
                               string.Equals(app.Manifest.ContentHash, e.hash, StringComparison.OrdinalIgnoreCase);
                item.ForeColor = current ? Theme.Ok : Theme.Text;
                historyList.Items.Add(item);
            }
            PackEntry newest = history.Entries.Count > 0 ? history.Entries[0] : null;
            historySummary.Text = "версий в истории: " + history.Entries.Count.ToString(CultureInfo.InvariantCulture) +
                                  (newest == null ? "" : "   последняя: " + newest.version + " от " + newest.built) +
                                  "   (файлы: " + Path.GetDirectoryName(app.HistoryFile) + ")";
            if (historyList.Items.Count > 0) { historyList.Items[0].Selected = true; }
        }

        private void RepublishSelected()
        {
            if (historyList.SelectedItems.Count == 0)
            {
                MessageBox.Show(this, "Выберите набор в списке.", "WolfServer",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            PackEntry entry = historyList.SelectedItems[0].Tag as PackEntry;
            if (entry == null) { return; }

            string snapshot = PackHistory.SnapshotFile(app.PacksFolder(), entry.hash);
            if (!File.Exists(snapshot))
            {
                MessageBox.Show(this, "Снимок этого набора не найден:\n" + snapshot, "WolfServer",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ModManifest manifest;
            try { manifest = ModManifest.FromJson(File.ReadAllText(snapshot)); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось прочитать снимок набора:\n" + ex.Message, "WolfServer",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (manifest.files.Count == 0)
            {
                MessageBox.Show(this, "Снимок набора пуст.", "WolfServer",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // The manifest only makes sense while its chunks are still in the store: they are
            // the payload players download. Chunks are content-addressed and never deleted by
            // a re-publish, but the admin could have cleared the folder manually.
            int missing = 0;
            foreach (ManifestFile f in manifest.files)
            {
                foreach (ChunkRef c in f.chunks)
                {
                    if (!app.Store.Has(c.sha, c.size)) { missing++; }
                }
            }
            if (missing > 0)
            {
                MessageBox.Show(this,
                    "В хранилище не хватает чанков: " + missing.ToString(CultureInfo.InvariantCulture) + ".\n\n" +
                    "Этот набор нельзя раздать без повторной публикации из папки Mods.",
                    "WolfServer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                app.Manifest = manifest;
                Directory.CreateDirectory(app.PublishFolder());
                Json.WriteFile(Path.Combine(app.PublishFolder(), "manifest.json"), manifest.ToJson(), true);
                WriteServerInfo(manifest, entry.changes);
                StartServing(manifest);
                app.Log.Add("опубликован старый набор " + entry.version + " (" + entry.hash + ")");
            }
            catch (Exception ex)
            {
                app.Log.Add("не удалось опубликовать старый набор: " + ex.Message);
                MessageBox.Show(this, "Ошибка публикации:\n" + ex.Message, "WolfServer",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
