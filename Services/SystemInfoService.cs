using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SMCL.Services
{
    public class JavaInfo
    {
        public string Path { get; set; } = string.Empty;
        public string Version { get; set; } = "Unknown";
    }

    public static class SystemInfoService
    {
        public static string GetOSVersion()
        {
            return $"{RuntimeInformation.OSDescription} ({Environment.OSVersion.Platform} {Environment.OSVersion.Version})";
        }

        public static string GetArchitecture()
        {
            return RuntimeInformation.OSArchitecture.ToString().ToLower();
        }

        public static string GetMemoryInfo()
        {
            var gcInfo = GC.GetGCMemoryInfo();
            double totalGiB = gcInfo.TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0);
            return $"{totalGiB:F2} GiB";
        }

        /// <summary>
        /// 提取 Java 主版本号 (例: "1.8.0_341" -> 8, "17.0.2" -> 17, "21.0.1" -> 21)
        /// </summary>
        public static int ExtractMajorVersion(string versionStr)
        {
            if (string.IsNullOrWhiteSpace(versionStr)) return 0;
            var match = Regex.Match(versionStr, @"(?:1\.)?(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int major))
            {
                return major;
            }
            return 0;
        }

        /// <summary>
        /// 根据 Minecraft 游戏版本推算推荐的最佳 Java 版本
        /// </summary>
        public static int GetRequiredJavaVersion(string gameVersion)
        {
            var match = Regex.Match(gameVersion, @"1\.(\d+)(?:\.(\d+))?");
            if (!match.Success) return 8;

            int minor = int.Parse(match.Groups[1].Value);
            int patch = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;

            if (minor < 17) return 8;                    // 1.16.5 及以下 -> Java 8
            if (minor == 17) return 16;                 // 1.17.x -> Java 16
            if (minor >= 18 && minor <= 19) return 17;  // 1.18 - 1.19.4 -> Java 17
            if (minor == 20) return patch >= 5 ? 21 : 17; // 1.20.0~1.20.4 -> 17, 1.20.5+ -> 21
            return 21;                                  // 1.21+ -> Java 21
        }

        public static List<JavaInfo> SearchAllJavaExecutables()
        {
            var results = new List<JavaInfo>();
            var pathsToScan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            string javaExeName = isWindows ? "java.exe" : "java";

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var p in pathEnv.Split(Path.PathSeparator))
                {
                    string fullPath = Path.Combine(p.Trim(), javaExeName);
                    if (File.Exists(fullPath)) pathsToScan.Add(fullPath);
                }
            }

            string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(javaHome))
            {
                string fullPath = Path.Combine(javaHome, "bin", javaExeName);
                if (File.Exists(fullPath)) pathsToScan.Add(fullPath);
            }

            var rootSearchDirs = new List<string>();
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (isWindows)
            {
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
                {
                    rootSearchDirs.Add(Path.Combine(drive.Name, "Program Files", "Java"));
                    rootSearchDirs.Add(Path.Combine(drive.Name, "Program Files (x86)", "Java"));
                    rootSearchDirs.Add(Path.Combine(drive.Name, "Java"));
                    rootSearchDirs.Add(Path.Combine(drive.Name, "Minecraft"));
                }
                rootSearchDirs.Add(Path.Combine(userHome, ".jdks"));
                rootSearchDirs.Add(Path.Combine(userHome, ".m2"));
            }
            else
            {
                rootSearchDirs.Add("/usr/lib/jvm");
                rootSearchDirs.Add("/usr/java");
                rootSearchDirs.Add("/opt");
                rootSearchDirs.Add(userHome);
                rootSearchDirs.Add(Path.Combine(userHome, ".sdkman/candidates/java"));
                rootSearchDirs.Add(Path.Combine(userHome, ".jdks"));
                rootSearchDirs.Add(Path.Combine(userHome, ".local/share/JetBrains"));
                rootSearchDirs.Add(Path.Combine(userHome, ".minecraft/runtime"));
            }

            foreach (var searchDir in rootSearchDirs.Distinct())
            {
                if (Directory.Exists(searchDir))
                {
                    SafeScanJava(searchDir, javaExeName, pathsToScan, maxDepth: 5);
                }
            }

            foreach (var path in pathsToScan)
            {
                string ver = QueryJavaVersion(path);
                results.Add(new JavaInfo { Path = path, Version = ver });
            }

            return results;
        }

        private static void SafeScanJava(string targetDir, string targetFile, HashSet<string> resultSet, int maxDepth, int currentDepth = 0)
        {
            if (currentDepth > maxDepth) return;

            try
            {
                string possibleJava = Path.Combine(targetDir, targetFile);
                if (File.Exists(possibleJava)) resultSet.Add(possibleJava);

                string possibleBinJava = Path.Combine(targetDir, "bin", targetFile);
                if (File.Exists(possibleBinJava)) resultSet.Add(possibleBinJava);

                foreach (var subDir in Directory.GetDirectories(targetDir))
                {
                    string dirName = Path.GetFileName(subDir);
                    if (dirName.StartsWith(".") && dirName != ".jdks" && dirName != ".sdkman" && dirName != ".minecraft")
                    {
                        continue;
                    }

                    SafeScanJava(subDir, targetFile, resultSet, maxDepth, currentDepth + 1);
                }
            }
            catch { }
        }

        public static string QueryJavaVersion(string javaPath)
        {
            if (!File.Exists(javaPath)) return "未知版本 / 文件不存在";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = javaPath,
                    Arguments = "-version",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardError.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(output)) output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(1500);

                    var match = Regex.Match(output, @"version\s*""([^""]+)""");
                    if (match.Success) return match.Groups[1].Value;
                }
            }
            catch { }
            return "Java Runtime";
        }
    }
}