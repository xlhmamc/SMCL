using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SMCL.Services
{
    /// <summary>
    /// 实例所属的模组加载器类型，用于同名实例覆盖校验。
    /// </summary>
    public enum InstanceLoaderKind
    {
        Vanilla,
        Fabric,
        Forge
    }
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
        /// 只校验名称本身（非法字符、Windows 保留名、基础版本冲突），不检查目录是否已存在。
        /// 用于 Forge 这类“默认目录名需要下载安装器后才能确定”的场景，先提前拦截明显错误。
        /// </summary>
        public static bool ValidateNameOnly(
            string? requestedName,
            string baseGameVersion,
            InstanceLoaderKind loaderKind,
            out string? error)
        {
            error = null;
            string candidate = (requestedName ?? string.Empty).Trim();

            if (candidate.Length == 0)
                return true; // 留空 = 使用默认名称，合法

            if (!IsValidName(candidate, out error))
                return false;

            if (loaderKind != InstanceLoaderKind.Vanilla &&
                string.Equals(candidate, baseGameVersion, StringComparison.OrdinalIgnoreCase))
            {
                error = $"实例名称不能与基础游戏版本“{baseGameVersion}”相同，否则会覆盖原版版本目录。请换一个名称。";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 解析最终的实例名称。
        /// </summary>
        /// <param name="requestedName">用户输入的名称；为空时使用默认名称</param>
        /// <param name="defaultName">加载器/原版的默认版本目录名</param>
        /// <param name="baseGameVersion">基础 Minecraft 版本（如 1.20.1）</param>
        /// <param name="versionsRoot">&lt;MinecraftRoot&gt;/versions</param>
        /// <param name="loaderKind">实例的加载器类型</param>
        /// <param name="finalName">最终使用的目录名</param>
        /// <param name="error">校验失败原因</param>
        public static bool TryResolve(
            string? requestedName,
            string defaultName,
            string baseGameVersion,
            string versionsRoot,
            InstanceLoaderKind loaderKind,
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

            // Fabric/Forge 的版本 JSON 依赖 versions/1.20.1 作为 inheritsFrom 父版本，
            // 若允许自定义名等于基础版本，会覆盖原版目录，必须拒绝。
            if (loaderKind != InstanceLoaderKind.Vanilla &&
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
                if (!CanOverwriteExistingInstance(targetDir, candidate, baseGameVersion, loaderKind))
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

        /// <summary>
        /// 重命名一个已安装的版本目录（主要用于 Forge 安装器生成固定名称后改名）：
        /// 1. 目录内的 <c>&lt;old&gt;.json</c> 重命名为 <c>&lt;new&gt;.json</c> 并同步改写 id；
        /// 2. 若存在 <c>&lt;old&gt;.jar</c> 一并重命名；
        /// 3. 最后重命名目录本身。
        /// </summary>
        public static async Task RenameVersionDirectoryAsync(
            string versionsRoot,
            string oldName,
            string newName,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
                return;

            string oldDir = Path.Combine(versionsRoot, oldName);
            string newDir = Path.Combine(versionsRoot, newName);

            if (!Directory.Exists(oldDir))
                throw new DirectoryNotFoundException($"未找到要重命名的实例目录：versions/{oldName}");

            if (Directory.Exists(newDir))
                throw new IOException($"目标实例名已存在：versions/{newName}");

            string oldJson = Path.Combine(oldDir, $"{oldName}.json");
            string newJson = Path.Combine(oldDir, $"{newName}.json");
            string oldJar = Path.Combine(oldDir, $"{oldName}.jar");
            string newJar = Path.Combine(oldDir, $"{newName}.jar");

            bool jsonRenamed = false;
            bool jarRenamed = false;

            try
            {
                if (File.Exists(oldJson))
                {
                    File.Move(oldJson, newJson);
                    jsonRenamed = true;
                    RewriteVersionJsonId(newJson, newName);
                }

                if (File.Exists(oldJar))
                {
                    File.Move(oldJar, newJar);
                    jarRenamed = true;
                }

                await Task.Run(() => Directory.Move(oldDir, newDir), cancellationToken);
            }
            catch
            {
                // 回滚已完成的文件重命名，避免目录名与 JSON 文件名不一致
                try { if (jsonRenamed && File.Exists(newJson) && !File.Exists(oldJson)) File.Move(newJson, oldJson); } catch { /* 忽略 */ }
                try { if (jarRenamed && File.Exists(newJar) && !File.Exists(oldJar)) File.Move(newJar, oldJar); } catch { /* 忽略 */ }
                throw;
            }
        }

        private static void RewriteVersionJsonId(string jsonPath, string newId)
        {
            try
            {
                string json = File.ReadAllText(jsonPath);
                if (JsonNode.Parse(json) is JsonObject root)
                {
                    root["id"] = newId;
                    File.WriteAllText(jsonPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InstanceNameService] 改写版本 JSON id 失败 {jsonPath}: {ex.Message}");
            }
        }

        private static bool CanOverwriteExistingInstance(
            string targetDir,
            string targetName,
            string baseGameVersion,
            InstanceLoaderKind loaderKind)
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

                if (loaderKind == InstanceLoaderKind.Fabric)
                    return hasFabricLoader;

                if (loaderKind == InstanceLoaderKind.Forge)
                    return hasForgeLoader;

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
