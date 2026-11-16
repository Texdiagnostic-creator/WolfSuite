using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace Wolf.Server
{
    // Config tab: editable serverconfig.xml fields grouped like the game groups them,
    // plus a raw-text escape hatch for options we do not surface.
    public sealed partial class ServerForm
    {
        private TextBox cfgRaw;
        private bool cfgDirty;
        private readonly Dictionary<string, Control> cfgInputs = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);
        private Label cfgStatus;
        private ComboBox cfgWorld;

        private TabPage BuildConfigTab()
        {
            TabPage page = NewTab("serverconfig.xml");

            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 38;
            bar.BackColor = Theme.Bg;
            page.Controls.Add(bar);

            cfgStatus = Theme.Dim("конфиг не загружен", 4, 10);
            bar.Controls.Add(cfgStatus);

            Button save = Theme.Button("Сохранить", 640, 3, 120, 30, delegate { SaveConfig(); }, true);
            save.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bar.Controls.Add(save);

            Button reload = Theme.Button("Перечитать", 770, 3, 120, 30, delegate { LoadConfigIntoUi(); }, false);
            reload.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bar.Controls.Add(reload);

            TabControl inner = new TabControl();
            inner.Dock = DockStyle.Fill;
            inner.Padding = new Point(12, 4);
            page.Controls.Add(inner);
            page.Controls.SetChildIndex(inner, 0);

            inner.TabPages.Add(ConfigGroup("Основное", ServerConfig.GeneralKeys));
            inner.TabPages.Add(ConfigGroup("Сеть и слоты", ServerConfig.NetworkKeys));
            inner.TabPages.Add(ConfigGroup("Мир и сложность", ServerConfig.WorldKeys));
            inner.TabPages.Add(ConfigGroup("Админ и порты", ServerConfig.AdminKeys));

            TabPage raw = NewTab("Все параметры");
            cfgRaw = new TextBox();
            cfgRaw.Multiline = true;
            cfgRaw.ScrollBars = ScrollBars.Both;
            cfgRaw.WordWrap = false;
            cfgRaw.Dock = DockStyle.Fill;
            cfgRaw.Font = new Font("Consolas", 9f);
            cfgRaw.BackColor = Theme.BgInput;
            cfgRaw.ForeColor = Theme.Text;
            cfgRaw.BorderStyle = BorderStyle.None;
            cfgRaw.TextChanged += delegate { cfgDirty = true; UpdateConfigStatus(); };
            raw.Controls.Add(cfgRaw);
            inner.TabPages.Add(raw);

            return page;
        }

        private TabPage ConfigGroup(string caption, string[] keys)
        {
            TabPage page = NewTab(caption);
            Panel scroll = new Panel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            scroll.BackColor = Theme.Bg;
            page.Controls.Add(scroll);

            int y = 10;
            foreach (string key in keys)
            {
                Label name = Theme.Label(key, 12, y + 3);
                name.Width = 210;
                scroll.Controls.Add(name);

                ConfigProp probe = app.Config.Find(key);
                string comment = probe == null ? "" : probe.Comment;

                Control input = MakeInput(key, probe, scroll, y);
                input.Location = new Point(232, y);
                input.Width = 320;
                scroll.Controls.Add(input);
                cfgInputs[key] = input;

                if (!string.IsNullOrEmpty(comment))
                {
                    Label hint = Theme.Dim(comment, 566, y + 4);
                    hint.MaximumSize = new Size(520, 0);
                    scroll.Controls.Add(hint);
                }
                y += 34;
            }

            // World picker: existing worlds only, so a typo cannot produce an empty map.
            if (caption == "Мир и сложность")
            {
                cfgWorld = new ComboBox();
                cfgWorld.DropDownStyle = ComboBoxStyle.DropDown;
                cfgWorld.BackColor = Theme.BgInput;
                cfgWorld.ForeColor = Theme.Text;
                cfgWorld.FlatStyle = FlatStyle.Flat;
                cfgWorld.Location = new Point(232, y);
                cfgWorld.Width = 320;
                scroll.Controls.Add(cfgWorld);
                Label worldLabel = Theme.Label("известные миры", 12, y + 3);
                worldLabel.Width = 210;
                scroll.Controls.Add(worldLabel);

                Button apply = Theme.Button("подставить", 566, y - 2, 110, 26, delegate
                {
                    if (cfgWorld.SelectedItem == null) { return; }
                    Control world;
                    if (cfgInputs.TryGetValue("GameWorld", out world))
                    {
                        world.Text = Convert.ToString(cfgWorld.SelectedItem);
                        cfgDirty = true;
                        UpdateConfigStatus();
                    }
                }, false);
                scroll.Controls.Add(apply);
            }

            return page;
        }

        private Control MakeInput(string key, ConfigProp probe, Control parent, int y)
        {
            string value = probe == null ? "" : probe.Value;

            if (key.Equals("ServerVisibility", StringComparison.OrdinalIgnoreCase))
            {
                ComboBox cb = new ComboBox();
                cb.DropDownStyle = ComboBoxStyle.DropDownList;
                cb.BackColor = Theme.BgInput;
                cb.ForeColor = Theme.Text;
                cb.FlatStyle = FlatStyle.Flat;
                cb.Items.AddRange(new object[] { "0 — не в списке", "1 — только друзьям", "2 — публичный" });
                int v;
                int.TryParse(value, out v);
                cb.SelectedIndex = Math.Max(0, Math.Min(2, v));
                cb.SelectedIndexChanged += delegate { cfgDirty = true; UpdateConfigStatus(); };
                return cb;
            }

            if (key.Equals("PlayerKillingMode", StringComparison.OrdinalIgnoreCase))
            {
                ComboBox cb = new ComboBox();
                cb.DropDownStyle = ComboBoxStyle.DropDownList;
                cb.BackColor = Theme.BgInput;
                cb.ForeColor = Theme.Text;
                cb.FlatStyle = FlatStyle.Flat;
                cb.Items.AddRange(new object[] { "0 — PvE (без убийств)", "1 — только союзники", "2 — только чужие", "3 — все" });
                int v;
                int.TryParse(value, out v);
                cb.SelectedIndex = Math.Max(0, Math.Min(3, v));
                cb.SelectedIndexChanged += delegate { cfgDirty = true; UpdateConfigStatus(); };
                return cb;
            }

            if (key.Equals("GameMode", StringComparison.OrdinalIgnoreCase))
            {
                ComboBox cb = new ComboBox();
                cb.DropDownStyle = ComboBoxStyle.DropDownList;
                cb.BackColor = Theme.BgInput;
                cb.ForeColor = Theme.Text;
                cb.FlatStyle = FlatStyle.Flat;
                cb.Items.AddRange(new object[] { "GameModeSurvival", "GameModeCreative" });
                cb.SelectedItem = value;
                if (cb.SelectedIndex < 0) { cb.SelectedIndex = 0; }
                cb.SelectedIndexChanged += delegate { cfgDirty = true; UpdateConfigStatus(); };
                return cb;
            }

            if (value == "true" || value == "false")
            {
                CheckBox chk = new CheckBox();
                chk.Text = "включено";
                chk.Checked = value == "true";
                chk.ForeColor = Theme.Text;
                chk.BackColor = Color.Transparent;
                chk.CheckedChanged += delegate { cfgDirty = true; UpdateConfigStatus(); };
                return chk;
            }

            if (key.Equals("TelnetPassword", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("ServerPassword", StringComparison.OrdinalIgnoreCase))
            {
                TextBox tb = Theme.Input(0, 0, 320);
                tb.Text = value;
                tb.UseSystemPasswordChar = false;
                tb.TextChanged += delegate { cfgDirty = true; UpdateConfigStatus(); };
                return tb;
            }

            TextBox box = Theme.Input(0, 0, 320);
            box.Text = value;
            box.TextChanged += delegate { cfgDirty = true; UpdateConfigStatus(); };
            return box;
        }

        private void LoadConfigIntoUi()
        {
            string cfg = app.ResolveConfigFile();
            if (cfg == null)
            {
                cfgStatus.Text = "serverconfig.xml не найден";
                cfgStatus.ForeColor = Theme.Err;
                return;
            }
            if (!app.Config.Load(cfg))
            {
                cfgStatus.Text = "не удалось прочитать " + cfg;
                cfgStatus.ForeColor = Theme.Err;
                return;
            }

            foreach (KeyValuePair<string, Control> kv in cfgInputs)
            {
                ConfigProp p = app.Config.Find(kv.Key);
                if (p == null) { continue; }
                Control c = kv.Value;
                if (c is CheckBox) { ((CheckBox)c).Checked = p.Value == "true"; }
                else if (c is ComboBox)
                {
                    ComboBox cb = (ComboBox)c;
                    if (kv.Key.Equals("ServerVisibility", StringComparison.OrdinalIgnoreCase) ||
                        kv.Key.Equals("PlayerKillingMode", StringComparison.OrdinalIgnoreCase))
                    {
                        int v;
                        int.TryParse(p.Value, out v);
                        cb.SelectedIndex = Math.Max(0, Math.Min(cb.Items.Count - 1, v));
                    }
                    else { cb.SelectedItem = p.Value; }
                }
                else { c.Text = p.Value; }
            }

            if (cfgWorld != null)
            {
                cfgWorld.Items.Clear();
                foreach (string w in ServerConfig.KnownWorlds(app.ResolveServerRoot())) { cfgWorld.Items.Add(w); }
            }

            if (cfgRaw != null)
            {
                try { cfgRaw.Text = File.ReadAllText(cfg); } catch { }
            }

            cfgDirty = false;
            UpdateConfigStatus();
        }

        private void UpdateConfigStatus()
        {
            if (cfgStatus == null) { return; }
            string cfg = app.Config.FilePath;
            cfgStatus.Text = (cfgDirty ? "● не сохранено — " : "✓ ") + (cfg ?? "конфиг не загружен");
            cfgStatus.ForeColor = cfgDirty ? Theme.Warn : Theme.TextDim;
        }

        private void SaveConfig()
        {
            if (cfgRaw != null && cfgRaw.Text.Length > 0)
            {
                // Raw view is authoritative: push it back through the parser so the
                // structured tabs stay in sync with whatever was typed.
                string cfg = app.ResolveConfigFile();
                if (cfg == null) { return; }
                try
                {
                    string bak = cfg + ".wolfbak";
                    File.Copy(cfg, bak, true);
                    File.WriteAllText(cfg, cfgRaw.Text, new System.Text.UTF8Encoding(false));
                    app.Config.Load(cfg);
                    app.Log.Add("serverconfig.xml сохранён (" + cfgRaw.Lines.Length + " строк)");
                    cfgDirty = false;
                    UpdateConfigStatus();
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Не удалось сохранить: " + ex.Message, "WolfServer",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            foreach (KeyValuePair<string, Control> kv in cfgInputs)
            {
                Control c = kv.Value;
                if (c is CheckBox) { app.Config.SetBool(kv.Key, ((CheckBox)c).Checked); }
                else if (c is ComboBox)
                {
                    ComboBox cb = (ComboBox)c;
                    if (kv.Key.Equals("ServerVisibility", StringComparison.OrdinalIgnoreCase) ||
                        kv.Key.Equals("PlayerKillingMode", StringComparison.OrdinalIgnoreCase))
                    {
                        app.Config.SetInt(kv.Key, cb.SelectedIndex);
                    }
                    else { app.Config.Set(kv.Key, Convert.ToString(cb.SelectedItem)); }
                }
                else { app.Config.Set(kv.Key, c.Text); }
            }

            string error;
            if (!app.Config.Save(out error))
            {
                MessageBox.Show(this, "Не удалось сохранить: " + error, "WolfServer",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            app.Log.Add("serverconfig.xml сохранён");
            cfgDirty = false;
            UpdateConfigStatus();
            lblServerName.Text = app.ResolveServerRoot() + "   (" + app.Config.Get("ServerName", "") + ")";
        }
    }
}
