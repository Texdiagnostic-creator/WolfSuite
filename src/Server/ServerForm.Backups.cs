using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Wolf.Server
{
    // Backups tab: manual backup, list, restore, delete, retention.
    public sealed partial class ServerForm
    {
        private ListView backupList;
        private Label backupStatus;
        private ComboBox backupWorld;

        private TabPage BuildBackupsTab()
        {
            TabPage page = NewTab("Бэкапы");

            Panel top = Theme.Card(10, 8, 1090, 92);
            top.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(top);

            top.Controls.Add(Theme.Label("Резервные копии сохранений", 14, 10).Bold());

            backupWorld = new ComboBox();
            backupWorld.DropDownStyle = ComboBoxStyle.DropDownList;
            backupWorld.BackColor = Theme.BgInput;
            backupWorld.ForeColor = Theme.Text;
            backupWorld.FlatStyle = FlatStyle.Flat;
            backupWorld.Location = new Point(14, 44);
            backupWorld.Width = 260;
            top.Controls.Add(backupWorld);

            Button refresh = Theme.Button("Обновить список", 286, 42, 150, 28, delegate { RefreshBackups(); }, false);
            top.Controls.Add(refresh);
            Button makeNow = Theme.Button("Сделать бэкап", 446, 42, 150, 28, delegate { BackupNow(); }, true);
            top.Controls.Add(makeNow);
            Button openFolder = Theme.Button("Открыть папку", 606, 42, 140, 28,
                delegate { app.OpenFolder(app.Backups.Root); }, false);
            top.Controls.Add(openFolder);

            backupStatus = Theme.Dim("", 760, 48);
            top.Controls.Add(backupStatus);

            backupList = Theme.StyleList(new ListView());
            backupList.View = View.Details;
            backupList.FullRowSelect = true;
            backupList.MultiSelect = false;
            backupList.Location = new Point(10, 108);
            backupList.Size = new Size(1090, 400);
            backupList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            backupList.Columns.Add("Мир", 200);
            backupList.Columns.Add("Когда", 160);
            backupList.Columns.Add("Размер", 100);
            backupList.Columns.Add("Файл", 620);
            page.Controls.Add(backupList);

            Button restore = Theme.Button("Восстановить выбранный", 10, 520, 240, 32, delegate { RestoreSelected(); }, false);
            restore.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            page.Controls.Add(restore);

            Button remove = Theme.Button("Удалить", 260, 520, 120, 32, delegate { DeleteSelected(); }, false);
            remove.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            page.Controls.Add(remove);

            Label warn = Theme.Dim("Восстановление перезаписывает папку мира — сервер должен быть остановлен.", 400, 528);
            warn.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            page.Controls.Add(warn);

            return page;
        }

        private void RefreshBackups()
        {
            string saves = app.SavesDir();
            List<string> worlds = GamePaths.Worlds(saves);
            string selected = backupWorld.SelectedItem as string;

            this.Ui(delegate
            {
                backupWorld.Items.Clear();
                foreach (string w in worlds) { backupWorld.Items.Add(w); }
                if (selected != null && backupWorld.Items.Contains(selected)) { backupWorld.SelectedItem = selected; }
                else if (backupWorld.Items.Count > 0) { backupWorld.SelectedIndex = 0; }

                backupList.Items.Clear();
                List<BackupManager.BackupItem> items = app.Backups.List(null);
                foreach (BackupManager.BackupItem b in items)
                {
                    ListViewItem item = new ListViewItem(new[]
                    {
                        b.World,
                        b.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
                        GamePaths.HumanSize(b.Size),
                        b.File,
                    });
                    item.Tag = b;
                    backupList.Items.Add(item);
                }
                backupStatus.Text = items.Count + " архивов, " + GamePaths.HumanSize(app.Backups.TotalSize());
            });
        }

        private void BackupNow()
        {
            string saves = app.SavesDir();
            string world = backupWorld.SelectedItem as string;
            if (string.IsNullOrEmpty(world))
            {
                MessageBox.Show(this, "Не найдено ни одного мира в " + saves, "WolfServer",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            backupStatus.Text = "копирование " + world + "...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string zip = app.Backups.BackupWorld(saves, world, "вручную", app.Log.Add);
                this.Ui(delegate
                {
                    backupStatus.Text = zip == null ? "бэкап не удался" : "готово: " + Path.GetFileName(zip);
                    RefreshBackups();
                });
            });
        }

        private void RestoreSelected()
        {
            if (backupList.SelectedItems.Count == 0) { return; }
            BackupManager.BackupItem item = backupList.SelectedItems[0].Tag as BackupManager.BackupItem;
            if (item == null) { return; }

            if (app.Controller.Running)
            {
                MessageBox.Show(this, "Остановите сервер перед восстановлением.", "WolfServer",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show(this,
                "Перезаписать мир «" + item.World + "» из архива\n" + item.File + "?",
                "Восстановление", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            string error;
            if (app.Backups.Restore(item, app.SavesDir(), out error))
            {
                app.Log.Add("восстановлен мир " + item.World + " из " + Path.GetFileName(item.File));
                backupStatus.Text = "восстановлено: " + item.World;
            }
            else
            {
                MessageBox.Show(this, "Не удалось восстановить: " + error, "WolfServer",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DeleteSelected()
        {
            if (backupList.SelectedItems.Count == 0) { return; }
            BackupManager.BackupItem item = backupList.SelectedItems[0].Tag as BackupManager.BackupItem;
            if (item == null) { return; }
            if (MessageBox.Show(this, "Удалить архив?\n" + item.File, "WolfServer",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }
            app.Backups.Delete(item);
            RefreshBackups();
        }
    }
}