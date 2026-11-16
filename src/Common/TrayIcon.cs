using System;
using System.Drawing;
using System.Windows.Forms;

namespace Wolf
{
    // Minimise-to-tray shared by both apps. The window leaves the taskbar, the icon stays in
    // the notification area, and a double click (or the menu) brings the window back.
    // Closing the window keeps its usual meaning: the app runs its own shutdown logic.
    public sealed class TrayIcon : IDisposable
    {
        private readonly Form form;
        private readonly string title;
        private readonly string hint;
        private readonly NotifyIcon icon;
        private bool hidden;
        private bool balloonShown;
        private bool disposed;

        public TrayIcon(Form form, string title, string hint)
        {
            this.form = form;
            this.title = title;
            this.hint = hint;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(Item("Открыть окно", delegate { Restore(); }));
            menu.Items.Add(Item("Свернуть в трей", delegate { MinimizeToTray(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Выход", delegate { Exit(); }));

            icon = new NotifyIcon();
            icon.Text = Truncate(title, 63);   // NotifyIcon.Text is limited to 63 characters
            icon.Icon = AppIcon();
            icon.ContextMenuStrip = menu;
            icon.DoubleClick += delegate { Restore(); };
            icon.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) { Restore(); }
            };
            // Shown later: when the launcher is started by Windows at login, the notification
            // area may not be ready yet and an icon created too early never shows up.
            icon.Visible = false;

            form.Resize += delegate(object s, EventArgs e) { OnFormResize(); };
            form.Shown += delegate { ShowIcon(); };
            form.FormClosed += delegate { Dispose(); };
        }

        public bool IsHidden { get { return hidden; } }

        private static ToolStripItem Item(string text, EventHandler onClick)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += onClick;
            return item;
        }

        private static Icon AppIcon()
        {
            try
            {
                Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) { return ico; }
            }
            catch { }
            return SystemIcons.Application;
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) { return "Wolf"; }
            return text.Length <= max ? text : text.Substring(0, max);
        }

        private void OnFormResize()
        {
            if (form.WindowState != FormWindowState.Minimized) { return; }
            // Hiding inside the Resize handler upsets the message loop; do it on the next turn.
            try { form.BeginInvoke(new Action(DoHide)); } catch { }
        }

        // Public so a window can send itself to the tray (the launcher does it when the game starts).
        public void MinimizeToTray()
        {
            if (disposed || form.IsDisposed) { return; }
            form.WindowState = FormWindowState.Minimized;
            DoHide();
        }

        private void ShowIcon()
        {
            try { icon.Visible = true; } catch { }
        }

        private void DoHide()
        {
            if (disposed || hidden || form.IsDisposed) { return; }
            hidden = true;
            form.ShowInTaskbar = false;
            form.Hide();
            ShowIcon();   // re-assert: the shell sometimes drops an icon created during login
            if (!balloonShown)
            {
                balloonShown = true;
                try
                {
                    icon.BalloonTipTitle = Truncate(title, 63);
                    icon.BalloonTipText = hint;
                    icon.BalloonTipIcon = ToolTipIcon.Info;
                    icon.ShowBalloonTip(5000);
                }
                catch { }
            }
        }

        public void Restore()
        {
            if (disposed || form.IsDisposed) { return; }
            hidden = false;
            form.ShowInTaskbar = true;
            form.Show();
            if (form.WindowState == FormWindowState.Minimized) { form.WindowState = FormWindowState.Normal; }
            form.Activate();
            form.BringToFront();
        }

        public void Exit()
        {
            // Restore first: WolfServer asks "остановить сервер?" and that dialog needs a
            // visible owner, otherwise it can end up behind other windows.
            Restore();
            form.Close();
        }

        public void Dispose()
        {
            if (disposed) { return; }
            disposed = true;
            try { icon.Visible = false; icon.Dispose(); } catch { }
        }
    }
}
