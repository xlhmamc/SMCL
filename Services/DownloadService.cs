using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SMCL.Models;
using System.Runtime.InteropServices;

namespace SMCL.Services
{
    public class DownloadService
    {
        private static readonly HttpClient HttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

                // 移除原来的 bool UseBmclapi
        // 新增：0=BMCLAPI, 1=MCBBS备用, 2=官方源
        public int DownloadSourceIndex { get; set; } = 0; 

        public async Task<VersionManifest?> GetVersionManifestAsync()
        {
            string url = DownloadSourceIndex switch
            {
                0 => "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
                1 => "https://download.mcbbs.net/mc/game/version_manifest_v2.json", // MCBBS 镜像（现多已失效，做备用）
                _ => "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json" // 官方源
            };

            string json = await HttpClient.GetStringAsync(url);
            return JsonSerializer.Deserialize<VersionManifest>(json);
        }

        public async Task DownloadGameVersionAsync(string versionId, string versionJsonUrl, string gameDir, IProgress<DownloadProgressReport>? progress = null)
        {
            string versionsDir = Path.Combine(gameDir, "versions", versionId);
            string nativesDir = Path.Combine(versionsDir, "natives"); 
            Directory.CreateDirectory(versionsDir);
            Directory.CreateDirectory(nativesDir); 

            progress?.Report(new DownloadProgressReport { TotalFiles = 1, CompletedFiles = 0, CurrentFileName = $"正在获取 {versionId}.json..." });

            // 1. 下载并保存版本专属 JSON
            string targetJsonUrl = ReplaceMirrorUrl(versionJsonUrl);
            string jsonPath = Path.Combine(versionsDir, $"{versionId}.json");
            string versionJsonContent = await HttpClient.GetStringAsync(targetJsonUrl);
            await File.WriteAllTextAsync(jsonPath, versionJsonContent);

            using var doc = JsonDocument.Parse(versionJsonContent);
            var root = doc.RootElement;

            List<DownloadItem> downloadQueue = new();
            List<string> nativesJarPaths = new(); 

            // 2. 解析客户端核心 Jar
            if (root.TryGetProperty("downloads", out var downloads) && downloads.TryGetProperty("client", out var client))
            {
                string clientUrl = client.GetProperty("url").GetString()!;
                string clientSha1 = client.GetProperty("sha1").GetString() ?? "";
                long clientSize = client.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;

                downloadQueue.Add(new DownloadItem
                {
                    Url = ReplaceMirrorUrl(clientUrl),
                    LocalPath = Path.Combine(versionsDir, $"{versionId}.jar"),
                    Sha1 = clientSha1,
                    Size = clientSize
                });
            }

            // 3. 解析 Libraries 依赖库
            if (root.TryGetProperty("libraries", out var libraries))
            {
                string currentOsKey = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" :
                                    RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "windows";

                foreach (var lib in libraries.EnumerateArray())
                {
                    // 检查 rules 规则
                    if (lib.TryGetProperty("rules", out var rulesElem))
                    {
                        bool allow = false;
                        foreach (var rule in rulesElem.EnumerateArray())
                        {
                            string action = rule.GetProperty("action").GetString() ?? "allow";
                            bool matchOs = true;
                            if (rule.TryGetProperty("os", out var osElem))
                            {
                                matchOs = false;
                                if (osElem.TryGetProperty("name", out var nameElem))
                                {
                                    string osName = nameElem.GetString() ?? "";
                                    if (osName == "linux" && RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) matchOs = true;
                                    else if (osName == "windows" && RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) matchOs = true;
                                    else if (osName == "osx" && RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) matchOs = true;
                                }
                            }
                            if (action == "allow" && matchOs) allow = true;
                            else if (action == "disallow" && matchOs) allow = false;
                        }
                        if (!allow) continue;
                    }

                    if (lib.TryGetProperty("downloads", out var libDownloads))
                    {
                                                // A. 下载普通依赖库
                        if (libDownloads.TryGetProperty("artifact", out var artifact))
                        {
                            string path = artifact.GetProperty("path").GetString()!;
                            string url = ReplaceMirrorUrl(artifact.GetProperty("url").GetString()!);
                            string sha1 = artifact.GetProperty("sha1").GetString() ?? "";
                            long size = artifact.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                            string fullJarPath = Path.Combine(gameDir, "libraries", path);

                            downloadQueue.Add(new DownloadItem
                            {
                                Url = url,
                                LocalPath = fullJarPath,
                                Sha1 = sha1,
                                Size = size
                            });

                            // 【新增】兼容 1.19+ 版本的 natives 识别
                            string libName = lib.TryGetProperty("name", out var nameElem) ? nameElem.GetString() ?? "" : "";
                            if ((libName.Contains("natives-") || path.Contains("natives-")) && fullJarPath.Contains(currentOsKey))
                            {
                                if (!nativesJarPaths.Contains(fullJarPath))
                                {
                                    nativesJarPaths.Add(fullJarPath);
                                }
                            }
                        }

                        // B. 下载当前平台专属的 natives classifiers
                        if (lib.TryGetProperty("natives", out var nativesElem) && nativesElem.TryGetProperty(currentOsKey, out var classifierKeyProp))
                        {
                            string classifierKey = classifierKeyProp.GetString()!
                                .Replace("${arch}", RuntimeInformation.OSArchitecture == Architecture.X64 ? "64" : "32");

                            if (libDownloads.TryGetProperty("classifiers", out var classifiersElem) && classifiersElem.TryGetProperty(classifierKey, out var classifierObj))
                            {
                                string path = classifierObj.GetProperty("path").GetString()!;
                                string url = ReplaceMirrorUrl(classifierObj.GetProperty("url").GetString()!);
                                string sha1 = classifierObj.GetProperty("sha1").GetString() ?? "";
                                long size = classifierObj.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                                
                                string fullJarPath = Path.Combine(gameDir, "libraries", path);

                                downloadQueue.Add(new DownloadItem
                                {
                                    Url = url,
                                    LocalPath = fullJarPath,
                                    Sha1 = sha1,
                                    Size = size
                                });
                                
                                nativesJarPaths.Add(fullJarPath); 
                            }
                        }
                    }
                }
            }

            // 4. 解析 Assets 资源文件
            if (root.TryGetProperty("assetIndex", out var assetIndex))
            {
                string indexId = assetIndex.GetProperty("id").GetString()!;
                string indexUrl = ReplaceMirrorUrl(assetIndex.GetProperty("url").GetString()!);
                string indexSha1 = assetIndex.GetProperty("sha1").GetString() ?? "";

                string indexDir = Path.Combine(gameDir, "assets", "indexes");
                Directory.CreateDirectory(indexDir);
                string indexFilePath = Path.Combine(indexDir, $"{indexId}.json");

                string indexJson = await HttpClient.GetStringAsync(indexUrl);
                await File.WriteAllTextAsync(indexFilePath, indexJson);

                using var indexDoc = JsonDocument.Parse(indexJson);
                if (indexDoc.RootElement.TryGetProperty("objects", out var objects))
                {
                    foreach (var obj in objects.EnumerateObject())
                    {
                        string hash = obj.Value.GetProperty("hash").GetString()!;
                        string subFolder = hash.Substring(0, 2);
                        string baseAssetUrl = "https://resources.download.minecraft.net";
                        if (DownloadSourceIndex == 0) baseAssetUrl = "https://bmclapi2.bangbang93.com/assets";
                        else if (DownloadSourceIndex == 1) baseAssetUrl = "https://download.mcbbs.net/assets";
                        
                        string assetUrl = $"{baseAssetUrl}/{subFolder}/{hash}";
                        downloadQueue.Add(new DownloadItem
                        {
                            Url = assetUrl,
                            LocalPath = Path.Combine(gameDir, "assets", "objects", subFolder, hash),
                            Sha1 = hash
                        });
                    }
                }
            }

            if (downloadQueue.Count == 0)
            {
                throw new Exception("解析下载队列失败：没有找到任何可下载的文件项！");
            }

            // 5. 多线程并发下载所有收集到的文件
            await BatchDownloadAsync(downloadQueue, 16, progress);

            // 6. 核心修复 - 解压 Natives 动态库
            progress?.Report(new DownloadProgressReport { TotalFiles = 100, CompletedFiles = 99, CurrentFileName = "正在解压原生库(Natives)..." });
            ExtractNatives(nativesJarPaths, nativesDir);
        }

        // 解压 Natives 的方法
        private void ExtractNatives(List<string> jarPaths, string nativesDir)
        {
            Directory.CreateDirectory(nativesDir);

            foreach (var jarPath in jarPaths)
            {
                if (!File.Exists(jarPath)) continue;

                try
                {
                    using var archive = ZipFile.OpenRead(jarPath);
                    foreach (var entry in archive.Entries)
                    {
                        // 筛选出动态库文件
                        if (entry.FullName.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ||
                            entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                            entry.FullName.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
                        {
                            // 用 Path.GetFileName 去掉 jar 包里面的多级目录，直接解压到 natives 根目录
                            string destPath = Path.Combine(nativesDir, Path.GetFileName(entry.FullName));
                            entry.ExtractToFile(destPath, overwrite: true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Natives Extract Error] 解压失败 {jarPath}: {ex.Message}");
                }
            }
        }

        // 批量下载方法
        private async Task BatchDownloadAsync(List<DownloadItem> items, int maxDegreeOfParallelism, IProgress<DownloadProgressReport>? progress)
        {
            int total = items.Count;
            int completed = 0;
            using var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);

            var tasks = items.Select(async item =>
            {
                await semaphore.WaitAsync();
                try
                {
                    if (File.Exists(item.LocalPath) && ValidateSha1(item.LocalPath, item.Sha1))
                    {
                        Interlocked.Increment(ref completed);
                        progress?.Report(new DownloadProgressReport { TotalFiles = total, CompletedFiles = completed, CurrentFileName = Path.GetFileName(item.LocalPath) });
                        return;
                    }

                    string? dir = Path.GetDirectoryName(item.LocalPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    byte[] data = await HttpClient.GetByteArrayAsync(item.Url);
                    await File.WriteAllBytesAsync(item.LocalPath, data);

                    Interlocked.Increment(ref completed);
                    progress?.Report(new DownloadProgressReport { TotalFiles = total, CompletedFiles = completed, CurrentFileName = Path.GetFileName(item.LocalPath) });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Download Error] 文件下载失败 -> URL: {item.Url}, 错误: {ex.Message}");
                    Interlocked.Increment(ref completed);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }

        // URL 替换镜像方法
        private string ReplaceMirrorUrl(string url)
        {
            if (DownloadSourceIndex == 2) 
            {
                // 2 = Mojang 官方源，直接原样返回
                return url; 
            }

            if (DownloadSourceIndex == 0)
            {
                // 0 = BMCLAPI
                return url.Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com")
                          .Replace("https://launchermeta.mojang.com", "https://bmclapi2.bangbang93.com")
                          .Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com")
                          .Replace("https://libraries.minecraft.net", "https://bmclapi2.bangbang93.com/libraries")
                          .Replace("https://resources.download.minecraft.net", "https://bmclapi2.bangbang93.com/assets");
            }

            if (DownloadSourceIndex == 1)
            {
                // 1 = MCBBS 镜像 (现多转为其他节点，这里替换为对应路径)
                return url.Replace("https://piston-meta.mojang.com", "https://download.mcbbs.net")
                          .Replace("https://launchermeta.mojang.com", "https://download.mcbbs.net")
                          .Replace("https://piston-data.mojang.com", "https://download.mcbbs.net")
                          .Replace("https://libraries.minecraft.net", "https://download.mcbbs.net/maven")
                          .Replace("https://resources.download.minecraft.net", "https://download.mcbbs.net/assets");
            }

            return url;
        }

        // SHA1 校验方法
        private bool ValidateSha1(string filePath, string expectedSha1)
        {
            if (string.IsNullOrEmpty(expectedSha1)) return true;
            try
            {
                using var sha1 = SHA1.Create();
                using var stream = File.OpenRead(filePath);
                byte[] hashBytes = sha1.ComputeHash(stream);
                string fileHash = Convert.ToHexString(hashBytes).ToLower();
                return string.Equals(fileHash, expectedSha1.ToLower(), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}