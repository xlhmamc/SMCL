using System;
using System.Collections.Generic; // 新增
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using SMCL.Models;

namespace SMCL.Services
{
    public class GameProcessService
    {
        private Process? _gameProcess;

        // 核心修改：将 string arguments 改为 IEnumerable<string> arguments
        public void StartProcess(string javaPath, IEnumerable<string> arguments, string workingDir, Action<string, string, LogLevel, bool> logCallback)
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaPath,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // 核心修改：逐个添加参数，彻底消灭引号转义问题
            foreach (var arg in arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            _gameProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

            // 监听输出流 (保持你原有的逻辑不变)
            _gameProcess.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    var (source, level, message) = ParseMinecraftLog(e.Data);
                    logCallback(message, source, level, true);
                }
            };

            _gameProcess.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    logCallback(e.Data, "STDERR", LogLevel.ERROR, true);
                }
            };

            _gameProcess.Exited += (sender, e) =>
            {
                int exitCode = _gameProcess.ExitCode;
                LogLevel level = exitCode == 0 ? LogLevel.INFO : LogLevel.ERROR;
                logCallback($"游戏进程已退出，退出代码: {exitCode}", "@.launcher.ProcessBuilder", level, false);
            };

            _gameProcess.Start();
            _gameProcess.BeginOutputReadLine();
            _gameProcess.BeginErrorReadLine();
        }

        private (string Source, LogLevel Level, string Message) ParseMinecraftLog(string rawLog)
        {
            var match = Regex.Match(rawLog, @"\[([^\]]+)\/(INFO|WARN|ERROR|FATAL|DEBUG|TRACE)\]:?\s*(.*)");
            
            if (match.Success)
            {
                string source = match.Groups[1].Value;
                string levelStr = match.Groups[2].Value;
                string message = match.Groups[3].Value;

                if (Enum.TryParse<LogLevel>(levelStr, true, out var level))
                {
                    return (source, level, string.IsNullOrEmpty(message) ? rawLog : message);
                }
            }
            return ("Minecraft", LogLevel.INFO, rawLog);
        }
    }
}