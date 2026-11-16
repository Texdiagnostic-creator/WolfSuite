using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Wolf.Server
{
    // Scheduler tab: daily restart / backup / broadcast tasks.
    public sealed partial class ServerForm
    {
        private ListView scheduleList;
        private NumericUpDown schedHour;
        private NumericUpDown schedMinute;
        private ComboBox schedKind;
        private TextBox schedText;
        private NumericUpDown schedWarn;
        private ComboBox schedMode;
        private NumericUpDown schedEvery;

        private TabPage BuildScheduleTab()
        {
            TabPage page = NewTab("Расписание");

            Panel top = Theme.Card(10, 8, 1090, 120);
            top.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(top);

            top.Controls.Add(Theme.Label("Ежедневные задачи", 14, 10).Bold());

            schedKind = new ComboBox();
            schedKind.DropDownStyle = ComboBoxStyle.DropDownList;
            schedKind.BackColor = Theme.BgInput;
            schedKind.ForeColor = Theme.Text;
            schedKind.FlatStyle = FlatStyle.Flat;
            schedKind.Location = new Point(14, 40);
            schedKind.Width = 220;
            schedKind.Items.AddRange(new object[] { "Перезапуск сервера", "Бэкап сохранений", "Сообщение в чат" });
            schedKind.SelectedIndex = 0;
            top.Controls.Add(schedKind);

            schedHour = new NumericUpDown();
            schedHour.Minimum = 0;
            schedHour.Maximum = 23;
            schedHour.Value = 4;
            schedHour.Location = new Point(246, 40);
            schedHour.Width = 60;
            schedHour.BackColor = Theme.BgInput;
            schedHour.ForeColor = Theme.Text;
            top.Controls.Add(schedHour);

            schedMinute = new NumericUpDown();
            schedMinute.Minimum = 0;
            schedMinute.Maximum = 59;
            schedMinute.Value = 0;
            schedMinute.Location = new Point(314, 40);
            schedMinute.Width = 60;
            schedMinute.BackColor = Theme.BgInput;
            schedMinute.ForeColor = Theme.Text;
            top.Controls.Add(schedMinute);

            schedWarn = new NumericUpDown();
            schedWarn.Minimum = 0;
            schedWarn.Maximum = 60;
            schedWarn.Value = 5;
            schedWarn.Location = new Point(386, 40);
            schedWarn.Width = 60;
            schedWarn.BackColor = Theme.BgInput;
            schedWarn.ForeColor = Theme.Text;
            top.Controls.Add(schedWarn);
            top.Controls.Add(Theme.Dim("мин. предупреждение", 452, 44));

            // Режим: ежедневно в HH:MM либо каждые N минут (например «сообщение раз в час»).
            schedMode = new ComboBox();
            schedMode.DropDownStyle = ComboBoxStyle.DropDownList;
            schedMode.BackColor = Theme.BgInput;
            schedMode.ForeColor = Theme.Text;
            schedMode.FlatStyle = FlatStyle.Flat;
            schedMode.Location = new Point(578, 40);
            schedMode.Width = 150;
            schedMode.Items.AddRange(new object[] { "ежедневно в", "каждые" });
            schedMode.SelectedIndex = 0;
            top.Controls.Add(schedMode);

            schedEvery = new NumericUpDown();
            schedEvery.Minimum = 1;
            schedEvery.Maximum = 1440;
            schedEvery.Value = 60;
            schedEvery.Location = new Point(736, 40);
            schedEvery.Width = 60;
            schedEvery.BackColor = Theme.BgInput;
            schedEvery.ForeColor = Theme.Text;
            schedEvery.Enabled = false;
            top.Controls.Add(schedEvery);
            top.Controls.Add(Theme.Dim("мин", 800, 44));

            schedMode.SelectedIndexChanged += delegate
            {
                bool interval = schedMode.SelectedIndex == 1;
                schedEvery.Enabled = interval;
                schedHour.Enabled = !interval;
                schedMinute.Enabled = !interval;
            };

            schedText = Theme.Input(14, 76, 560);
            schedText.Text = "Сервер перезапустится через 5 минут";
            top.Controls.Add(schedText);

            Button add = Theme.Button("Добавить", 960, 38, 118, 30, delegate { AddScheduleTask(); }, true);
            top.Controls.Add(add);

            scheduleList = Theme.StyleList(new ListView());
            scheduleList.View = View.Details;
            scheduleList.FullRowSelect = true;
            scheduleList.Location = new Point(10, 138);
            scheduleList.Size = new Size(1090, 360);
            scheduleList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            scheduleList.Columns.Add("Задача", 800);
            scheduleList.Columns.Add("Вкл", 80);
            scheduleList.Columns.Add("Последний запуск", 180);
            page.Controls.Add(scheduleList);

            Button toggle = Theme.Button("Вкл/Выкл", 10, 508, 140, 32, delegate { ToggleScheduleTask(); }, false);
            toggle.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            page.Controls.Add(toggle);

            Button remove = Theme.Button("Удалить", 160, 508, 120, 32, delegate { RemoveScheduleTask(); }, false);
            remove.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            page.Controls.Add(remove);

            // Список заполнялся только при добавлении/правке задачи, поэтому после запуска
            // панели вкладка выглядела пустой, хотя задачи уже загружены из schedule.json.
            page.Enter += delegate { RefreshSchedule(); };

            return page;
        }

        private void AddScheduleTask()
        {
            ScheduleTask task = new ScheduleTask();
            task.Kind = (ScheduleKind)schedKind.SelectedIndex;
            task.Hour = (int)schedHour.Value;
            task.Minute = (int)schedMinute.Value;
            task.WarnMinutes = (int)schedWarn.Value;
            task.Text = schedText.Text;
            task.EveryMinutes = schedMode.SelectedIndex == 1 ? (int)schedEvery.Value : 0;
            app.Scheduler.Add(task);
            RefreshSchedule();
            app.Log.Add("добавлена задача: " + task.Describe());
        }

        private void ToggleScheduleTask()
        {
            if (scheduleList.SelectedItems.Count == 0) { return; }
            ScheduleTask task = scheduleList.SelectedItems[0].Tag as ScheduleTask;
            if (task == null) { return; }
            task.Enabled = !task.Enabled;
            app.Scheduler.Save();
            RefreshSchedule();
        }

        private void RemoveScheduleTask()
        {
            if (scheduleList.SelectedItems.Count == 0) { return; }
            ScheduleTask task = scheduleList.SelectedItems[0].Tag as ScheduleTask;
            if (task == null) { return; }
            app.Scheduler.Remove(task);
            RefreshSchedule();
        }

        // Для ежедневной задачи — дата, для интервальной — время последнего запуска (локальное).
        private static string LastRunLabel(ScheduleTask t)
        {
            if (t.IsInterval)
            {
                DateTime utc;
                if (!DateTime.TryParseExact(t.LastRunAt, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc))
                {
                    return "—";
                }
                return utc.ToLocalTime().ToString("HH:mm dd.MM", CultureInfo.InvariantCulture);
            }
            return string.IsNullOrEmpty(t.LastRunDay) ? "—" : t.LastRunDay;
        }

        private void RefreshSchedule()
        {
            this.Ui(delegate
            {
                scheduleList.Items.Clear();
                foreach (ScheduleTask t in app.Scheduler.Tasks)
                {
                    ListViewItem item = new ListViewItem(new[]
                    {
                        t.Describe(),
                        t.Enabled ? "да" : "нет",
                        LastRunLabel(t),
                    });
                    item.Tag = t;
                    scheduleList.Items.Add(item);
                }
            });
        }
    }
}
