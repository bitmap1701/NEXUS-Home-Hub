using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NEXUSHome.HostService
{
    /// <summary>
    /// Minimal host log. HostService is a WinExe (no console), so lines always go to
    /// 03_Local_Data_Vault/handoff/hostservice.log (git-ignored); with --console they are also printed
    /// to the launching terminal (or a new console window).
    /// </summary>
    internal static class HostLog
    {
        private const int ATTACH_PARENT_PROCESS = -1;
        private static readonly object Gate = new();
        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
        private static string? _filePath;
        private static bool _console;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllocConsole();

        public static void Initialize(string vaultPath, bool attachConsole)
        {
            try
            {
                _filePath = Path.Combine(vaultPath, "handoff", "hostservice.log");
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            }
            catch (Exception)
            {
                _filePath = null;
            }

            if (attachConsole && (AttachConsole(ATTACH_PARENT_PROCESS) || AllocConsole()))
            {
                _console = true;
                Console.OutputEncoding = Utf8NoBom;
            }
        }

        public static void Write(string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            lock (Gate)
            {
                try
                {
                    if (_console) Console.WriteLine(line);
                    if (_filePath != null) File.AppendAllText(_filePath, line + Environment.NewLine, Utf8NoBom);
                }
                catch (Exception)
                {
                    // Logging must never take the host down.
                }
            }
        }
    }
}
