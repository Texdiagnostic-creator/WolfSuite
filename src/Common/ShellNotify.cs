using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Wolf
{
    // Оболочка кэширует значки по пути файла, поэтому после замены exe на месте
    // Explorer продолжает показывать старую (или пустую) картинку. SHChangeNotify
    // заставляет его перечитать значок без перезапуска проводника.
    public static class ShellNotify
    {
        private const int SHCNE_UPDATEITEM = 0x00002000;
        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_PATHW = 0x0005;
        private const uint SHCNF_IDLIST = 0x0000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        // Точечное обновление значка одного файла: после подмены exe Explorer
        // показывает иконку из старой записи кэша.
        public static void RefreshFile(string path)
        {
            if (string.IsNullOrEmpty(path)) { return; }
            try { SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, path, IntPtr.Zero); }
            catch { }
        }

        // Полный сброс значков: нужен, когда точечного обновления недостаточно.
        public static void RefreshIcons()
        {
            try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
            catch { }
        }

        // Explorer хранит значок в кэше не по содержимому файла, а по его пути, и замены exe
        // это не сбрасывает: у игроков после самообновления на месте лаунчера оставался
        // белый лист. Точечного SHCNE_UPDATEITEM оказалось мало — кэш чистит только
        // ie4uinit.exe -show. Запускаем его скрыто и один раз на версию, иначе сброс
        // выполнялся бы при каждом старте лаунчера.
        public static void RefreshAfterUpdate(string version)
        {
            string stamp = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WolfLauncher", "shell_update.txt");
            try
            {
                string done = File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : "";
                if (!string.IsNullOrEmpty(version) && done == version) { return; }
            }
            catch { }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("ie4uinit.exe", "-show");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi)) { p.WaitForExit(4000); }
            }
            catch { }

            RefreshIcons();
            RefreshFile(CurrentExe());
            try
            {
                string dir = Path.GetDirectoryName(stamp);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
                File.WriteAllText(stamp, version ?? "");
            }
            catch { }
        }

        private static string CurrentExe()
        {
            try
            {
                System.Reflection.Assembly entry = System.Reflection.Assembly.GetEntryAssembly();
                return entry == null ? null : entry.Location;
            }
            catch { return null; }
        }
    }
}
