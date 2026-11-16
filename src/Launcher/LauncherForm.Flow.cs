using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Wolf.Launcher
{
    // Connection, synchronisation and launch flow.
    public sealed partial class LauncherForm
    {
        private void ConnectAndCheck()
        {
            string url = (txtUrl.Text ?? "").Trim();
            if (url.Length == 0)
            {
                MessageBox.Show(this, "Введите адрес сервера, например http://мой-сервер:8775",
                    "WolfLauncher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!url.StartsWith("http://") && !url.StartsWith("https://")) { url = "http://" + url; }
            settings.ServerUrl = url.TrimEnd('/');
            txtUrl.Text = settings.ServerUrl;
            Save();
            lblServerName.Text = settings.ServerUrl;
            ApplyAutoTimer();
            CheckMods(true);
        }

        private void CheckMods(bool interactive)
        {
            CheckMods(interactive, true);
        }

        // deep=true re-hashes every local file (button "Проверить файлы").
        // ignoreCache=true считает хеши заново, не доверяя кэшу (подмена файла с сохранением даты).
        private void CheckMods(bool interactive, bool deep, bool ignoreCache = false)
        {
            if (syncing) { return; }
            if (string.IsNullOrEmpty(settings.ServerUrl))
            {
                if (interactive)
                {
                    MessageBox.Show(this, "Сначала укажите адрес сервера.", "WolfLauncher",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }
            if (string.IsNullOrEmpty(settings.ModsDir))
            {
                MessageBox.Show(this, "Не найдена папка Mods. Укажите её в настройках.", "WolfLauncher",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            syncing = true;
            btnSync.Enabled = false;
            btnPlay.Enabled = false;
            btnCancel.Enabled = true;
            btnSync.Text = "⟳  Проверить моды";
            progress.Visible = true;
            SetState(deep ? "глубокая проверка файлов..." : "проверка модов...", Theme.Info);

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    ModSync current = new ModSync(store, settings.ModsDir, log);
                    current.HashCache = hashCache;
                    current.IgnoreCache = ignoreCache;
                    sync = current;
                    current.BaseUrl = settings.ServerUrl;
                    current.OnProgress += delegate(int done, int total, string source)
                    {
                        int percent = total == 0 ? 0 : (int)(done * 100L / total);
                        Theme.ReportProgress(this, progress, percent, lblProgress,
                            "скачано " + done + " / " + total + " чанков  (" + source + ")");
                    };
                    current.OnTransfer += delegate(long doneBytes, long totalBytes, string source)
                    {
                        if (totalBytes <= 0) { return; }
                        int percent = (int)(doneBytes * 100L / totalBytes);
                        Theme.ReportProgress(this, progress, percent, lblProgress,
                            TransferText(doneBytes, totalBytes, source));
                    };
                    current.OnStep += delegate(string s) { SetState(s, Theme.TextDim); };

                    string error;
                    if (!current.FetchManifest(out error))
                    {
                        bool wanted = playWhenReady;
                        if (current.Cancelled)
                        {
                            SetState("проверка отменена", Theme.TextDim);
                            log.Add("проверка отменена");
                        }
                        else
                        {
                            SetState("сервер недоступен: " + error, Theme.Err);
                            log.Add("манифест не получен: " + error);
                        }
                        FinishCheck(false);
                        AskLaunchAnyway(wanted && !current.Cancelled, error);
                        return;
                    }

                    ServerInfo info = current.Info;
                    ModManifest manifest = current.Manifest;
                    if (info.Port > 0) { lastGamePort = info.Port; }
                    this.Ui(delegate
                    {
                        lblServerName.Text = string.IsNullOrEmpty(info.Name) ? settings.ServerUrl : info.Name;
                        lblServerNote.Text = (string.IsNullOrEmpty(info.Note) ? "" : info.Note + "   ") +
                                             (string.IsNullOrEmpty(info.World) ? "" : "мир: " + info.World + "   ") +
                                             (info.Slots > 0 ? "слотов: " + info.Slots : "");
                        if (!string.IsNullOrEmpty(info.Vk)) { settings.VkUrl = info.Vk; Save(); }
                        ShowNews(info, manifest);
                    });

                    List<ManifestFile> changed = current.Diff(deep);
                    long bytes = current.DownloadSize(changed);
                    log.Add((deep ? "глубокая проверка" : "проверка") + (ignoreCache ? " (без кэша)" : "") +
                            ": файлов в наборе " + manifest.files.Count +
                            ", требуют обновления: " + changed.Count +
                            ", к загрузке: " + GamePaths.HumanSize(bytes));
                    LogMismatches(current.Mismatches);
                    lastMismatches = new List<DiffMismatch>(current.Mismatches);
                    lastServerHash = manifest.ContentHash;
                    hashCache.Save();

                    this.Ui(delegate
                    {
                        modList.Items.Clear();
                        HashSet<string> changedRel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (ManifestFile f in changed) { changedRel.Add(f.rel); }

                        // Причина расхождения по моду: «содержимое» значит, что размер совпал и
                        // ZAC кикнул бы за подменённые байты — самый непонятный для игрока случай.
                        Dictionary<string, string> modReason = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (DiffMismatch m in current.Mismatches)
                        {
                            string mod = m.rel.Split('/')[0];
                            string known;
                            if (modReason.TryGetValue(mod, out known))
                            {
                                if (known == "содержимое") { continue; }
                                if (m.reason != "содержимое") { continue; }
                            }
                            modReason[mod] = m.reason;
                        }

                        Dictionary<string, long> perMod = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                        Dictionary<string, int> perModCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (ManifestFile f in manifest.files)
                        {
                            string mod = f.rel.Split('/')[0];
                            long acc;
                            perMod.TryGetValue(mod, out acc);
                            perMod[mod] = acc + f.size;
                            int cnt;
                            perModCount.TryGetValue(mod, out cnt);
                            perModCount[mod] = cnt + 1;
                        }

                        foreach (string mod in manifest.loadOrder)
                        {
                            string version;
                            manifest.modVersions.TryGetValue(mod, out version);
                            bool dirty = false;
                            foreach (string rel in changedRel) { if (rel.StartsWith(mod + "/", StringComparison.OrdinalIgnoreCase)) { dirty = true; break; } }

                            long size;
                            perMod.TryGetValue(mod, out size);
                            string reason;
                            modReason.TryGetValue(mod, out reason);
                            string status = dirty
                                ? ("нужно обновить: " + (string.IsNullOrEmpty(reason) ? "файлы" : reason))
                                : "актуален";
                            ListViewItem item = new ListViewItem(new[]
                            {
                                mod,
                                string.IsNullOrEmpty(version) ? "—" : version,
                                status,
                                GamePaths.HumanSize(size),
                            });
                            item.ForeColor = dirty ? Theme.Warn : Theme.Text;
                            modList.Items.Add(item);
                        }

                        lblModsCount.Text = "модов: " + manifest.loadOrder.Count +
                                            "   файлов: " + manifest.files.Count +
                                            "   хэш: " + manifest.ContentHash;
                    });

                    if (current.Cancelled)
                    {
                        SetState("проверка отменена", Theme.TextDim);
                        FinishCheck(false);
                        return;
                    }

                    // Моды, которых сервер больше не раздаёт, убираем ДО выхода «всё актуально»:
                    // иначе у клиента, который уже всё скачал, лишние папки остаются навсегда,
                    // и античит кикает его за "extra mod".
                    List<string> obsolete = current.RemoveObsolete(delegate(string name) { log.Add("удалён лишний мод: " + name); });
                    if (obsolete.Count > 0)
                    {
                        log.Add("лишних модов убрано: " + obsolete.Count + " (перенесены в disabled_mods)");
                    }
                    if (changed.Count == 0)
                    {
                        settings.LastHash = manifest.ContentHash;
                        Save();
                        hashCache.Save();
                        if (peer != null) { peer.Hash = settings.LastHash; }
                        SetState(deep ? "все файлы целы" : "всё актуально", Theme.Ok);
                        Theme.ReportProgress(this, progress, 100, lblProgress,
                            deep ? "проверка файлов завершена: расхождений нет" : "обновление не требуется");
                        FinishCheck(true);

                        if (playWhenReady)
                        {
                            playWhenReady = false;
                            log.Add("набор совпал с сервером — запускаю игру");
                            this.Ui(delegate { LaunchGameNow(); });
                        }
                        return;
                    }

                    if (!settings.AcceptedRules)
                    {
                        DialogResult r = MessageBox.Show(this,
                            "Сервер предлагает набор модов (" + changed.Count + " файлов, " + GamePaths.HumanSize(bytes) + ").\n\n" +
                            "Моды будут скачаны в папку:\n" + settings.ModsDir + "\n\nПродолжить?",
                            "Синхронизация модов", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                        if (r != DialogResult.OK)
                        {
                            SetState("синхронизация отменена", Theme.TextDim);
                            FinishCheck(false);
                            return;
                        }
                        settings.AcceptedRules = true;
                        Save();
                    }

                    List<PeerInfo> peers = PeerNode.QueryPeers(settings.ServerUrl, manifest.ContentHash, 8000);
                    if (peer != null)
                    {
                        peer.Hash = manifest.ContentHash;
                        peer.AnnounceTo(settings.ServerUrl, 60);
                    }

                    SetState("загрузка модов...", Theme.Info);
                    int chunkCount = 0;
                    foreach (ManifestFile f in changed) { chunkCount += f.chunks.Count; }
                    log.Add("к загрузке: " + GamePaths.HumanSize(bytes) + ", файлов " + changed.Count +
                            ", чанков " + chunkCount);
                    Theme.ReportProgress(this, progress, 0, lblProgress,
                        "к загрузке " + GamePaths.HumanSize(bytes) + " (" + changed.Count + " файлов)");
                    if (!current.Apply(changed, peers, out error))
                    {
                        bool wanted = playWhenReady;
                        if (current.Cancelled)
                        {
                            SetState("загрузка отменена — нажмите «Повторить»", Theme.Warn);
                            log.Add("загрузка отменена, скачанные чанки сохранены");
                        }
                        else
                        {
                            SetState("ошибка загрузки: " + error, Theme.Err);
                            log.Add("ошибка синхронизации: " + error);
                        }
                        FinishCheck(false);
                        AskLaunchAnyway(wanted && !current.Cancelled, error);
                        return;
                    }

                    List<string> removed = current.RemoveObsolete(delegate(string name) { log.Add("удалён лишний мод: " + name); });
                    settings.LastHash = manifest.ContentHash;
                    Save();
                    hashCache.Save();
                    if (peer != null) { peer.Hash = settings.LastHash; }

                    SetState("моды обновлены", Theme.Ok);
                    Theme.ReportProgress(this, progress, 100, lblProgress, "готово");
                    this.Ui(delegate
                    {
                        foreach (ListViewItem item in modList.Items)
                        {
                            item.SubItems[2].Text = "актуален";
                            item.ForeColor = Theme.Text;
                        }
                    });
                    FinishCheck(true);

                    if (settings.AutoPlayAfterSync || playWhenReady)
                    {
                        playWhenReady = false;
                        log.Add("моды обновлены — запускаю игру");
                        this.Ui(delegate { LaunchGameNow(); });
                    }
                }
                catch (Exception ex)
                {
                    log.Add("исключение: " + ex.Message);
                    SetState("ошибка: " + ex.Message, Theme.Err);
                    FinishCheck(false);
                }
            });
        }

        // Список расхождений в журнал: игрок может скопировать строку и прислать её в чат,
        // вместо того чтобы гадать, какой файл античит назвал «modified file».
        private void LogMismatches(List<DiffMismatch> list)
        {
            if (list == null || list.Count == 0) { return; }
            log.Add("расхождения с сервером (" + list.Count + "):");
            int shown = 0;
            foreach (DiffMismatch m in list)
            {
                if (shown >= 20)
                {
                    log.Add("  ... ещё " + (list.Count - shown).ToString(CultureInfo.InvariantCulture));
                    break;
                }
                log.Add("  " + m);
                shown++;
            }
        }

        private void CancelSync()        {
            ModSync current = sync;
            if (current == null) { return; }
            current.Cancel();
            btnCancel.Enabled = false;
            SetState("останавливаю загрузку...", Theme.Warn);
            log.Add("запрошена отмена синхронизации");
        }

        // Сверка не удалась, но игрок нажимал «Играть». Раньше мы спрашивали «запустить всё равно»,
        // и игрок гарантированно получал кик античитом через несколько минут игры. Теперь запуска
        // без сверки нет: показываем причину и оставляем кнопку «Повторить».
        private void AskLaunchAnyway(bool wanted, string reason)
        {
            if (!wanted) { return; }
            this.Ui(delegate
            {
                MessageBox.Show(this,
                    "Игра не запущена: моды не совпали с сервером.\n\n" + reason +
                    "\n\nНажмите «Повторить», чтобы закончить сверку. С непроверенными модами сервер" +
                    " выкинет вас из игры через несколько минут.",
                    "WolfLauncher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            });
        }

        // "12.4 МБ / 87.0 МБ · 3.1 МБ/с · осталось ~24 с"
        // Скорость считаем по скользящему окну (последние ~6 с), а не от старта загрузки:
        // иначе ETA держится на первом медленном отрезке и врёт в разы.
        private readonly Queue<KeyValuePair<DateTime, long>> speedSamples = new Queue<KeyValuePair<DateTime, long>>();

        private string TransferText(long doneBytes, long totalBytes, string source)
        {
            DateTime now = DateTime.UtcNow;
            if (transferStartUtc == DateTime.MinValue)
            {
                transferStartUtc = now;
                transferStartBytes = doneBytes;
            }

            speedSamples.Enqueue(new KeyValuePair<DateTime, long>(now, doneBytes));
            while (speedSamples.Count > 2 && (now - speedSamples.Peek().Key).TotalSeconds > 6.0)
            {
                speedSamples.Dequeue();
            }

            KeyValuePair<DateTime, long> first = speedSamples.Peek();
            double seconds = Math.Max(0.5, (now - first.Key).TotalSeconds);
            double speed = (doneBytes - first.Value) / seconds;
            if (speed < 0) { speed = 0; }

            // Раз в 5 секунд пишем скорость в журнал: по нему видно, где именно проседает
            // загрузка (начало, середина, конкретный чанк), не гоняя игрока по скриншотам.
            if ((now - lastSpeedLogUtc).TotalSeconds >= 5.0)
            {
                lastSpeedLogUtc = now;
                log.Add("[скорость] " + GamePaths.HumanSize((long)speed) + "/с, сделано " +
                        GamePaths.HumanSize(doneBytes) + " из " + GamePaths.HumanSize(totalBytes) + " (" + source + ")");
            }
            string text = GamePaths.HumanSize(doneBytes) + " / " + GamePaths.HumanSize(totalBytes) +
                          "   " + GamePaths.HumanSize((long)speed) + "/с";
            if (speed > 1024 && totalBytes > doneBytes)
            {
                text += "   осталось ~" + EtaText((totalBytes - doneBytes) / speed);
            }
            return text + "   (" + source + ")";
        }

        private DateTime transferStartUtc = DateTime.MinValue;
        private long transferStartBytes;
        private DateTime lastSpeedLogUtc = DateTime.MinValue;
        private int lastGamePort = 26900;   // порт игры с /server.json, нужен моду автоподключения

        private static string EtaText(double seconds)
        {
            if (seconds < 1) { return "меньше секунды"; }
            if (seconds < 60) { return Math.Round(seconds).ToString("0", CultureInfo.InvariantCulture) + " с"; }
            if (seconds < 3600) { return Math.Round(seconds / 60.0).ToString("0", CultureInfo.InvariantCulture) + " мин"; }
            return ((int)(seconds / 3600)).ToString(CultureInfo.InvariantCulture) + " ч " +
                   ((int)((seconds % 3600) / 60)).ToString(CultureInfo.InvariantCulture) + " мин";
        }

        private void FinishCheck(bool success)
        {
            transferStartUtc = DateTime.MinValue;
            transferStartBytes = 0;
            // Неудачная сверка отменяет отложенный запуск: игра стартует только с актуальным набором.
            if (!success) { playWhenReady = false; }
            this.Ui(delegate
            {
                syncing = false;
                btnSync.Enabled = true;
                btnPlay.Enabled = true;
                btnCancel.Enabled = false;
                progress.Visible = false;
                btnSync.Text = success ? "⟳  Проверить моды" : "⟳  Повторить";
                if (success && string.IsNullOrEmpty(settings.GameRoot))
                {
                    lblProgress.Text = "игра не найдена — укажите папку в настройках";
                }
            });
        }

        // «Играть» сначала сверяет набор с сервером, как это делал ModSync: пока моды не
        // совпали с манифестом, игра не запускается. Сама сверка и докачка — в CheckMods.
        private void Play()
        {
            if (string.IsNullOrEmpty(settings.ServerUrl))
            {
                MessageBox.Show(this, "Сначала укажите адрес сервера.", "WolfLauncher",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (string.IsNullOrEmpty(settings.GameRoot))
            {
                string found = GamePaths.ClientRoot(null);
                if (found == null)
                {
                    MessageBox.Show(this, "Не найдена установленная игра. Укажите папку в настройках.", "WolfLauncher",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                settings.GameRoot = found;
                settings.ModsDir = GamePaths.ClientModsDir(found);
                Save();
            }
            if (string.IsNullOrEmpty(settings.ModsDir))
            {
                MessageBox.Show(this, "Не найдена папка Mods. Укажите её в настройках.", "WolfLauncher",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            playWhenReady = true;
            if (syncing)
            {
                SetState("идёт проверка модов — игра запустится после неё", Theme.Info);
                log.Add("нажато «Играть» во время проверки: запуск после сверки");
                return;
            }
            SetState("сверяю файлы модов перед запуском...", Theme.Info);
            CheckMods(false, true);
        }

        // Запуск игры без сверки: только после успешной проверки набора.
        private void LaunchGameNow()
        {
            if (string.IsNullOrEmpty(settings.GameRoot)) { return; }

            // Адрес сервера для мода автоподключения — до старта игры.
            // После подключения мод сам гасит Enabled, поэтому прямой запуск игры не тянет на сервер.
            ModSync.WriteAutoConnect(settings.ServerUrl, lastGamePort);

            string error;
            if (ModSync.LaunchGame(settings.GameRoot, settings, out error))
            {
                log.Add("игра запущена: " + settings.GameRoot);
                SetState("игра запущена", Theme.Ok);
                // The game takes the screen anyway; keep the launcher in the tray instead of
                // leaving a window behind it. Double click on the icon brings it back.
                if (tray != null) { tray.MinimizeToTray(); }
                if (!string.IsNullOrEmpty(settings.ServerUrl) && peer != null)
                {
                    peer.AnnounceTo(settings.ServerUrl, 60);
                }
            }
            else
            {
                log.Add("ошибка запуска: " + error);
                MessageBox.Show(this, "Не удалось запустить игру:\n" + error, "WolfLauncher",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
