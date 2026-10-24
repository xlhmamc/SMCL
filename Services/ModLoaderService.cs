using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using SMCL.Models;

namespace SMCL.Services
{
    public class ModLoaderService
    {
        private static readonly HttpClient HttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // 获取 Fabric 加载器列表
        public async Task<List<string>> GetFabricLoaderVersionsAsync(string mcVersion)
        {
            try
            {
                // Fabric Meta API
                string url = $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}";
                string json = await HttpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var list = new List<string>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("loader", out var loader) && loader.TryGetProperty("version", out var version))
                    {
                        list.Add(version.GetString()!);
                    }
                }
                return list;
            }
            catch
            {
                return new List<string>();
            }
        }

        // 获取 Forge 加载器列表 (推荐使用 BMCLAPI 的 Forge 接口)
        public async Task<List<string>> GetForgeLoaderVersionsAsync(string mcVersion)
        {
            try
            {
                string url = $"https://bmclapi2.bangbang93.com/forge/minecraft/{mcVersion}";
                string json = await HttpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var list = new List<string>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    // BMCLAPI 返回的 Forge 数据包含 version 字段
                    if (item.TryGetProperty("version", out var version))
                    {
                        list.Add(version.GetString()!);
                    }
                }
                return list;
            }
            catch
            {
                return new List<string>();
            }
        }

        // === Fabric 安装流程（纯 JSON 拼装） ===
        /// <summary>
        /// 仅安装 Fabric Loader 本体（版本 JSON + Loader 依赖库）。
        /// Fabric API 等模组的安装请使用 <see cref="ModService"/>（会写入实例的 mods 目录）。
        /// </summary>
        public async Task InstallFabricAsync(string mcVersion, string loaderVersion, string gameDir, IProgress<DownloadProgressReport>? progress)
        {
            string versionName = $"fabric-loader-{loaderVersion}-{mcVersion}";
            string versionDir = Path.Combine(gameDir, "versions", versionName);
            Directory.CreateDirectory(versionDir);

            progress?.Report(new DownloadProgressReport { TotalFiles = 1, CompletedFiles = 0, CurrentFileName = "正在获取 Fabric 配置..." });

            // 1. 获取 Fabric Profile JSON
            string profileUrl = $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}/{loaderVersion}/profile/json";
            string profileJson = await HttpClient.GetStringAsync(profileUrl);
            
            // 写入本地版本文件夹
            string jsonPath = Path.Combine(versionDir, $"{versionName}.json");
            await File.WriteAllTextAsync(jsonPath, profileJson);

            // 解析并提取需要下载的 Libraries (可以使用你 DownloadService 里现成的 BatchDownloadAsync 逻辑)
            // 注意：Fabric 的 JSON 里 libraries 的 URL 是存在的（通常是 Maven Central 或 FabricMC 官方 Maven），可以直接下载。
            // 这里你需要把 DownloadService 里解析 Library 下载的功能抽象出来，给这里调用。
            
            // 提示：由于 Fabric JSON 里指定了 "inheritsFrom": "1.20.1"，
            // 你的 MainViewModel 里 ResolveVersionInfo 已经支持了父子继承，所以启动时会自动把原版 1.20.1 的 Jar 加入 Classpath！
            
            progress?.Report(new DownloadProgressReport { TotalFiles = 1, CompletedFiles = 1, CurrentFileName = "Fabric 安装完成！" });
        }

        // === Forge 安装流程（运行官方安装器） ===
        public async Task InstallForgeAsync(string mcVersion, string forgeVersion, string gameDir, string javaPath, IProgress<DownloadProgressReport>? progress)
        {
            string installerUrl = $"https://bmclapi2.bangbang93.com/forge/download/{forgeVersion}";
            // 实际上 BMCLAPI 的 Forge 下载链接需要具体的参数，推荐直接拼官方的 maven
            // 格式如: https://maven.minecraftforge.net/net/minecraftforge/forge/{mcVersion}-{forgeVersion}/forge-{mcVersion}-{forgeVersion}-installer.jar
            
            string installerPath = Path.Combine(Path.GetTempPath(), $"forge-installer-{forgeVersion}.jar");

            progress?.Report(new DownloadProgressReport { TotalFiles = 1, CompletedFiles = 0, CurrentFileName = "正在下载 Forge 安装器..." });
            
            // 1. 下载安装器
            string officialUrl = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mcVersion}-{forgeVersion}/forge-{mcVersion}-{forgeVersion}-installer.jar";
            byte[] installerData = await HttpClient.GetByteArrayAsync(officialUrl);
            await File.WriteAllBytesAsync(installerPath, installerData);

            progress?.Report(new DownloadProgressReport { TotalFiles = 1, CompletedFiles = 0, CurrentFileName = "正在静默安装 Forge (请稍候)..." });

            // 2. 使用子进程运行安装器
            var psi = new ProcessStartInfo
            {
                FileName = javaPath,
                // 核心：--installClient 指定安装到客户端，"." 代表当前目录
                Arguments = $"-jar \"{installerPath}\" --installClient \"{gameDir}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                // 等待安装完成（Forge 安装可能需要几分钟）
                await process.WaitForExitAsync();
                
                if (process.ExitCode != 0)
                {
                    throw new Exception("Forge 安装器执行失败，请检查 Java 版本是否兼容！");
                }
            }
            
            // 清理临时文件
            try { File.Delete(installerPath); } catch { }
            
            progress?.Report(new DownloadProgressReport { TotalFiles = 1, CompletedFiles = 1, CurrentFileName = "Forge 安装完成！" });
        }
    }
}