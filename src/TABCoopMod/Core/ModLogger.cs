using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace TABCoopMod.Core
{
    public static class ModLogger
    {
        private static readonly object _lock = new object();
        private static string _logFilePath;
        private static string _versionedLogFilePath;
        private const int MaxArchivedLogs = 30;

        public static string LogFilePath => _logFilePath;
        public static string VersionedLogFilePath => _versionedLogFilePath;

        public static void Initialize(string customLogFileName = null)
        {
            try
            {
                string modDir = Path.GetDirectoryName(typeof(ModLogger).Assembly.Location);
                string baseDir = !string.IsNullOrEmpty(modDir) ? modDir : AppDomain.CurrentDomain.BaseDirectory;
                // Allow multi-instance tests to give each process its own log file.
                string envLogName = Environment.GetEnvironmentVariable("TABCOOP_LOG_NAME");
                bool isCustomEnvLog = !string.IsNullOrWhiteSpace(envLogName);
                string fileName = isCustomEnvLog
                    ? envLogName.Trim()
                    : (customLogFileName ?? "TABCoopMod.log");
                _logFilePath = Path.Combine(baseDir, fileName);

                int pid = Process.GetCurrentProcess().Id;
                DateTime now = DateTime.Now;

                if (!isCustomEnvLog && string.IsNullOrEmpty(customLogFileName))
                {
                    string logsDir = Path.Combine(baseDir, "TABCoopMod_Logs");
                    Directory.CreateDirectory(logsDir);
                    _versionedLogFilePath = Path.Combine(
                        logsDir,
                        $"TABCoopMod_{ModInfo.Tag}_{now:yyyyMMdd_HHmmss}_p{pid}.log");
                    PruneOldArchivedLogs(logsDir);
                }
                else
                {
                    _versionedLogFilePath = null;
                }

                string header =
                    $"================================================================\r\n" +
                    $" TABCoopMod {ModInfo.Tag} (Asm={ModInfo.AssemblyVersion}, Build={ModInfo.GetBuildTimestamp()})\r\n" +
                    $" Initialized at {now:yyyy-MM-dd HH:mm:ss.fff}\r\n" +
                    $" Process ID: {pid} | 64-bit: {Environment.Is64BitProcess} | CLR: {Environment.Version}\r\n" +
                    $" OS: {Environment.OSVersion}\r\n" +
                    $" Base Directory: {baseDir}\r\n" +
                    (_versionedLogFilePath != null ? $" Versioned Log: {_versionedLogFilePath}\r\n" : "") +
                    $" Command Line: {Environment.CommandLine}\r\n" +
                    $"================================================================\r\n";

                lock (_lock)
                {
                    File.WriteAllText(_logFilePath, header, Encoding.UTF8);
                    if (_versionedLogFilePath != null)
                        File.WriteAllText(_versionedLogFilePath, header, Encoding.UTF8);
                }
            }
            catch
            {
                // Fallback to temp if game dir is not writable
                _logFilePath = Path.Combine(Path.GetTempPath(), $"TABCoopMod_{ModInfo.Tag}.log");
                _versionedLogFilePath = null;
            }
        }

        private static void PruneOldArchivedLogs(string logsDir)
        {
            try
            {
                var files = new DirectoryInfo(logsDir).GetFiles("TABCoopMod_*.log");
                if (files.Length < MaxArchivedLogs) return;
                Array.Sort(files, (a, b) => a.CreationTimeUtc.CompareTo(b.CreationTimeUtc));
                int toDelete = files.Length - (MaxArchivedLogs - 1);
                for (int i = 0; i < toDelete; i++)
                {
                    try { files[i].Delete(); } catch { }
                }
            }
            catch { }
        }

        public static void LogGameVersionInfo(Assembly gameAssembly)
        {
            try
            {
                if (gameAssembly == null) return;
                string loc = gameAssembly.Location;
                string fileVer = (!string.IsNullOrEmpty(loc) && File.Exists(loc))
                    ? FileVersionInfo.GetVersionInfo(loc).FileVersion
                    : "unknown";
                Info($"[Environment] Mod={ModInfo.Tag} GameAsm={gameAssembly.GetName().Version} GameFileVer={fileVer}");
            }
            catch { }
        }

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message, Exception ex = null)
        {
            if (ex != null)
                Write("ERROR", $"{message}\r\nException: {ex}");
            else
                Write("ERROR", message);
        }

        private static void Write(string level, string message)
        {
            try
            {
                if (_logFilePath == null) Initialize();
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [{ModInfo.Tag}] [T{Environment.CurrentManagedThreadId}] {message}\r\n";
                lock (_lock)
                {
                    File.AppendAllText(_logFilePath, line, Encoding.UTF8);
                    if (_versionedLogFilePath != null)
                        File.AppendAllText(_versionedLogFilePath, line, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }
    }
}
