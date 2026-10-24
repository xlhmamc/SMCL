using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SMCL.Models;

namespace SMCL.Services
{
    /// <summary>
    /// 模组安装服务：负责从 Modrinth（官方 API，失败时回退 MCIM 镜像）解析项目版本，
    /// 校验并下载模组文件到指定 mods 目录。
    ///
    /// Fabric API 的安装会额外读取 Jar 内 <c>fabric.mod.json</c> 的
    /// <c>depends.fabricloader</c> 约束，确保与当前实例的 Fabric Loader 版本兼容。
    /// </summary>
    public class ModService
    {
        private const string FabricApiSlug = "fabric-api";
        private const string FabricLoaderDependencyKey = "fabricloader";

        private const string OfficialApiRoot = "https://api.modrinth.com/v2";
        private const string MirrorApiRoot = "https://mod.mcimirror.top/modrinth/v2";
        private const string MirrorFileRoot = "https://mod.mcimirror.top";

        // 兼容性回退时最多尝试的 Fabric API 版本数量，避免极端情况下无限下载
        private const int MaxFabricApiCompatibilityAttempts = 30;

        private static readonly HttpClient HttpClient = CreateHttpClient();

        /// <summary>
        /// 单个模组的安装结果。
        /// </summary>
        public sealed record ModInstallResult(
            string FilePath,
            string FileName,
            string VersionNumber,
            bool AlreadyInstalled,
            string DownloadSource,
            string? Note = null);

        /// <summary>
        /// 从 <c>fabric.mod.json</c> 中解析出的依赖信息。
        /// </summary>
        public sealed record FabricModInfo(
            string ModId,
            string ModName,
            string ModVersion,
            string? MinecraftConstraint,
            string? LoaderConstraint);

        private sealed record ModVersionCandidate(
            string FileName,
            string FileUrl,
            string MirrorFileUrl,
            string Sha1,
            long Size,
            string VersionNumber,
            string VersionType,
            DateTimeOffset Published);

        private static HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 8
            };

            var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };

            // Modrinth 要求提供可识别的 User-Agent，否则可能返回 403
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "SMCL/1.0 (Minecraft Launcher; +https://github.com/SMCL-Launcher)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

            return client;
        }

        /// <summary>
        /// 确保指定游戏版本、且与 <paramref name="loaderVersion"/> 兼容的 Fabric API 已安装。
        ///
        /// 选择策略：从最新版本开始向下查找，使用 Jar 内 fabric.mod.json 的
        /// <c>depends.fabricloader</c> 约束做真实兼容性判断，而不是假设“最新一定兼容”。
        /// 这样当用户选择旧版 Loader（例如 0.15.11）时，不会装上要求 &gt;=0.16.10 的新版 API。
        /// </summary>
        public async Task<ModInstallResult> EnsureFabricApiAsync(
            string mcVersion,
            string loaderVersion,
            string modsDir,
            IProgress<DownloadProgressReport>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(mcVersion))
                throw new ArgumentException("Minecraft 版本不能为空。", nameof(mcVersion));
            if (string.IsNullOrWhiteSpace(loaderVersion))
                throw new ArgumentException("Fabric Loader 版本不能为空。", nameof(loaderVersion));
            if (string.IsNullOrWhiteSpace(modsDir))
                throw new ArgumentException("mods 目录不能为空。", nameof(modsDir));

            Directory.CreateDirectory(modsDir);

            var candidates = await FindCandidatesAsync(
                FabricApiSlug,
                "Fabric API",
                mcVersion,
                "fabric",
                cancellationToken);

            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    $"未找到适用于 Minecraft {mcVersion} 的 Fabric API。" +
                    "（Modrinth 暂无该游戏版本的 fabric 构建，可能尚未发布或不支持该快照）");
            }

            string latestVersion = candidates[0].VersionNumber;
            string? latestLoaderConstraint = null;
            Exception? lastError = null;

            int attempts = Math.Min(candidates.Count, MaxFabricApiCompatibilityAttempts);

            for (int i = 0; i < attempts; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ModVersionCandidate candidate = candidates[i];

                try
                {
                    progress?.Report(new DownloadProgressReport
                    {
                        TotalFiles = 1,
                        CompletedFiles = 0,
                        CurrentFileName = $"正在校验 Fabric API {candidate.VersionNumber} 与 Loader {loaderVersion} 的兼容性..."
                    });

                    var (data, source) = await DownloadVerifiedAsync(candidate, cancellationToken);
                    var modInfos = ReadFabricModInfos(data);

                    if (modInfos.Count > 0)
                    {
                        bool incompatible = false;

                        foreach (var modInfo in modInfos)
                        {
                            if (!string.IsNullOrWhiteSpace(modInfo.MinecraftConstraint) &&
                                !FabricVersionUtil.Satisfies(mcVersion, modInfo.MinecraftConstraint))
                            {
                                Debug.WriteLine(
                                    $"[ModService] 跳过 Fabric API {candidate.VersionNumber}：" +
                                    $"模块 {modInfo.ModId} 的 minecraft 约束 {modInfo.MinecraftConstraint} 不匹配 {mcVersion}");
                                incompatible = true;
                                break;
                            }

                            if (!FabricVersionUtil.Satisfies(loaderVersion, modInfo.LoaderConstraint))
                            {
                                Debug.WriteLine(
                                    $"[ModService] 跳过 Fabric API {candidate.VersionNumber}：" +
                                    $"模块 {modInfo.ModId} 要求 fabricloader {modInfo.LoaderConstraint}，当前 {loaderVersion}");
                                incompatible = true;

                                if (i == 0 && string.IsNullOrWhiteSpace(latestLoaderConstraint))
                                {
                                    latestLoaderConstraint = modInfo.LoaderConstraint;
                                }

                                break;
                            }
                        }

                        if (incompatible) continue;
                    }

                    string? note = null;
                    if (i > 0 && !string.IsNullOrWhiteSpace(latestLoaderConstraint))
                    {
                        note = $"Fabric Loader {loaderVersion} 不满足最新版 Fabric API {latestVersion}" +
                               $"（要求 {latestLoaderConstraint}），已自动选择兼容版本 {candidate.VersionNumber}";
                    }

                    return await WriteJarAsync(
                        data,
                        candidate.FileName,
                        candidate.Sha1,
                        modsDir,
                        FabricApiSlug,
                        "Fabric API",
                        candidate.VersionNumber,
                        source,
                        note,
                        progress,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Debug.WriteLine($"[ModService] 检查 Fabric API {candidate.VersionNumber} 失败: {ex.Message}");
                }
            }

            string requirement = latestLoaderConstraint != null
                ? $"（最新版 {latestVersion} 要求 Fabric Loader {latestLoaderConstraint}）"
                : string.Empty;
            string lastErrorText = lastError != null ? $"；最后一次错误：{lastError.Message}" : string.Empty;

            throw new InvalidOperationException(
                $"未找到与 Fabric Loader {loaderVersion} 兼容的 Fabric API{requirement}{lastErrorText}。" +
                "请升级 Fabric Loader 后重试。");
        }

        /// <summary>
        /// 通用 Modrinth 项目安装：查询游戏版本 + 加载器兼容的最新文件，校验 SHA1 后落盘。
        /// 该方法不做 Loader 版本约束检查，适合不依赖 Fabric Loader 新 API 的普通模组。
        /// </summary>
        public async Task<ModInstallResult> InstallProjectAsync(
            string slug,
            string displayName,
            string mcVersion,
            string loader,
            string modsDir,
            IProgress<DownloadProgressReport>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(slug))
                throw new ArgumentException("模组项目标识不能为空。", nameof(slug));
            if (string.IsNullOrWhiteSpace(mcVersion))
                throw new ArgumentException("Minecraft 版本不能为空。", nameof(mcVersion));
            if (string.IsNullOrWhiteSpace(modsDir))
                throw new ArgumentException("mods 目录不能为空。", nameof(modsDir));

            Directory.CreateDirectory(modsDir);

            progress?.Report(new DownloadProgressReport
            {
                TotalFiles = 1,
                CompletedFiles = 0,
                CurrentFileName = $"正在查询 {displayName} 兼容版本 ({mcVersion}/{loader})..."
            });

            var candidate = await FindBestCandidateAsync(slug, displayName, mcVersion, loader, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"未找到适用于 Minecraft {mcVersion} 的 {displayName}。" +
                    $"（Modrinth 暂无该游戏版本的 {loader} 构建，可能尚未发布或不支持该快照）");

            var (data, source) = await DownloadVerifiedAsync(candidate, cancellationToken);

            return await WriteJarAsync(
                data,
                candidate.FileName,
                candidate.Sha1,
                modsDir,
                slug,
                displayName,
                candidate.VersionNumber,
                source,
                null,
                progress,
                cancellationToken);
        }

        /// <summary>
        /// 从 Jar 文件读取 <c>fabric.mod.json</c> 依赖信息；非 Fabric 模组返回 null。
        /// </summary>
        public static FabricModInfo? TryReadFabricModInfo(string jarPath)
        {
            return ReadFabricModInfos(jarPath).FirstOrDefault();
        }

        /// <summary>
        /// 读取 Jar 以及 <c>META-INF/jars/*.jar</c> 下嵌套模块的依赖信息。
        /// Fabric API 的子模块（fabric-object-builder-api-v1 等）就位于嵌套 Jar 中，
        /// 它们的 <c>depends.fabricloader</c> 也必须参与兼容性判断。
        /// </summary>
        public static IReadOnlyList<FabricModInfo> ReadFabricModInfos(string jarPath)
        {
            try
            {
                using var stream = File.OpenRead(jarPath);
                return ReadFabricModInfos(stream);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModService] 读取 fabric.mod.json 失败 {jarPath}: {ex.Message}");
                return Array.Empty<FabricModInfo>();
            }
        }

        private static IReadOnlyList<FabricModInfo> ReadFabricModInfos(byte[] jarData)
        {
            try
            {
                using var stream = new MemoryStream(jarData, writable: false);
                return ReadFabricModInfos(stream);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModService] 从内存解析 fabric.mod.json 失败: {ex.Message}");
                return Array.Empty<FabricModInfo>();
            }
        }

        private static IReadOnlyList<FabricModInfo> ReadFabricModInfos(Stream jarStream)
        {
            var result = new List<FabricModInfo>();

            try
            {
                using var archive = new ZipArchive(jarStream, ZipArchiveMode.Read, leaveOpen: true);

                FabricModInfo? topLevelInfo = ReadFabricModInfo(archive);
                if (topLevelInfo != null) result.Add(topLevelInfo);

                int nestedCount = 0;
                foreach (var entry in archive.Entries)
                {
                    if (nestedCount >= 128) break;

                    if (!entry.FullName.StartsWith("META-INF/jars/", StringComparison.Ordinal) ||
                        !entry.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                        entry.Length > 32 * 1024 * 1024)
                    {
                        continue;
                    }

                    nestedCount++;

                    try
                    {
                        using Stream nestedStream = entry.Open();
                        using var nestedMemory = new MemoryStream();
                        nestedStream.CopyTo(nestedMemory);
                        nestedMemory.Position = 0;

                        using var nestedArchive = new ZipArchive(nestedMemory, ZipArchiveMode.Read);
                        FabricModInfo? nestedInfo = ReadFabricModInfo(nestedArchive);
                        if (nestedInfo != null) result.Add(nestedInfo);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ModService] 解析嵌套模块失败 {entry.FullName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModService] 读取 Jar 失败: {ex.Message}");
            }

            return result;
        }

        private static FabricModInfo? ReadFabricModInfo(ZipArchive archive)
        {
            ZipArchiveEntry? entry = archive.GetEntry("fabric.mod.json");
            if (entry == null) return null;

            using Stream entryStream = entry.Open();
            using var doc = JsonDocument.Parse(entryStream);
            JsonElement root = doc.RootElement;

            string modId = GetString(root, "id");
            if (string.IsNullOrWhiteSpace(modId)) return null;

            string modName = GetString(root, "name");
            if (string.IsNullOrWhiteSpace(modName)) modName = modId;

            return new FabricModInfo(
                ModId: modId,
                ModName: modName,
                ModVersion: GetString(root, "version"),
                MinecraftConstraint: ReadDependencyConstraint(root, "minecraft"),
                LoaderConstraint: ReadDependencyConstraint(root, FabricLoaderDependencyKey));
        }

        private static string? ReadDependencyConstraint(JsonElement root, string dependencyKey)
        {
            if (!root.TryGetProperty("depends", out var depends) ||
                depends.ValueKind != JsonValueKind.Object ||
                !depends.TryGetProperty(dependencyKey, out var value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                string? text = value.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                // Fabric Loader 的语义：数组中的多个 version predicate 是“或”关系
                // （ModDependencyImpl.matches 只要任意一个 predicate 命中即通过），
                // 因此用 " || " 连接，交给 FabricVersionUtil 按 OR 处理。
                var predicates = value.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s));

                string joined = string.Join(" || ", predicates);
                return string.IsNullOrWhiteSpace(joined) ? null : joined;
            }

            return null;
        }

        // ================= 查询与下载 =================

        private static async Task<ModVersionCandidate?> FindBestCandidateAsync(
            string slug,
            string displayName,
            string mcVersion,
            string loader,
            CancellationToken cancellationToken)
        {
            var candidates = await FindCandidatesAsync(slug, displayName, mcVersion, loader, cancellationToken);
            return candidates.FirstOrDefault();
        }

        /// <summary>
        /// 查询 Modrinth 版本列表（先官方，失败再镜像），返回按优先级排序的候选列表。
        /// </summary>
        private static async Task<List<ModVersionCandidate>> FindCandidatesAsync(
            string slug,
            string displayName,
            string mcVersion,
            string loader,
            CancellationToken cancellationToken)
        {
            // Modrinth 要求 game_versions / loaders 参数为 JSON 数组字符串
            string encodedGameVersions = Uri.EscapeDataString("[\"" + mcVersion + "\"]");
            string encodedLoaders = Uri.EscapeDataString("[\"" + loader + "\"]");
            string query = $"?game_versions={encodedGameVersions}&loaders={encodedLoaders}";

            var failures = new List<Exception>();
            bool anyApiResponded = false;

            foreach (string apiRoot in new[] { OfficialApiRoot, MirrorApiRoot })
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    string url = $"{apiRoot}/project/{slug}/version{query}";

                    // 单个 API 节点最多等待 20 秒，避免官方源被墙时长时间卡住
                    using var apiTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    apiTimeout.CancelAfter(TimeSpan.FromSeconds(20));

                    string json = await HttpClient.GetStringAsync(url, apiTimeout.Token);

                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                        continue;

                    anyApiResponded = true;

                    var candidates = new List<ModVersionCandidate>();
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        var parsed = ParseCandidate(item, mcVersion, loader);
                        if (parsed != null) candidates.Add(parsed);
                    }

                    if (candidates.Count == 0)
                        continue;

                    return candidates
                        .OrderBy(c => VersionTypeRank(c.VersionType))
                        .ThenByDescending(c => c.Published)
                        .ToList();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    Debug.WriteLine($"[ModService] 查询 {displayName} 失败 ({apiRoot}): {ex.Message}");
                }
            }

            // 至少有一个 API 正常响应过，说明是“确实没有兼容版本”，而不是网络问题
            if (anyApiResponded)
                return new List<ModVersionCandidate>();

            if (failures.Count > 0)
            {
                throw new IOException(
                    $"无法连接 Modrinth 获取 {displayName}（官方源与镜像源均失败）: {failures[^1].Message}",
                    failures[^1]);
            }

            return new List<ModVersionCandidate>();
        }

        private static ModVersionCandidate? ParseCandidate(JsonElement item, string mcVersion, string loader)
        {
            if (!ArrayContainsString(item, "game_versions", mcVersion)) return null;
            if (!ArrayContainsString(item, "loaders", loader)) return null;

            string versionId = GetString(item, "id");
            string projectId = GetString(item, "project_id");
            string versionNumber = GetString(item, "version_number");
            string versionType = GetString(item, "version_type");
            if (string.IsNullOrWhiteSpace(versionType)) versionType = "release";

            DateTimeOffset published = DateTimeOffset.MinValue;
            string? publishedText = GetString(item, "date_published");
            if (!string.IsNullOrWhiteSpace(publishedText))
            {
                DateTimeOffset.TryParse(
                    publishedText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out published);
            }

            if (!item.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                return null;

            JsonElement? primary = null;
            JsonElement? fallback = null;

            foreach (var file in files.EnumerateArray())
            {
                fallback ??= file;
                if (file.TryGetProperty("primary", out var primaryProp) &&
                    primaryProp.ValueKind == JsonValueKind.True)
                {
                    primary = file;
                    break;
                }
            }

            if (primary is null && fallback is null) return null;
            JsonElement selected = primary.HasValue ? primary.Value : fallback!.Value;

            string fileUrl = GetString(selected, "url");
            string fileName = GetString(selected, "filename");
            if (string.IsNullOrWhiteSpace(fileUrl) || string.IsNullOrWhiteSpace(fileName))
                return null;

            long size = selected.TryGetProperty("size", out var sizeProp) &&
                        sizeProp.TryGetInt64(out long parsedSize)
                ? parsedSize
                : 0;

            string sha1 = string.Empty;
            if (selected.TryGetProperty("hashes", out var hashes) &&
                hashes.TryGetProperty("sha1", out var sha1Prop))
            {
                sha1 = sha1Prop.GetString() ?? string.Empty;
            }

            // 镜像文件地址：https://mod.mcimirror.top/data/{project_id}/versions/{version_id}/{file_name}
            string mirrorFileUrl = string.Empty;
            if (!string.IsNullOrEmpty(projectId) && !string.IsNullOrEmpty(versionId))
            {
                mirrorFileUrl =
                    $"{MirrorFileRoot}/data/{Uri.EscapeDataString(projectId)}/versions/" +
                    $"{Uri.EscapeDataString(versionId)}/{Uri.EscapeDataString(fileName)}";
            }

            return new ModVersionCandidate(
                FileName: fileName,
                FileUrl: fileUrl,
                MirrorFileUrl: mirrorFileUrl,
                Sha1: sha1,
                Size: size,
                VersionNumber: versionNumber,
                VersionType: versionType,
                Published: published);
        }

        /// <summary>
        /// 按“官方 CDN -> 镜像 CDN”的顺序下载，并对大小与 SHA1 做完整校验。
        /// 任何一步校验失败都会尝试下一个来源。
        /// </summary>
        private static async Task<(byte[] Data, string Source)> DownloadVerifiedAsync(
            ModVersionCandidate candidate,
            CancellationToken cancellationToken)
        {
            var urls = new List<(string Url, string Source)>
            {
                (candidate.FileUrl, "modrinth")
            };

            if (!string.IsNullOrWhiteSpace(candidate.MirrorFileUrl) &&
                !string.Equals(candidate.MirrorFileUrl, candidate.FileUrl, StringComparison.OrdinalIgnoreCase))
            {
                urls.Add((candidate.MirrorFileUrl, "mcimirror"));
            }

            var failures = new List<Exception>();

            foreach (var (url, source) in urls)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using var response = await HttpClient.GetAsync(
                        url,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

                    response.EnsureSuccessStatusCode();

                    string? mediaType = response.Content.Headers.ContentType?.MediaType;
                    if (!string.IsNullOrEmpty(mediaType) &&
                        mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("服务器返回了 HTML 页面而不是 Jar 文件（可能是下载源错误或限流）。");
                    }

                    byte[] data = await response.Content.ReadAsByteArrayAsync(cancellationToken);

                    if (data.Length == 0)
                        throw new InvalidDataException("下载内容为空。");

                    if (candidate.Size > 0 && data.LongLength != candidate.Size)
                    {
                        throw new InvalidDataException(
                            $"文件大小校验失败：期望 {candidate.Size} 字节，实际 {data.LongLength} 字节。");
                    }

                    if (!string.IsNullOrWhiteSpace(candidate.Sha1))
                    {
                        string actualSha1 = Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
                        if (!string.Equals(actualSha1, candidate.Sha1, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(
                                $"SHA1 校验失败：期望 {candidate.Sha1}，实际 {actualSha1}。");
                        }
                    }

                    return (data, source);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    Debug.WriteLine($"[ModService] 下载失败 ({url}): {ex.Message}");
                }
            }

            throw new IOException(
                $"模组文件下载失败（已尝试 {urls.Count} 个下载源）: {failures[^1].Message}",
                failures[^1]);
        }

        private static async Task<ModInstallResult> WriteJarAsync(
            byte[] data,
            string fileName,
            string expectedSha1,
            string modsDir,
            string slug,
            string displayName,
            string versionNumber,
            string source,
            string? note,
            IProgress<DownloadProgressReport>? progress,
            CancellationToken cancellationToken)
        {
            string safeFileName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(safeFileName) ||
                !safeFileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{displayName} 返回了非法的文件名: {fileName}");
            }

            string targetPath = Path.Combine(modsDir, safeFileName);
            string actualSha1 = string.IsNullOrWhiteSpace(expectedSha1)
                ? Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant()
                : expectedSha1.ToLowerInvariant();

            // 幂等：文件已存在且大小/SHA1 一致则直接复用
            if (File.Exists(targetPath) &&
                new FileInfo(targetPath).Length == data.LongLength &&
                await Sha1MatchesAsync(targetPath, actualSha1, cancellationToken))
            {
                progress?.Report(new DownloadProgressReport
                {
                    TotalFiles = 1,
                    CompletedFiles = 1,
                    CurrentFileName = $"{displayName} 已存在，跳过下载: {safeFileName}"
                });

                return new ModInstallResult(targetPath, safeFileName, versionNumber, true, "local", note);
            }

            string tempPath = targetPath + ".tmp";

            try
            {
                await File.WriteAllBytesAsync(tempPath, data, cancellationToken);
                File.Move(tempPath, targetPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { /* 忽略清理失败 */ }
                }
            }

            // 清理同项目旧版本 Jar，避免重复模组导致游戏崩溃
            RemoveStaleJars(modsDir, slug, safeFileName);

            progress?.Report(new DownloadProgressReport
            {
                TotalFiles = 1,
                CompletedFiles = 1,
                CurrentFileName = $"{displayName} 安装完成: {safeFileName}"
            });

            Debug.WriteLine($"[ModService] {displayName} {versionNumber} -> {targetPath} (来源: {source})");

            return new ModInstallResult(targetPath, safeFileName, versionNumber, false, source, note);
        }

        private static async Task<bool> Sha1MatchesAsync(
            string path,
            string expectedSha1,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(expectedSha1)) return false;

            try
            {
                await using var stream = File.OpenRead(path);
                byte[] hash = await SHA1.HashDataAsync(stream, cancellationToken);
                string actual = Convert.ToHexString(hash).ToLowerInvariant();
                return string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 删除同项目旧版本文件（例如 fabric-api-0.92.12+1.20.1.jar），防止重复加载同一模组。
        /// 这里只针对 Fabric API 这类“文件名前缀固定且不应多版本共存”的模组做清理。
        /// </summary>
        private static void RemoveStaleJars(string modsDir, string slug, string keepFileName)
        {
            if (!string.Equals(slug, FabricApiSlug, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                foreach (string oldJar in Directory.EnumerateFiles(modsDir, "fabric-api-*.jar"))
                {
                    if (string.Equals(Path.GetFileName(oldJar), keepFileName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        File.Delete(oldJar);
                        Debug.WriteLine($"[ModService] 已清理旧版 Fabric API: {oldJar}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ModService] 清理旧文件失败 {oldJar}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModService] 扫描旧版本文件失败: {ex.Message}");
            }
        }

        private static string GetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        private static bool ArrayContainsString(JsonElement element, string propertyName, string expected)
        {
            if (!element.TryGetProperty(propertyName, out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String &&
                    string.Equals(entry.GetString(), expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static int VersionTypeRank(string versionType) => versionType.ToLowerInvariant() switch
        {
            "release" => 0,
            "beta" => 1,
            "alpha" => 2,
            _ => 3
        };
    }
}
