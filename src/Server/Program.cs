using System;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyVersion("1.4.7.0")]
[assembly: AssemblyFileVersion("1.4.7.0")]
[assembly: AssemblyTitle("WolfServer")]
[assembly: AssemblyProduct("WolfServer for 7 Days to Die")]

namespace Wolf.Server
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Один экземпляр на машину. Второй WolfServer отбирал порт раздачи, открывал
            // вторую telnet-сессию и второй раз слал сообщения в чат по расписанию.
            bool created;
            using (System.Threading.Mutex only = new System.Threading.Mutex(true, "WolfServer.SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("WolfServer уже запущен — второе окно не нужно.\n\n" +
                                    "Найдите его в трее или в панели задач.",
                        "WolfServer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Exception ex = e.ExceptionObject as Exception;
                    MessageBox.Show("Непредвиденная ошибка:\n\n" + (ex == null ? "неизвестно" : ex.ToString()),
                        "WolfServer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };

                Application.Run(new ServerForm());
                GC.KeepAlive(only);
            }
        }
    }
}
