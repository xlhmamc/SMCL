using System;
using System.Diagnostics;
using System.IO;
using SMCL.Models;

namespace SMCL.Services
{
    /// <summary>
    /// 轻量级日志服务：集中记录 Forge/下载/安装过程，写入
    /// <c>&lt;启动器目录&gt;/logs/forge-install.log</c>，同时通过 <see cref="MessageLogged"/>
    /// 事件转发到主界面日志面板。
    ///
    /// 与 MainViewModel 的 smcl-latest.log 分开，避免两个写入者争用同一个文件；
    /// 需要排查 Forge 下载问题时直接查看该文件即可。
    /// </summary>
    public static class LogService
    {
        private const long MaxLogBytes = 5 * 1024 * 1024;

        private static readonly object Sync = new();

        public static string LogFilePath { get; } =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "forge-install.log");

        /// <summary>
        /// 参数：source、message、level。主界面订阅后可在日志面板实时显示。
        /// </summary>
        public static event Action<string, string, LogLevel>? MessageLogged;

        public static void Trace(string source, string message) => Write(LogLevel.TRACE, source, message);
        public static void Debug(string source, string message) => Write(LogLevel.DEBUG, source, message);
        public static void Info(string source, string message) => Write(LogLevel.INFO, source, message);
        public static void Warn(string source, string message) => Write(LogLevel.WARN, source, message);
        public static void Error(string source, string message) => Write(LogLevel.ERROR, source, message);

        private static void Write(LogLevel level, string source, string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}/{level}] {message}";

            lock (Sync)
            {
                try
                {
                    string? directory = Path.GetDirectoryName(LogFilePath);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);

                    RotateIfNeeded();
                    File.AppendAllText(LogFilePath, line + Environment.NewLine);
                }
                catch
                {
                    // 日志失败绝不能影响安装流程
                }
            }

            System.Diagnostics.Debug.WriteLine(line);

            try
            {
                MessageLogged?.Invoke(source, message, level);
            }
            catch
            {
                // 忽略界面日志处理异常
            }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                if (File.Exists(LogFilePath) &&
                    new FileInfo(LogFilePath).Length > MaxLogBytes)
                {
                    File.Move(LogFilePath, LogFilePath + ".old", overwrite: true);
                }
            }
            catch
            {
                // 忽略轮转失败
            }
        }
    }
}
