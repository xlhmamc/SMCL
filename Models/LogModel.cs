using System;
using Avalonia.Media;

namespace SMCL.Models
{
    public enum LogLevel
    {
        TRACE,
        DEBUG,
        INFO,
        WARN,
        ERROR,
        FATAL
    }

    public class LogModel
    {
        public string Timestamp { get; } = DateTime.Now.ToString("HH:mm:ss");
        public string Source { get; set; } = "@.Launcher.main";
        public LogLevel Level { get; set; } = LogLevel.INFO;
        public string Message { get; set; } = string.Empty;
        public bool IsMinecraftLog { get; set; }

        /// <summary>
        /// HMCL 风格日志文本拼接
        /// </summary>
        public string FormattedText
        {
            get
            {
                // 无 Source 或属于缩进/格式化行（如 "- CPU: ..."）时直接输出消息
                if (string.IsNullOrEmpty(Source) || Message.StartsWith(" - ") || Message.StartsWith("   "))
                {
                    return $"[{Timestamp}] {Message}";
                }

                // MC 运行日志: [12:34:56] [Client thread/INFO]: 消息
                // 启动器内部日志: [12:34:56] [@.Launcher.main/INFO] 消息
                return IsMinecraftLog
                    ? $"[{Timestamp}] [{Source}/{Level}]: {Message}"
                    : $"[{Timestamp}] [{Source}/{Level}] {Message}";
            }
        }

        /// <summary>
        /// 控制台高亮配色方案
        /// </summary>
        public IBrush Color => Level switch
        {
            LogLevel.TRACE => SolidColorBrush.Parse("#48515a"), // 灰色 TRACE
            LogLevel.DEBUG => SolidColorBrush.Parse("#000000"), // 黑色/深灰 DEBUG
            LogLevel.INFO => IsMinecraftLog 
                ? SolidColorBrush.Parse("#2bff00")              // MC 游戏日志 (淡绿)
                : SolidColorBrush.Parse("#0051ff"),             // 启动器 INFO (主蓝)
            LogLevel.WARN => SolidColorBrush.Parse("#e1e100"),  // 警告 (橙黄)
            LogLevel.ERROR or LogLevel.FATAL => SolidColorBrush.Parse("#ff002f"), // 错误 (红色)
            _ => Brushes.White
        };

        public LogModel(string message, string source = "@.Launcher.main", LogLevel level = LogLevel.INFO, bool isMinecraftLog = false)
        {
            Message = message;
            Source = source;
            Level = level;
            IsMinecraftLog = isMinecraftLog;
        }
    }
}