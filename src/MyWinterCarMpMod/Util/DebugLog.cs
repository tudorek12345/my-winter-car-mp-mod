using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;

namespace MyWinterCarMpMod.Util
{
    public static class DebugLog
    {
        private static readonly object LockObj = new object();
        private static ManualLogSource _log;
        private static StreamWriter _writer;
        private static bool _verbose;

        public static void Initialize(ManualLogSource log, bool verbose)
        {
            _log = log;
            _verbose = verbose;

            if (_writer != null)
            {
                return;
            }

            try
            {
                string root = Paths.BepInExRootPath;
                string fileName = "LogOutput_MyWinterCarMpMod_" + Process.GetCurrentProcess().Id + ".log";
                string path = Path.Combine(root, fileName);
                Directory.CreateDirectory(root);
                _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
                {
                    AutoFlush = true
                };
                WriteLine("INFO", "Log started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            }
            catch (Exception ex)
            {
                if (_log != null)
                {
                    _log.LogWarning("DebugLog init failed: " + ex.Message);
                }
            }
        }

        public static void SetVerbose(bool verbose)
        {
            _verbose = verbose;
        }

        public static void Dispose()
        {
            lock (LockObj)
            {
                if (_writer != null)
                {
                    try
                    {
                        _writer.Flush();
                        _writer.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                    _writer = null;
                }
            }
        }

        public static void Info(string message)
        {
            if (_log != null)
            {
                _log.LogInfo(message);
            }
            WriteLine("INFO", message);
        }

        public static void Warn(string message)
        {
            if (_log != null)
            {
                _log.LogWarning(message);
            }
            WriteLine("WARN", message);
        }

        public static void Error(string message)
        {
            if (_log != null)
            {
                _log.LogError(message);
            }
            WriteLine("ERROR", message);
        }

        public static void Verbose(string message)
        {
            if (!_verbose)
            {
                return;
            }
            if (_log != null)
            {
                _log.LogInfo(message);
            }
            WriteLine("DEBUG", message);
        }

        private static void WriteLine(string level, string message)
        {
            lock (LockObj)
            {
                if (_writer == null)
                {
                    return;
                }
                string line = string.Format("{0} [{1}] {2}", DateTime.Now.ToString("HH:mm:ss.fff"), level, message ?? string.Empty);
                _writer.WriteLine(line);
            }
        }
    }
}
