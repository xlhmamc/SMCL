using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using SMCL.Models;
using System.Text;
using System.Security.Cryptography;
using System.IO;

namespace SMCL.Services
{
    public class ModrinthService
    {
        private readonly HttpClient _httpClient;

        public ModrinthService()
        {
            var handler = new HttpClientHandler();
            string? envProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? 
                               Environment.GetEnvironmentVariable("HTTP_PROXY") ?? 
                               Environment.GetEnvironmentVariable("ALL_PROXY");
            if (!string.IsNullOrEmpty(envProxy))
            {
                handler.Proxy = new WebProxy(envProxy);
                handler.UseProxy = true;
            }
            _httpClient = new HttpClient(handler);
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SMCL-Launcher/1.0 (Windows/Linux; xlhmamc)");
            _httpClient.Timeout = TimeSpan.FromSeconds(30); 
        }

        // 1. 搜索模组
        public async Task<ModrinthSearchResponse> SearchAsync(string query, string mcVersion, string loader, int offset, int limit = 20)
        {
            var response = new ModrinthSearchResponse();
            try
            {
                string facets = "";
                if (loader.ToLower() != "vanilla")
                {
                    facets = $"[[\"versions:{mcVersion}\"],[\"categories:{loader.ToLower()}\"]]";
                }
                else
                {
                    facets = $"[[\"versions:{mcVersion}\"]]";
                }

                // 加入 offset 和 limit 参数
                string url = $"https://api.modrinth.com/v2/search?query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facets)}&limit={limit}&offset={offset}&index=downloads";
                
                var json = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                
                response.TotalHits = doc.RootElement.GetProperty("total_hits").GetInt32();
                var hits = doc.RootElement.GetProperty("hits");
                
                foreach (var hit in hits.EnumerateArray())
                {
                    response.Results.Add(new ModrinthSearchResult
                    {
                        ProjectId = hit.GetProperty("project_id").GetString() ?? "",
                        Title = hit.GetProperty("title").GetString() ?? "",
                        Description = hit.GetProperty("description").GetString() ?? "",
                        IconUrl = hit.GetProperty("icon_url").GetString() ?? "",
                        Author = hit.GetProperty("author").GetString() ?? "",
                        Downloads = hit.GetProperty("downloads").GetInt32()
                    });
                }
                return response;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Modrinth] 搜索异常: {ex.Message}");
                return response;
            }
        }

        // 2. 获取特定模组的所有历史版本并按 MC 版本分组
        public async Task<List<ModVersionGroup>> GetAllVersionsGroupedAsync(string projectId, string currentMcVersion)
        {
            try
            {
                string url = $"https://api.modrinth.com/v2/project/{projectId}/version";
                var response = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);
                
                var allVersions = new List<ModrinthVersion>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    allVersions.Add(ParseVersion(item));
                }

                var groups = new List<ModVersionGroup>();
                var distinctGameVersions = allVersions.SelectMany(v => v.GameVersions).Distinct();

                foreach (var gv in distinctGameVersions)
                {
                    var group = new ModVersionGroup { GameVersion = gv };
                    group.Versions = allVersions.Where(v => v.GameVersions.Contains(gv))
                                                .OrderByDescending(v => v.DatePublished)
                                                .ToList();
                    
                    if (gv == currentMcVersion) group.IsExpanded = true; 
                    groups.Add(group);
                }

                return groups.OrderByDescending(g => g.GameVersion).ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Modrinth] 获取版本列表失败: {ex.Message}");
                return new List<ModVersionGroup>();
            }
        }

        // 3. 下载图片
        public async Task<Avalonia.Media.Imaging.Bitmap?> DownloadImageAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            try
            {
                // 打印正在下载的图片地址
                Debug.WriteLine($"[Modrinth Image] Loading: {url}");
                byte[] data = await _httpClient.GetByteArrayAsync(url);
                using var ms = new System.IO.MemoryStream(data);
                return new Avalonia.Media.Imaging.Bitmap(ms);
            }
            catch (Exception ex)
            {
                // 打印失败原因（如果是因为没开代理导致的超时，这里会显示）
                Debug.WriteLine($"[Modrinth Image] Failed: {ex.Message}");
                return null;
            }
        }

        // 4. 通过 VersionId 获取特定版本（用于依赖解析）
        public async Task<ModrinthVersion?> GetVersionByIdAsync(string versionId)
        {
            try
            {
                string url = $"https://api.modrinth.com/v2/version/{versionId}";
                var response = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);
                return ParseVersion(doc.RootElement);
            }
            catch { return null; }
        }

        // 5. 通过 ProjectId 获取兼容当前环境的最新版本（用于依赖解析）
        public async Task<ModrinthVersion?> GetLatestCompatibleVersionAsync(string projectId, string mcVersion, string loader)
        {
            try
            {
                string url = $"https://api.modrinth.com/v2/project/{projectId}/version?game_versions=[\"{mcVersion}\"]&loaders=[\"{loader.ToLower()}\"]";
                var response = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.GetArrayLength() > 0)
                {
                    return ParseVersion(doc.RootElement[0]);
                }
                return null;
            }
            catch { return null; }
        }

        // 6. 统一的解析辅助方法
        private ModrinthVersion ParseVersion(JsonElement item)
        {
            var version = new ModrinthVersion
            {
                Id = item.GetProperty("id").GetString() ?? "",
                Name = item.GetProperty("name").GetString() ?? "",
                VersionNumber = item.GetProperty("version_number").GetString() ?? "",
                VersionType = item.GetProperty("version_type").GetString() ?? "release",
                DatePublished = item.GetProperty("date_published").GetDateTime()
            };

            foreach (var gv in item.GetProperty("game_versions").EnumerateArray())
                version.GameVersions.Add(gv.GetString() ?? "");
                
            foreach (var l in item.GetProperty("loaders").EnumerateArray())
                version.Loaders.Add(l.GetString() ?? "");

            var files = item.GetProperty("files");
            if (files.GetArrayLength() > 0)
            {
                var file = files[0];
                version.DownloadUrl = file.GetProperty("url").GetString() ?? "";
                version.FileName = file.GetProperty("filename").GetString() ?? "";
            }

            if (item.TryGetProperty("dependencies", out var deps))
            {
                foreach (var dep in deps.EnumerateArray())
                {
                    version.Dependencies.Add(new ModrinthDependency
                    {
                        ProjectId = dep.TryGetProperty("project_id", out var pid) ? pid.GetString() ?? "" : "",
                        VersionId = dep.TryGetProperty("version_id", out var vid) ? vid.GetString() ?? "" : "",
                        FileName = dep.TryGetProperty("file_name", out var fn) ? fn.GetString() ?? "" : "",
                        DependencyType = dep.TryGetProperty("dependency_type", out var dt) ? dt.GetString() ?? "" : ""
                    });
                }
            }
            return version;
        }

        public async Task<HashSet<string>> GetLocalProjectIdsAsync(string modsDir)
        {
            var projectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(modsDir)) return projectIds;

            var jarFiles = Directory.GetFiles(modsDir, "*.jar");
            if (jarFiles.Length == 0) return projectIds;

            var hashList = new List<string>();
            
            foreach (var file in jarFiles)
            {
                try
                {
                    using var stream = File.OpenRead(file);
                    byte[] hashBytes = SHA1.HashData(stream);
                    string hash = Convert.ToHexString(hashBytes).ToLower();
                    hashList.Add(hash);
                }
                catch { }
            }

            if (hashList.Count == 0) return projectIds;

            try
            {
                // 构造 POST 请求体：{"hashes": ["...", "..."], "algorithm": "sha1"}
                var requestBody = new { hashes = hashList, algorithm = "sha1" };
                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync("https://api.modrinth.com/v2/version_files", content);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                
                // 返回结果是一个字典，键是 SHA1，值是版本信息
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("project_id", out var pid))
                    {
                        projectIds.Add(pid.GetString() ?? "");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Modrinth] 扫描本地模组失败: {ex.Message}");
            }

            return projectIds;
        }

        public async Task<Dictionary<string, string>> GetIconUrlsFromHashesAsync(List<string> hashes)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (hashes.Count == 0) return result;

            try
            {
                var requestBody = new { hashes = hashes, algorithm = "sha1" };
                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync("https://api.modrinth.com/v2/version_files", content);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("project_id", out var pid))
                    {
                        // 其实这里拿不到直接的 IconUrl，需要再查一次 project
                        // 为了速度，我们暂存 ProjectId，后续请求 project 详情
                        result[prop.Name] = pid.GetString() ?? ""; 
                    }
                }
            }
            catch { }
            return result;
        }

        public async Task<string?> GetProjectIconUrlAsync(string projectId)
        {
            try
            {
                var response = await _httpClient.GetStringAsync($"https://api.modrinth.com/v2/project/{projectId}");
                using var doc = JsonDocument.Parse(response);
                return doc.RootElement.GetProperty("icon_url").GetString();
            }
            catch { return null; }
        }
    }
}