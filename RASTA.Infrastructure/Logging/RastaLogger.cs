using System;
using System.IO;
using System.Threading;

namespace RASTA.Infrastructure.Logging
{
    public class RastaLogger
    {
        private readonly string _logPath;
        private readonly object _lock = new object();

        public RastaLogger(string logPath)
        {
            _logPath = logPath;

            // Ensure directory exists
            var dir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }

        public void Info(string message)
        {
            Write("INFO", message);
        }

        public void Warn(string message)
        {
            Write("WARN", message);
        }

        public void Error(string message)
        {
            Write("ERROR", message);
        }

        private void Write(string level, string message)
        {
            string line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            System.Diagnostics.Debug.WriteLine(line);
            lock (_lock)
            {
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
    }
}
