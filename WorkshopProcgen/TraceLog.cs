using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;

namespace WorkshopProcgen
{
    internal sealed class TraceLog : ILogListener
    {
        private static readonly object Gate = new object();
        private static string _path;

        public static void Install()
        {
            _path = Path.Combine(Paths.BepInExRootPath, "WorkshopProcgen.trace.log");
            File.WriteAllText(_path, $"--- trace started {DateTime.Now:O} ---{Environment.NewLine}");
            Logger.Listeners.Add(new TraceLog());
        }

        public static void Write(string line)
        {
            if (_path == null) return;
            try
            {
                lock (Gate)
                    File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} [t{System.Threading.Thread.CurrentThread.ManagedThreadId}] {line}{Environment.NewLine}");
            }
            catch { }
        }

        public void LogEvent(object sender, LogEventArgs e) => Write($"[{e.Level}:{e.Source.SourceName}] {e.Data}");

        public void Dispose() { }
    }
}
