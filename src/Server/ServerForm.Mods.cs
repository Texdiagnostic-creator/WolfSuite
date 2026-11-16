using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Wolf.Server
{
    // Mods tab: curate the pack, publish it, watch who has it.
    public sealed partial class ServerForm
    {
        private ListView modsList;
        private Label modsSummary;
        private Label publishState;
        private TextBox publishUrl;
        private ListView peerList;
        private ProgressBar packProgress;
        private Label packProgressText;

        private volatile bool publishing;
        private bool pendingPublish;
        private FileSystemWatcher modsWatcher;
        private System.Windows.Forms.Timer publishDebounce;

        private TabPage BuildModsTab()
        {
            TabPage page = NewTab("Моды и раздача");

            Panel top = Theme.Card(10, 8, 1090, 96);
            top.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(top);

            top.Controls.Add(Theme.Label("Набор модов сервера", 14, 10).Bold());
            modsSummary = Theme.Dim("моды не просканированы", 14, 38);
            top.Controls.Add(modsSummary);

            Button scan = Theme.Button("Сканировать Mods", 760, 12, 160, 30, delegate { ScanMods(); }, false);
            top.Controls.Add(scan);
            Button publish = Theme.Button("Опубликовать", 930, 12, 146, 30, delegate { PublishPack(); }, true);
            top.Controls.Add(publish);
            Button openMods = Theme.Button("Открыть папку", 760, 52, 160, 28,
                delegate { app.OpenFolder(app.ModsSourceDir()); }, false);
            top.Controls.Add(openMods);
            Button rehash = Theme.Button("Пересчитать хеши", 596, 52, 156, 28, delegate
            {
                // Кэш хешей иногда надо сбросить руками: если файл подменили с сохранением
                // даты изменения, старая запись уже не совпадёт с содержимым по подписи,
                // но принудительный пересчёт полезен, когда сомневаешься в наборе.
                app.Hashes.Clear();
                app.Hashes.Save();
                app.Log.Add("кэш хешей сброшен — набор будет перехэширован");
                PublishPack(true);
            }, false);
            top.Controls.Add(rehash);
            Button copyUrl = Theme.Button("Скопировать ссылку", 930, 52, 146, 28, delegate
            {
                try { Clipboard.SetText(publishUrl.Text); app.Log.Add("ссылка скопирована: " + publishUrl.Text); } catch { }
            }, false);
            top.Controls.Add(copyUrl);

            publishState = Theme.Dim("раздача остановлена", 14, 62);
            top.Controls.Add(publishState);

            modsList = Theme.StyleList(new ListView());
            modsList.View = View.Details;
            modsList.FullRowSelect = true;
            modsList.CheckBoxes = false;
            modsList.Location = new Point(10, 112);
            modsList.Size = new Size(560, 380);
            modsList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
            modsList.Columns.Add("Мод", 330);
            modsList.Columns.Add("Версия", 90);
            modsList.Columns.Add("Файлов", 70);
            modsList.Columns.Add("Размер", 80);
            page.Controls.Add(modsList);

            Panel right = Theme.Card(580, 112, 520, 380);
            right.Anchor = AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
            page.Controls.Add(right);

            right.Controls.Add(Theme.Label("Кто раздаёт / у кого есть", 14, 10).Bold());

            peerList = Theme.StyleList(new ListView());
            peerList.View = View.Details;
            peerList.FullRowSelect = true;
            peerList.Location = new Point(14, 36);
            peerList.Size = new Size(492, 290);
            peerList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            peerList.Columns.Add("Имя", 150);
            peerList.Columns.Add("Адрес", 140);
            peerList.Columns.Add("SteamID", 130);
            peerList.Columns.Add("Есть", 60);
            right.Controls.Add(peerList);

            publishUrl = Theme.Input(14, 336, 492);
            publishUrl.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            publishUrl.ReadOnly = true;
            publishUrl.Text = app.DisplayBaseUrl();
            right.Controls.Add(publishUrl);

            packProgress = new ProgressBar();
            packProgress.Location = new Point(10, 500);
            packProgress.Size = new Size(1090, 16);
            packProgress.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            page.Controls.Add(packProgress);

            packProgressText = Theme.Dim("", 10, 520);
            packProgressText.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            page.Controls.Add(packProgressText);

            return page;
        }

        private void ScanMods()
        {
            string dir = app.ModsSourceDir();
            if (dir == null || !Directory.Exists(dir))
            {
                MessageBox.Show(this, "Папка модов не найдена. Укажите её во вкладке «Настройки».",
                    "WolfServer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            modsList.Items.Clear();
            modsSummary.Text = "сканирование...";

            ThreadPool.QueueUserWorkItem(delegate
            {
                int count = 0;
                foreach (string modDir in GamePaths.SafeDirs(dir))
                {
                    string name = Path.GetFileName(modDir);
                    long size = 0;
                    int files = 0;
                    foreach (string f in GamePaths.SafeFiles(modDir, "*", SearchOption.AllDirectories))
                    {
                        try { size += new FileInfo(f).Length; files++; } catch { }
                    }
                    string version = "";
                    string info = Path.Combine(modDir, "ModInfo.xml");
                    if (File.Exists(info))
                    {
                        try
                        {
                            System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
                            doc.Load(info);
                            System.Xml.XmlNode v = doc.SelectSingleNode("//Version");
                            if (v != null) { version = v.InnerText.Trim(); }
                        }
                        catch { }
                    }

                    ListViewItem item = new ListViewItem(new[]
                    {
                        name, version, files.ToString(CultureInfo.InvariantCulture), GamePaths.HumanSize(size),
                    });
                    int index = count++;
                    this.Ui(delegate { modsList.Items.Add(item); });
                }

                this.Ui(delegate
                {
                    modsSummary.Text = count + " модов в " + dir;
                    app.Log.Add("просканировано модов: " + count);
                });
            });
        }

        // Builds the manifest (hashing everything into the chunk store) and starts serving it.
        private void PublishPack()
        {
            PublishPack(false);
        }

        // silent=true is used by the folder watcher: no dialogs, no log spam on no-op changes.
        private void PublishPack(bool silent)
        {
            string dir = app.ModsSourceDir();
            if (dir == null || !Directory.Exists(dir))
            {
                if (!silent)
                {
                    MessageBox.Show(this, "Папка модов не найдена.", "WolfServer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }
            if (publishing)
            {
                pendingPublish = true;
                return;
            }
            publishing = true;
            pendingPublish = false;

            packProgress.Value = 0;
            packProgressText.Text = "хэширование модов...";
            app.Log.Add("публикация набора модов из " + dir);

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    ModFilters filters = ModFilters.ForThisServer(dir, app.Settings.ShipServerOnlyMods);
                    foreach (string src in filters.Sources) { app.Log.Add("[фильтр] " + src); }

                    string error;
                    ModManifest manifest = ModManifest.Build(dir, app.Store, app.Settings.PackName, app.Settings.PackVersion,
                        app.Settings.PackNote, filters, app.Hashes, app.Manifest, delegate(string mod)
                        {
                            this.Ui(delegate { packProgressText.Text = "хэширование: " + mod; });
                        }, out error);
                    app.Hashes.Save();

                    if (error != null)
                    {
                        this.Ui(delegate { packProgressText.Text = error; });
                        app.Log.Add("публикация не удалась: " + error);
                        return;
                    }

                    string newHash = manifest.ContentHash;
                    ModManifest old = app.Manifest;
                    // Набор уже уходил клиентам (в этой сессии или в прошлой) — тогда повторная
                    // публикация ничего не меняет. Это не «моды не менялись»: изменения уже
                    // разъехались, чаще всего автопубликацией из-за слежения за папкой Mods.
                    PackEntry previous = app.History.Find(newHash);
                    bool same = (old != null && old.ContentHash == newHash) || previous != null;
                    List<string> changes = ManifestDiff.Compare(old, manifest);

                    app.Manifest = manifest;
                    string publicDir = app.PublishFolder();
                    Directory.CreateDirectory(publicDir);
                    Json.WriteFile(Path.Combine(publicDir, "manifest.json"), manifest.ToJson(), true);
                    WriteServerInfo(manifest, changes);

                    // Keep a snapshot of every published pack. The manifest is small; the
                    // chunks it refers to are already in the content-addressed store.
                    try
                    {
                        Directory.CreateDirectory(app.PacksFolder());
                        Json.WriteFile(PackHistory.SnapshotFile(app.PacksFolder(), newHash), manifest.ToJson(), true);
                        app.History.Add(manifest, changes);
                        app.History.Save(app.HistoryFile);
                    }
                    catch (Exception ex)
                    {
                        app.Log.Add("история наборов: " + ex.Message);
                    }

                    app.Log.Add(string.Format(CultureInfo.InvariantCulture,
                        "манифест собран: {0} модов, {1} файлов, {2}, хэш {3}{4}",
                        manifest.loadOrder.Count, manifest.files.Count, GamePaths.HumanSize(manifest.TotalBytes), newHash,
                        same
                            ? " — этот набор уже опубликован" +
                              (previous == null || string.IsNullOrEmpty(previous.built) ? "" : " " + previous.built) +
                              ", у клиентов он уже есть"
                            : ""));

                    if (!same)
                    {
                        app.Log.Add("изменения: " + ManifestDiff.Summary(changes));
                    }

                    if (!same && app.Settings.NotifyPack)
                    {
                        app.Notifier.SendAsync("Опубликован новый набор модов: " + manifest.version +
                            " (" + manifest.files.Count + " файлов, " + GamePaths.HumanSize(manifest.TotalBytes) +
                            "), " + ManifestDiff.Summary(changes), app.Log.Add);
                    }

                    this.Ui(delegate { StartServing(manifest, same); });
                }
                catch (Exception ex)
                {
                    app.Log.Add("ошибка публикации: " + ex.Message);
                    this.Ui(delegate { packProgressText.Text = "ошибка: " + ex.Message; });
                }
                finally
                {
                    publishing = false;
                    if (pendingPublish)
                    {
                        pendingPublish = false;
                        PublishPack(true);
                    }
                }
            });
        }

        // server.json is what the launcher shows on its "Что нового" tab.
        private void WriteServerInfo(ModManifest manifest, List<string> changes)
        {
            List<object> list = new List<object>();
            if (changes != null)
            {
                foreach (string s in changes) { list.Add(s); }
            }
            Json.WriteFile(Path.Combine(app.PublishFolder(), "server.json"), new Dictionary<string, object>
            {
                { "name", manifest.serverName },
                { "version", manifest.version },
                { "note", manifest.note },
                { "vk", app.Settings.VkUrl },
                { "world", app.WorldName() },
                { "slots", app.Config.Loaded ? app.Config.GetInt("ServerMaxPlayerCount", 0) : 0 },
                { "port", app.Config.Loaded ? app.Config.GetInt("ServerPort", 26900) : 26900 },
                { "built", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) },
                { "changes", list },
            }, true);
        }

        private void StartServing(ModManifest manifest)
        {
            StartServing(manifest, false);
        }

        private void StartServing(ModManifest manifest, bool unchanged)
        {
            try
            {
                if (app.Http == null)
                {
                    app.Http = new MiniHttp();
                    app.Http.OnError += delegate(string s) { app.Log.Add("[http] " + s); };
                    app.Tracker.Map(app.Http);
                    app.Http.MapFolder("/mods", app.PublishFolder());
                    app.Http.Map("/manifest.json", ctx => ctx.SendFile(Path.Combine(app.PublishFolder(), "manifest.json")));
                    app.Http.Map("/server.json", ctx => ctx.SendFile(Path.Combine(app.PublishFolder(), "server.json")));
                    app.Http.Map("/chunk", ctx =>
                    {
                        string sha = ctx.Query("h") ?? ctx.Path.Substring("/chunk/".Length - 1).TrimStart('/');
                        byte[] data = string.IsNullOrEmpty(sha) ? null : app.Store.Read(sha);
                        if (data == null) { ctx.NotFound(); return; }
                        ctx.WriteBody(200, "application/octet-stream", data);
                    });
                    app.Http.Map("/status", ctx =>
                    {
                        Dictionary<string, object> d = new Dictionary<string, object>();
                        d["name"] = app.Settings.PackName;
                        d["version"] = app.Settings.PackVersion;
                        d["hash"] = app.Manifest == null ? "" : app.Manifest.ContentHash;
                        d["files"] = app.Manifest == null ? 0 : app.Manifest.files.Count;
                        d["peers"] = app.Tracker.All().Count;
                        d["running"] = app.Controller.Running;
                        d["players"] = lblPlayers.Text;
                        ctx.Ok(d);
                    });
                    app.Http.Map("/launcher.json", ctx =>
                    {
                        string info = Path.Combine(app.PublishFolder(), "launcher.json");
                        if (!File.Exists(info)) { ctx.NotFound(); return; }
                        ctx.SendFile(info);
                    });
                    app.Http.Map("/launcher.exe", ctx =>
                    {
                        string exe = app.LauncherFile();
                        if (exe == null) { ctx.NotFound(); return; }
                        ctx.SendFile(exe);
                    });
                    app.Http.Map("/download", ctx =>
                    {
                        // One link for players: a zip with the launcher and its instruction.
                        string zip = app.LauncherZip();
                        if (zip == null) { ctx.NotFound(); return; }
                        ctx.SendFile(zip);
                    });
                    app.Http.Map("/packs", ctx =>
                    {
                        // Small JSON index of published packs (no file contents).
                        List<object> list = new List<object>();
                        foreach (PackEntry e in app.History.Entries)
                        {
                            list.Add(new Dictionary<string, object>
                            {
                                { "version", e.version },
                                { "built", e.built },
                                { "files", e.files },
                                { "bytes", e.bytes },
                                { "hash", e.hash },
                                { "changes", e.changes },
                            });
                        }
                        Dictionary<string, object> d = new Dictionary<string, object>();
                        d["packs"] = list;
                        ctx.Ok(d);
                    });
                    app.Http.Map("/", ctx => ctx.Html(StatusPage.Build(app)));
                    app.Radio.Map(app.Http);
                    app.Http.Bind(app.Settings.PublishPort);
                    app.Log.Add("HTTP раздача на порту " + app.Http.Port);
                }

                if (app.Peer == null)
                {
                    app.Peer = new PeerNode(app.Store);
                    app.Peer.Hash = manifest.ContentHash;
                    app.Peer.DisplayName = app.Settings.PackName;
                    app.Peer.IsSeed = true;
                    if (!app.Peer.Start(app.Settings.PeerPort))
                    {
                        app.Log.Add("не удалось открыть P2P-порт " + app.Settings.PeerPort);
                    }
                    else
                    {
                        app.Log.Add("P2P-порт " + app.Peer.Port);
                        string announceUrl = app.PublicBaseUrl();
                        ThreadPool.QueueUserWorkItem(delegate { try { app.Peer.AnnounceTo(announceUrl, 60); } catch { } });
                    }
                }
                else
                {
                    app.Peer.Hash = manifest.ContentHash;
                }

                publishUrl.Text = app.DisplayBaseUrl();
                publishState.Text = string.Format(CultureInfo.InvariantCulture,
                    "раздача активна: {0} файлов, {1}, хэш {2} · опубликован {3}",
                    manifest.files.Count, GamePaths.HumanSize(manifest.TotalBytes), manifest.ContentHash,
                    manifest.builtUtc.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture));
                publishState.ForeColor = Theme.Ok;
                packProgress.Value = 100;
                packProgressText.Text = unchanged
                    ? "набор не изменился — он уже опубликован, у клиентов актуален"
                    : "опубликовано: " + manifest.files.Count + " файлов";
                lblUrl.Text = "клиентам: " + publishUrl.Text;
                RefreshModsSummary();
                RefreshHistory();
                app.Log.Add("набор опубликован, хэш " + manifest.ContentHash);

                string launcherHash = app.PublishLauncherInfo();
                if (launcherHash != null)
                {
                    string zip = app.LauncherZip();
                    app.Log.Add("лаунчер для раздачи: " + publishUrl.Text + "/download" +
                                (zip == null ? " (zip не собран)" : " (" + GamePaths.HumanSize(new FileInfo(zip).Length) + ")"));
                }
                else
                {
                    app.Log.Add("WolfLauncher.exe не найден — раздача лаунчера отключена");
                }
            }
            catch (Exception ex)
            {
                publishState.Text = "ошибка раздачи: " + ex.Message;
                publishState.ForeColor = Theme.Err;
                app.Log.Add("ошибка раздачи: " + ex.Message);
            }
        }

        private void RefreshModsSummary()
        {
            ModManifest m = app.Manifest;
            if (m == null)
            {
                modsSummary.Text = "набор не опубликован — нажмите «Опубликовать»";
                lblMods.Text = "модов в раздаче: —";
                return;
            }
            string text = m.files.Count + " файлов, " + GamePaths.HumanSize(m.TotalBytes) + ", хэш " + m.ContentHash;
            modsSummary.Text = text;
            lblMods.Text = "модов в раздаче: " + m.loadOrder.Count + "  (" + text + ")";
        }

        private void RefreshPeers()
        {
            if (peerList == null) { return; }
            List<PeerInfo> peers = app.Tracker.All();
            this.Ui(delegate
            {
                peerList.Items.Clear();
                foreach (PeerInfo p in peers)
                {
                    peerList.Items.Add(new ListViewItem(new[]
                    {
                        string.IsNullOrEmpty(p.name) ? "(без имени)" : p.name,
                        p.ip + ":" + p.port.ToString(CultureInfo.InvariantCulture),
                        p.steamid,
                        p.isServer ? "сид" : "пир",
                    }));
                }
            });
        }

        // Watches the Mods folder the same way ModSync's auto_publish does, but debounced:
        // copying a 20-mod update fires hundreds of events and only the last one matters.
        private void StartModsWatcher()
        {
            string dir = app.ModsSourceDir();
            if (modsWatcher != null)
            {
                try { modsWatcher.EnableRaisingEvents = false; modsWatcher.Dispose(); } catch { }
                modsWatcher = null;
            }
            if (!app.Settings.AutoPublish || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { return; }

            publishDebounce = publishDebounce ?? new System.Windows.Forms.Timer();
            publishDebounce.Stop();
            publishDebounce.Interval = Math.Max(5, app.Settings.AutoPublishDelaySec) * 1000;
            publishDebounce.Tick -= OnPublishDebounce;
            publishDebounce.Tick += OnPublishDebounce;

            try
            {
                modsWatcher = new FileSystemWatcher(dir);
                modsWatcher.IncludeSubdirectories = true;
                modsWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                           NotifyFilters.LastWrite | NotifyFilters.Size;
                FileSystemEventHandler onEvent = delegate { this.Ui(delegate { publishDebounce.Stop(); publishDebounce.Start(); }); };
                modsWatcher.Changed += onEvent;
                modsWatcher.Created += onEvent;
                modsWatcher.Deleted += onEvent;
                modsWatcher.Renamed += delegate(object s, RenamedEventArgs e) { onEvent(s, e); };
                modsWatcher.EnableRaisingEvents = true;
                app.Log.Add("[авто] слежение за " + dir + " включено, задержка " +
                            app.Settings.AutoPublishDelaySec + " с");
            }
            catch (Exception ex)
            {
                app.Log.Add("[авто] слежение не удалось: " + ex.Message);
            }
        }

        private void OnPublishDebounce(object sender, EventArgs e)
        {
            publishDebounce.Stop();
            if (!app.Settings.AutoPublish) { return; }
            app.Log.Add("[авто] папка Mods изменилась — пересобираю набор");
            PublishPack(true);
        }

        private void StopModsWatcher()
        {
            try
            {
                if (modsWatcher != null) { modsWatcher.EnableRaisingEvents = false; modsWatcher.Dispose(); modsWatcher = null; }
                if (publishDebounce != null) { publishDebounce.Stop(); publishDebounce.Dispose(); publishDebounce = null; }
            }
            catch { }
        }
    }
}

