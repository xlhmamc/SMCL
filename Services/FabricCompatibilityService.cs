using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SMCL.Models;

namespace SMCL.Services
{
    /// <summary>
    /// 单个 Fabric 模组的兼容性问题。
    /// <paramref name="ContainerModId"/> 为直接位于 mods 目录的顶层模组 ID，
    /// <paramref name="ModId"/> 可能是其嵌套子模块（如 Fabric API 的子模块）。
    /// </summary>
    public sealed record FabricModIssue(
        string ContainerModId,
        string ModId,
        string ModName,
        string ModVersion,
        string? LoaderConstraint);

    /// <summary>
    /// Fabric 实例兼容性检查结果。
    /// </summary>
    public sealed record FabricCompatibilityReport(
        bool IsFabric,
        string? MinecraftVersion,
        string? LoaderVersion,
        IReadOnlyList<FabricModIssue> Issues)
    {
        public bool IsCompatible => Issues.Count == 0;
    }

    /// <summary>
    /// 安装/启动前的 Fabric 兼容性检查。
    ///
    /// 通过读取实例版本 JSON 中的 <c>net.fabricmc:fabric-loader</c> 库版本，
    /// 再逐个解析 mods 目录内 Jar 的 <c>fabric.mod.json</c>：
    /// <c>depends.fabricloader</c> 约束必须被当前 Loader 满足。
    ///
    /// 如果唯一的不兼容项是启动器自动安装的 Fabric API，会自动替换为与当前
    /// Loader 兼容的版本；其他第三方模组的问题只报告，不擅自修改用户文件。
    /// </summary>
    public class FabricCompatibilityService
    {
        private const string FabricLoaderLibraryPrefix = "net.fabricmc:fabric-loader:";
        private const string FabricApiModId = "fabric-api";

        private readonly ModService _modService = new();

        public async Task<FabricCompatibilityReport> CheckAsync(
            string versionDir,
            string versionName,
            string modsDir,
            CancellationToken cancellationToken = default)
        {
            string versionJsonPath = Path.Combine(versionDir, $"{versionName}.json");
            if (!File.Exists(versionJsonPath))
            {
                return new FabricCompatibilityReport(false, null, null, Array.Empty<FabricModIssue>());
            }

            string json = await File.ReadAllTextAsync(versionJsonPath, cancellationToken);

            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string? loaderVersion = GetFabricLoaderVersion(root);
            if (string.IsNullOrWhiteSpace(loaderVersion))
            {
                return new FabricCompatibilityReport(false, null, null, Array.Empty<FabricModIssue>());
            }

            string? minecraftVersion = GetString(root, "inheritsFrom");
            var issues = new List<FabricModIssue>();

            if (Directory.Exists(modsDir))
            {
                foreach (string jarPath in Directory.EnumerateFiles(modsDir, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!jarPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;

                    IReadOnlyList<ModService.FabricModInfo> modInfos = ModService.ReadFabricModInfos(jarPath);
                    if (modInfos.Count == 0) continue;

                    string containerModId = modInfos[0].ModId;

                    foreach (var info in modInfos)
                    {
                        if (string.IsNullOrWhiteSpace(info.LoaderConstraint)) continue;
                        if (FabricVersionUtil.Satisfies(loaderVersion, info.LoaderConstraint)) continue;

                        issues.Add(new FabricModIssue(
                            ContainerModId: containerModId,
                            ModId: info.ModId,
                            ModName: info.ModName,
                            ModVersion: info.ModVersion,
                            LoaderConstraint: info.LoaderConstraint));
                    }
                }
            }

            return new FabricCompatibilityReport(
                IsFabric: true,
                MinecraftVersion: string.IsNullOrWhiteSpace(minecraftVersion) ? null : minecraftVersion,
                LoaderVersion: loaderVersion,
                Issues: issues);
        }

        /// <summary>
        /// 检查并在安全前提下修复兼容性：
        /// 仅当不兼容项全部来自 Fabric API（启动器自动安装的前置模组）时，
        /// 自动下载并替换成与当前 Loader 兼容的版本。
        /// </summary>
        public async Task<FabricCompatibilityReport> EnsureCompatibleAsync(
            string versionDir,
            string versionName,
            string modsDir,
            IProgress<DownloadProgressReport>? progress = null,
            Action<string>? log = null,
            CancellationToken cancellationToken = default)
        {
            var report = await CheckAsync(versionDir, versionName, modsDir, cancellationToken);

            if (!report.IsFabric || report.IsCompatible)
                return report;

            if (string.IsNullOrWhiteSpace(report.MinecraftVersion) ||
                string.IsNullOrWhiteSpace(report.LoaderVersion))
            {
                return report;
            }

            bool onlyAutoManagedFabricApi = report.Issues.All(issue =>
                string.Equals(issue.ContainerModId, FabricApiModId, StringComparison.OrdinalIgnoreCase));

            if (!onlyAutoManagedFabricApi)
                return report;

            log?.Invoke(
                $"检测到 Fabric API 与 Fabric Loader {report.LoaderVersion} 不兼容，" +
                "正在自动安装兼容版本...");

            try
            {
                var result = await _modService.EnsureFabricApiAsync(
                    report.MinecraftVersion,
                    report.LoaderVersion,
                    modsDir,
                    progress,
                    cancellationToken);

                string note = string.IsNullOrWhiteSpace(result.Note) ? string.Empty : $"（{result.Note}）";
                log?.Invoke($"已自动安装兼容的 Fabric API {result.VersionNumber}: {result.FilePath}{note}");
                log?.Invoke("提示：如需使用最新版 Fabric API，请重新安装实例并选择满足其要求的 Fabric Loader。");

                return await CheckAsync(versionDir, versionName, modsDir, cancellationToken);
            }
            catch (Exception ex)
            {
                log?.Invoke($"自动安装兼容 Fabric API 失败：{ex.Message}");
                return report;
            }
        }

        private static string? GetFabricLoaderVersion(JsonElement root)
        {
            if (!root.TryGetProperty("libraries", out var libraries) ||
                libraries.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var library in libraries.EnumerateArray())
            {
                string name = GetString(library, "name");
                if (!name.StartsWith(FabricLoaderLibraryPrefix, StringComparison.Ordinal))
                    continue;

                string[] parts = name.Split(':');
                if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]))
                    return parts[2];
            }

            return null;
        }

        private static string GetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
    }
}
