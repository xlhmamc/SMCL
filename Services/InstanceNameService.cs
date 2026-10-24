using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SMCL.Services
{
    /// <summary>
    /// 游戏实例（versions/&lt;name&gt;）命名校验与创建辅助。
    ///
    /// 版本目录名会同时用于：
    /// <list type="bullet">
    ///   <item><c>versions/&lt;name&gt;/&lt;name&gt;.json</c></item>
    ///   <item>启动参数 <c>--version &lt;name&gt;</c></item>
    ///   <item>版本隔离时的 <c>--gameDir</c></item>
    /// </list>
    /// 因此必须保证名称是安全的单级路径，且不能覆盖基础游戏版本目录。
    /// </summary>
    public static class InstanceNameService
    {
        private const int MaxNameLength = 64;

        private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>
        /// 解析最终的实例名称。
        /// </summary>
        /// <param name="requestedName">用户输入的名称；为空时使用默认名称</param>
        /// <param name="defaultName">加载器/原版的默认版本目录名</param>
        /// <param name="baseGameVersion">基础 Minecraft 版本（如 1.20.1）</param>
        /// <param name="versionsRoot">&lt;MinecraftRoot&gt;/versions</param>
        /// <param name="isFabricInstance">是否 Fabric 实例；Fabric 不能与基础版目录同名</param>
        /// <param name="finalName">最终使用的目录名</param>
        /// <param name="error">校验失败原因</param>
        public static bool TryResolve(
            string? requestedName,
            string defaultName,
            string baseGameVersion,
            string versionsRoot,
            bool isFabricInstance,
            out string finalName,
            out string? error)
        {
            error = null;

            string candidate = (requestedName ?? string.Empty).Trim();

            if (candidate.Length == 0)
            {
                finalName = defaultName;
                return true;
            }

            if (!IsValidName(candidate, out error))
            {
                finalName = defaultName;
                return false;
            }

            // Fabric 的版本 JSON 依赖 versions/1.20.1 作为 inheritsFrom 父版本，
            // 若允许自定义名等于基础版本，会覆盖原版目录，必须拒绝。
            if (isFabricInstance &&
                string.Equals(candidate, baseGameVersion, StringComparison.OrdinalIgnoreCase))
            {
                finalName = candidate;
                error = $"实例名称不能与基础游戏版本“{baseGameVersion}”相同，否则会覆盖原版版本目录。请换一个名称。";
                return false;
            }

            string targetDir = Path.Combine(versionsRoot, candidate);
            if (Directory.Exists(targetDir) &&
                !string.Equals(candidate, defaultName, StringComparison.Ordinal))
            {
                // 同名目录存在时，只有它继承自同一个基础版本、且加载器类型一致时才允许覆盖（便于重装/修复）
                if (!CanOverwriteExistingInstance(targetDir, candidate, baseGameVersion, isFabricInstance))
                {
                    finalName = candidate;
                    error = $"实例名称已存在：versions/{candidate}（不是基于 Minecraft {baseGameVersion} 的同类实例）。" +
                            "请更换名称或先删除该实例。";
                    return false;
                }
            }

            finalName = candidate;
            return true;
        }

        /// <summary>
        /// 为原版安装创建轻量别名实例：
        /// 只写一个 <c>{ "id": alias, "inheritsFrom": base }</c> 的 JSON，
        /// 实际 Jar、Libraries、Assets 继续复用基础版本目录，避免复制几十 MB 文件。
        /// </summary>
        public static async Task CreateVanillaAliasAsync(
            string versionsRoot,
            string baseVersion,
            string aliasName,
            CancellationToken cancellationToken = default)
        {
            string aliasDir = Path.Combine(versionsRoot, aliasName);
            Directory.CreateDirectory(aliasDir);

            string jsonPath = Path.Combine(aliasDir, $"{aliasName}.json");
            string json = JsonSerializer.Serialize(
                new { id = aliasName, inheritsFrom = baseVersion },
                new JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(jsonPath, json, cancellationToken);
        }

        private static bool CanOverwriteExistingInstance(
            string targetDir,
            string targetName,
            string baseGameVersion,
            bool isFabricInstance)
        {
            string jsonPath = Path.Combine(targetDir, $"{targetName}.json");
            if (!File.Exists(jsonPath)) return false;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                JsonElement root = doc.RootElement;

                if (!root.TryGetProperty("inheritsFrom", out var inherited) ||
                    !string.Equals(inherited.GetString(), baseGameVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                bool hasFabricLoader = false;
                bool hasForgeLoader = false;
                bool hasQuiltLoader = false;

                if (root.TryGetProperty("libraries", out var libraries) &&
                    libraries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var library in libraries.EnumerateArray())
                    {
                        string name = library.TryGetProperty("name", out var nameProp)
                            ? nameProp.GetString() ?? string.Empty
                            : string.Empty;

                        if (name.StartsWith("net.fabricmc:fabric-loader:", StringComparison.Ordinal))
                            hasFabricLoader = true;
                        else if (name.StartsWith("net.minecraftforge:", StringComparison.Ordinal) ||
                                 name.StartsWith("net.neoforged:", StringComparison.Ordinal))
                            hasForgeLoader = true;
                        else if (name.StartsWith("org.quiltmc:quilt-loader:", StringComparison.Ordinal))
                            hasQuiltLoader = true;
                    }
                }

                if (isFabricInstance)
                    return hasFabricLoader;

                // 原版别名实例：不应覆盖 Fabric/Forge/Quilt 实例
                return !hasFabricLoader && !hasForgeLoader && !hasQuiltLoader;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsValidName(string name, out string? error)
        {
            error = null;

            if (name.Length > MaxNameLength)
            {
                error = $"实例名称不能超过 {MaxNameLength} 个字符。";
                return false;
            }

            if (name == "." || name == "..")
            {
                error = "实例名称不能是 . 或 ..。";
                return false;
            }

            if (name.EndsWith(".", StringComparison.Ordinal) ||
                name.EndsWith(" ", StringComparison.Ordinal))
            {
                error = "实例名称不能以点或空格结尾。";
                return false;
            }

            if (name.StartsWith("-", StringComparison.Ordinal))
            {
                error = "实例名称不能以 - 开头（会被误认为启动参数）。";
                return false;
            }

            if (name.Any(char.IsControl))
            {
                error = "实例名称不能包含控制字符。";
                return false;
            }

            // Path.GetInvalidFileNameChars 在 Linux 上只覆盖 '/' 和 NUL，
            // 这里额外加入 Windows 禁止字符，保证配置跨平台可用。
            var invalidChars = new HashSet<char>(Path.GetInvalidFileNameChars());
            foreach (char c in new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' })
            {
                invalidChars.Add(c);
            }

            int invalidIndex = name.IndexOfAny(invalidChars.ToArray());
            if (invalidIndex >= 0)
            {
                error = $"实例名称包含非法字符：{name[invalidIndex]}";
                return false;
            }

            string stem = name.Split('.')[0].Trim();
            if (ReservedWindowsNames.Contains(stem))
            {
                error = $"“{stem}”是 Windows 保留设备名，请换一个实例名称。";
                return false;
            }

            return true;
        }
    }
}
