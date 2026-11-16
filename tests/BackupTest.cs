using System;
using System.Collections.Generic;
using System.IO;
using Wolf;

// Backup/restore + retention behaviour on a fake world folder.
internal static class BackupTest
{
    private static int failures;

    private static void Check(bool condition, string what)
    {
        if (condition) { Console.WriteLine("  ok   " + what); }
        else { Console.WriteLine("  FAIL " + what); failures++; }
    }

    private static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string work = Path.Combine(Path.GetTempPath(), "wolf_backup_test");
        if (Directory.Exists(work)) { Directory.Delete(work, true); }
        string saves = Path.Combine(work, "Saves");
        string backups = Path.Combine(work, "backups");
        string world = Path.Combine(saves, "West Xuyofu Territory");
        Directory.CreateDirectory(Path.Combine(world, "Player"));
        File.WriteAllText(Path.Combine(world, "main.ttw"), "world state v1");
        File.WriteAllText(Path.Combine(world, "world.env"), "env");
        File.WriteAllText(Path.Combine(world, "Player", "76561198164237936.map"), "map data");
        Directory.CreateDirectory(Path.Combine(saves, "Pregen06k01"));
        File.WriteAllText(Path.Combine(saves, "Pregen06k01", "main.ttw"), "other world");
        File.WriteAllText(Path.Combine(saves, "newGameOptions.sdf"), "not a world");

        Console.WriteLine("== world detection ==");
        List<string> worlds = GamePaths.Worlds(saves);
        Check(worlds.Count == 2, "найдено миров: " + worlds.Count + " (" + string.Join(", ", worlds.ToArray()) + ")");
        Check(worlds.Contains("West Xuyofu Territory"), "мир с пробелами в имени");
        Check(!worlds.Contains("newGameOptions.sdf"), "файл не считается миром");

        Console.WriteLine("== backup ==");
        BackupManager manager = new BackupManager(backups);
        manager.KeepLast = 2;
        manager.KeepDays = 1;
        string zip = manager.BackupWorld(saves, "West Xuyofu Territory", "тест", delegate(string s) { });
        Check(zip != null && File.Exists(zip), "архив создан: " + (zip == null ? "нет" : Path.GetFileName(zip)));
        Check(new FileInfo(zip).Length > 0, "архив не пустой: " + GamePaths.HumanSize(new FileInfo(zip).Length));

        List<BackupManager.BackupItem> items = manager.List("West Xuyofu Territory");
        Check(items.Count == 1, "в списке один архив");

        Console.WriteLine("== restore ==");
        File.WriteAllText(Path.Combine(world, "main.ttw"), "world state BROKEN");
        File.Delete(Path.Combine(world, "Player", "76561198164237936.map"));

        string error;
        Check(manager.Restore(items[0], saves, out error), "восстановление выполнено" + (error == null ? "" : ": " + error));
        Check(File.ReadAllText(Path.Combine(world, "main.ttw")) == "world state v1", "содержимое main.ttw вернулось");
        Check(File.Exists(Path.Combine(world, "Player", "76561198164237936.map")), "удалённый файл восстановлен");

        Console.WriteLine("== retention ==");
        for (int i = 0; i < 5; i++)
        {
            manager.BackupWorld(saves, "West Xuyofu Territory", "петля " + i, delegate(string s) { });
            System.Threading.Thread.Sleep(1100); // distinct timestamps
        }
        manager.Prune();
        items = manager.List("West Xuyofu Territory");
        Check(items.Count <= 3, "ротация оставила архивов: " + items.Count + " (лимит 2 + 1 день)");

        Console.WriteLine("== backup of a locked file ==");
        string locked = Path.Combine(world, "main.ttw");
        using (FileStream hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            string zip2 = manager.BackupWorld(saves, "West Xuyofu Territory", "с занятым файлом", delegate(string s) { });
            Check(zip2 != null, "бэкап создан даже при занятом файле");
        }

        try { Directory.Delete(work, true); } catch { }

        Console.WriteLine(failures == 0 ? "\nALL BACKUP TESTS PASSED" : "\nFAILURES: " + failures);
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
