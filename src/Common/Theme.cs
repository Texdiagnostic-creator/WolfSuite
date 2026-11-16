using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Wolf
{
    // Shared dark palette + helpers so both apps look like one product.
    public static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(24, 26, 31);
        public static readonly Color BgPanel = Color.FromArgb(31, 34, 41);
        public static readonly Color BgInput = Color.FromArgb(38, 42, 50);
        public static readonly Color BgHover = Color.FromArgb(45, 50, 60);
        public static readonly Color Line = Color.FromArgb(56, 62, 74);
        public static readonly Color Text = Color.FromArgb(223, 228, 236);
        public static readonly Color TextDim = Color.FromArgb(146, 155, 168);
        public static readonly Color Accent = Color.FromArgb(214, 122, 47);      // 7DTD rust
        public static readonly Color AccentDark = Color.FromArgb(163, 90, 32);
        public static readonly Color Ok = Color.FromArgb(96, 190, 120);
        public static readonly Color Warn = Color.FromArgb(232, 188, 74);
        public static readonly Color Err = Color.FromArgb(226, 92, 84);
        public static readonly Color Info = Color.FromArgb(96, 160, 226);

        public const string FontName = "Segoe UI";

        public static Font Font(float size, FontStyle style = FontStyle.Regular)
        {
            return new Font(FontName, size, style, GraphicsUnit.Point);
        }

        public static void Apply(Form form)
        {
            form.BackColor = Bg;
            form.ForeColor = Text;
            form.Font = Font(9f);
            try { form.DoubleBuffered(true); } catch { }
            ApplyRecursive(form);
        }

        public static void ApplyRecursive(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                ApplyOne(c);
                if (c.HasChildren) { ApplyRecursive(c); }
            }
        }

        private static void ApplyOne(Control c)
        {
            if (c is Form) { return; }

            if (c is Button)
            {
                Button b = (Button)c;
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = BgPanel;
                b.ForeColor = Text;
                b.FlatAppearance.BorderColor = Line;
                b.FlatAppearance.MouseOverBackColor = BgHover;
                b.FlatAppearance.MouseDownBackColor = AccentDark;
                return;
            }
            if (c is Label)
            {
                c.BackColor = Color.Transparent;
                if (c.ForeColor == SystemColors.ControlText) { c.ForeColor = Text; }
                return;
            }
            if (c is CheckBox)
            {
                c.BackColor = Color.Transparent;
                c.ForeColor = Text;
                return;
            }
            if (c is TextBox || c is ComboBox || c is NumericUpDown || c is ListBox || c is ListView || c is RichTextBox)
            {
                c.BackColor = BgInput;
                c.ForeColor = Text;
                if (c is ComboBox) { ((ComboBox)c).FlatStyle = FlatStyle.Flat; }
                if (c is ListView)
                {
                    ListView lv = (ListView)c;
                    lv.BackColor = BgInput;
                    lv.ForeColor = Text;
                    lv.BorderStyle = BorderStyle.FixedSingle;
                }
                if (c is ListBox) { ((ListBox)c).BorderStyle = BorderStyle.FixedSingle; }
                if (c is TextBox) { ((TextBox)c).BorderStyle = BorderStyle.FixedSingle; }
                return;
            }
            if (c is TabControl)
            {
                c.BackColor = Bg;
                return;
            }
            if (c is GroupBox)
            {
                c.ForeColor = Text;
                return;
            }
            if (c is ProgressBar)
            {
                c.BackColor = BgInput;
                return;
            }
            if (c is Panel || c is SplitContainer || c is TableLayoutPanel || c is FlowLayoutPanel)
            {
                if (c.BackColor == SystemColors.Control) { c.BackColor = BgPanel; }
            }
        }

        // ListView не наследует цвет фона от панели, а Apply() вызывается до того, как
        // построены вкладки, поэтому списки оставались системно-белыми. Светлый текст
        // (Theme.Text у статистики, Theme.Ok у истории) на белом не читался вообще.
        public static ListView StyleList(ListView list)
        {
            list.BackColor = BgInput;
            list.ForeColor = Text;
            list.BorderStyle = BorderStyle.FixedSingle;
            return list;
        }

        public static Button Button(string text, int x, int y, int w, int h, EventHandler onClick, bool accent)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, h);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = accent ? AccentDark : Line;
            b.BackColor = accent ? AccentDark : BgPanel;
            b.ForeColor = Color.White;
            if (onClick != null) { b.Click += onClick; }
            return b;
        }

        public static Label Label(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.AutoSize = true;
            l.BackColor = Color.Transparent;
            l.ForeColor = Text;
            return l;
        }

        public static Label Dim(string text, int x, int y)
        {
            Label l = Label(text, x, y);
            l.ForeColor = TextDim;
            return l;
        }

        public static Label Title(string text, int x, int y)
        {
            Label l = Label(text, x, y);
            l.Font = Font(12f, FontStyle.Bold);
            l.ForeColor = Accent;
            return l;
        }

        public static TextBox Input(int x, int y, int w)
        {
            TextBox t = new TextBox();
            t.Location = new Point(x, y);
            t.Width = w;
            t.BackColor = BgInput;
            t.ForeColor = Text;
            t.BorderStyle = BorderStyle.FixedSingle;
            return t;
        }

        public static Panel Card(int x, int y, int w, int h)
        {
            Panel p = new Panel();
            p.Location = new Point(x, y);
            p.Size = new Size(w, h);
            p.BackColor = BgPanel;
            p.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Line)) { e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1); }
            };
            return p;
        }

        public static void SetStatus(Label label, string text, Color color)
        {
            label.Text = text;
            label.ForeColor = color;
        }

        // Fills a progress bar from a worker thread without deadlocking the UI.
        public static void ReportProgress(Control owner, ProgressBar bar, int percent, Label caption, string text)
        {
            if (owner == null || owner.IsDisposed) { return; }
            if (owner.InvokeRequired)
            {
                owner.BeginInvoke(new Action(delegate { ReportProgress(owner, bar, percent, caption, text); }));
                return;
            }
            if (bar != null && !bar.IsDisposed) { bar.Value = Math.Max(0, Math.Min(100, percent)); }
            if (caption != null && !caption.IsDisposed) { caption.Text = text; }
        }
    }

    public static class ControlExt
    {
        public static void DoubleBuffered(this Control c, bool value)
        {
            System.Reflection.PropertyInfo pi = typeof(Control).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (pi != null) { pi.SetValue(c, value, null); }
        }

        // Marshals an action to the UI thread; safe to call from any worker.
        public static void Ui(this Control c, Action action)
        {
            if (c == null || c.IsDisposed) { return; }
            if (c.InvokeRequired) { c.BeginInvoke(action); }
            else { action(); }
        }

        public static Label Bold(this Label l)
        {
            l.Font = Theme.Font(10f, FontStyle.Bold);
            l.ForeColor = Theme.Text;
            return l;
        }
    }

    // Rolling text log shared by both apps: file + in-memory ring for the UI.
    public sealed class Log
    {
        private readonly object gate = new object();
        private readonly Queue<string> ring = new Queue<string>();
        private readonly string file;
        private readonly int capacity;

        public Log(string folder, string name, int capacity)
        {
            this.capacity = capacity;
            Directory.CreateDirectory(folder);
            file = Path.Combine(folder, name);
        }

        public string FilePath { get { return file; } }

        public void Add(string message)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] " + message;
            lock (gate)
            {
                ring.Enqueue(line);
                while (ring.Count > capacity) { ring.Dequeue(); }
                try { File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8); } catch { }
            }
            if (OnLine != null) { OnLine(line); }
        }

        public event Action<string> OnLine;

        public string[] Snapshot()
        {
            lock (gate) { return ring.ToArray(); }
        }
    }
}
