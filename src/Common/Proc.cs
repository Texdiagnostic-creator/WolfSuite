using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Wolf
{
    // Starts external processes (steamcmd, the game, the dedicated server) and streams their output.
    public sealed class Proc
    {
        private Process process;
        private readonly object gate = new object();

        public event Action<string> OnOutput;
        public event Action<int> OnExit;

        public bool Running
        {
            get
            {
                lock (gate)
                {
                    if (process == null) { return false; }
                    try { return !process.HasExited; } catch { return false; }
                }
            }
        }

        public int Pid
        {
            get
            {
                lock (gate)
                {
                    if (process == null) { return 0; }
                    try { return process.Id; } catch { return 0; }
                }
            }
        }

        // true, когда запущенный процесс — обёртка cmd.exe (запуск через .bat/.cmd).
        // Нужно, чтобы остановка снимала всё дерево: и цикл батника, и сам сервер.
        public bool IsCmdWrapper
        {
            get
            {
                lock (gate)
                {
                    if (process == null) { return false; }
                    try
                    {
                        string name = process.ProcessName;
                        return string.Equals(name, "cmd", StringComparison.OrdinalIgnoreCase);
                    }
                    catch { return false; }
                }
            }
        }

        public int ExitCode
        {
            get
            {
                lock (gate)
                {
                    if (process == null) { return -1; }
                    try { return process.HasExited ? process.ExitCode : -1; } catch { return -1; }
                }
            }
        }

        // hidden=true runs without a console window (used for steamcmd and the game).
        public bool Start(string exe, string args, string workDir, bool hidden, bool attachOutput)
        {
            Stop();
            ProcessStartInfo psi = new ProcessStartInfo(exe, args ?? "");
            if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir)) { psi.WorkingDirectory = workDir; }
            psi.UseShellExecute = false;
            psi.CreateNoWindow = hidden;
            if (attachOutput)
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
            }

            Process p = new Process();
            p.StartInfo = psi;
            if (attachOutput)
            {
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { Emit(e.Data); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { Emit(e.Data); };
            }
            p.EnableRaisingEvents = true;
            p.Exited += delegate
            {
                Action<int> h = OnExit;
                int code = -1;
                try { code = p.ExitCode; } catch { }
                if (h != null) { h(code); }
            };

            try
            {
                p.Start();
            }
            catch (Exception)
            {
                return false;
            }

            lock (gate) { process = p; }
            if (attachOutput)
            {
                try { p.BeginOutputReadLine(); p.BeginErrorReadLine(); } catch { }
            }
            return true;
        }

        private void Emit(string line)
        {
            if (line == null) { return; }
            Action<string> h = OnOutput;
            if (h != null) { h(line); }
        }

        public void Stop()
        {
            Process p;
            lock (gate)
            {
                p = process;
                process = null;
            }
            if (p == null) { return; }
            try
            {
                if (!p.HasExited)
                {
                    // Dedicated server answers graceful "shutdown" only via console; kill the tree here.
                    KillTree(p);
                }
            }
            catch { }
            try { p.Dispose(); } catch { }
        }

        // Снимает ТОЛЬКО обёртку cmd.exe (цикл start_server1.bat), не трогая дочерний
        // 7DaysToDieServer.exe. Нужно для штатной остановки: пока жив цикл, батник поднимет
        // сервер заново через 15 секунд после shutdown. taskkill без /T дерево не сносит,
        // поэтому сервер остаётся работать и успевает закрыться сам по команде из консоли.
        public void StopWrapperOnly()
        {
            Process p;
            lock (gate)
            {
                p = process;
                process = null;
            }
            if (p == null) { return; }
            try
            {
                if (!p.HasExited)
                {
                    ProcessStartInfo psi = new ProcessStartInfo("taskkill.exe", "/F /PID " + p.Id.ToString(CultureInfo.InvariantCulture));
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    Process.Start(psi);
                    p.WaitForExit(4000);
                }
            }
            catch { }
            try { p.Dispose(); } catch { }
        }

        // Жёсткое снятие процесса по PID: резерв, когда консоль сервера недоступна.
        public static void KillPid(int pid)
        {
            KillPid(pid, true);
        }

        // tree=true снимает всё дерево процесса. Для обёртки cmd.exe цикла это недопустимо:
        // в её дереве висит сам 7DaysToDieServer.exe, поэтому цикл убивается только /F /PID.
        public static void KillPid(int pid, bool tree)
        {
            if (pid == 0) { return; }
            try
            {
                string args = "/F " + (tree ? "/T " : "") + "/PID " + pid.ToString(CultureInfo.InvariantCulture);
                ProcessStartInfo psi = new ProcessStartInfo("taskkill.exe", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi)) { if (p != null) { p.WaitForExit(4000); } }
            }
            catch { }
        }

        // Жив ли процесс с таким PID (не наш Process-объект, а любой в системе).
        public static bool IsAlive(int pid)
        {
            if (pid == 0) { return false; }
            try
            {
                using (Process p = Process.GetProcessById(pid)) { return !p.HasExited; }
            }
            catch { return false; }
        }

        private static void KillTree(Process p)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("taskkill.exe", "/F /T /PID " + p.Id.ToString(CultureInfo.InvariantCulture));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
                if (!p.WaitForExit(4000)) { p.Kill(); }
            }
            catch
            {
                try { p.Kill(); } catch { }
            }
        }

        // Runs a process to completion and returns its whole output (steamcmd queries, ping, etc).
        public static string Run(string exe, string args, string workDir, int timeoutMs, out int code)
        {
            StringBuilder sb = new StringBuilder();
            code = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args ?? "");
                if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir)) { psi.WorkingDirectory = workDir; }
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;

                using (Process p = Process.Start(psi))
                {
                    DataReceivedEventHandler handler = delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) { lock (sb) { sb.AppendLine(e.Data); } }
                    };
                    p.OutputDataReceived += handler;
                    p.ErrorDataReceived += handler;
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return sb.ToString();
                    }
                    p.WaitForExit();
                    code = p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                lock (sb) { sb.AppendLine(ex.Message); }
            }
            return sb.ToString();
        }

        public static Process StartDetached(string exe, string args, string workDir)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args ?? "");
            if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir)) { psi.WorkingDirectory = workDir; }
            psi.UseShellExecute = true;
            try { return Process.Start(psi); }
            catch { return null; }
        }
    }

    // steamcmd wrapper: installs/updates the dedicated server (app 294420) and the client (251570).
    public static class SteamCmd
    {
        // force_install_dir is relative to steamcmd's own folder, so we drive it from that folder.
        public static string ScriptUpdate(int appId, string installDir, bool validate, string login)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("//auto-generated by WolfSuite");
            sb.AppendLine("force_install_dir \"" + (installDir ?? "").Replace('\\', '/') + "\"");
            sb.AppendLine("login " + (string.IsNullOrEmpty(login) ? "anonymous" : login));
            sb.AppendLine("app_update " + appId.ToString(CultureInfo.InvariantCulture) + (validate ? " validate" : "") + "");
            sb.AppendLine("quit");
            return sb.ToString();
        }

        public static bool Update(Proc proc, string steamcmd, int appId, string installDir, bool validate, string login)
        {
            if (string.IsNullOrEmpty(steamcmd) || !File.Exists(steamcmd)) { return false; }
            string dir = Path.GetDirectoryName(steamcmd);
            string script = Path.Combine(dir, "wolf_update_" + appId + ".vdf");
            try { File.WriteAllText(script, ScriptUpdate(appId, installDir, validate, login), Encoding.UTF8); }
            catch { return false; }
            string args = "+runscript \"" + script + "\"";
            return proc.Start(steamcmd, args, dir, true, true);
        }
    }
}
