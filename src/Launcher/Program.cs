using System;
using System.Reflection;
using System.Windows.Forms;

// The version is what the server publishes in /launcher.json, so a player can see which
// build they run. Bump it together with the build number in build.bat.
[assembly: AssemblyVersion("1.5.6.0")]
[assembly: AssemblyFileVersion("1.5.6.0")]
[assembly: AssemblyTitle("WolfLauncher")]
[assembly: AssemblyProduct("WolfLauncher for 7 Days to Die")]

namespace Wolf.Launcher
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Значок на рабочем столе после самообновления оставался пустым листом: Explorer
            // кэширует картинку по пути файла. Сбрасываем кэш один раз на версию, до показа окна.
            Wolf.ShellNotify.RefreshAfterUpdate(LauncherVersion());

            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                MessageBox.Show("Непредвиденная ошибка:\n\n" + (ex == null ? "неизвестно" : ex.ToString()),
                    "WolfLauncher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            Application.Run(new LauncherForm());
        }

        // Версия сборки в том же формате, что публикует сервер в launcher.json.
        private static string LauncherVersion()
        {
            try
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build + "." + v.Revision;
            }
            catch { return ""; }
        }
    }
}
