using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SMCL.Models;

namespace SMCL.Services
{
    /// <summary>
    /// Forge 安装服务：下载官方 Forge 安装器（可按镜像源回退），
    /// 静默运行 <c>--installClient</c>，检测生成版本，并按需重命名为自定义实例名。
    ///
    /// 与 Fabric 的纯 JSON 安装不同，Forge 依赖官方安装器做二进制补丁与依赖解析，
    /// 因此这里把“下载安装器、运行、检测/重命名”封装为独立服务。
    /// </summary>
    public class ForgeInstallerService
    {
        private static readonly HttpClient HttpClient = CreateHttpClient();

        /// <summary>
        /// Forge 安装结果，包含最终版本名与安装后校验信息。
        /// </summary>
        public sealed record ForgeInstallResult(
            string VersionName,
            bool Verified,
            string VerificationSummary);

        /// <summary>
        /// 安装 Forge 客户端实例。
        /// </summary>
        /// <returns>最终版本名与安装校验结果</returns>
        public async Task<ForgeInstallResult> InstallAsync(
            string mcVersion,
            string forgeVersion,
            long? build,
            int downloadSourceIndex,
            string gameDir,
            string javaPath,
            string? customName,
            IProgress<DownloadProgressReport>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(mcVersion))
                throw new ArgumentException("Minecraft 版本不能为空。", nameof(mcVersion));
            if (string.IsNullOrWhiteSpace(forgeVersion))
                throw new ArgumentException("Forge 版本不能为空。", nameof(forgeVersion));
            if (string.IsNullOrWhiteSpace(gameDir))
                throw new ArgumentException("游戏目录不能为空。", nameof(gameDir));

            Directory.CreateDirectory(gameDir);
            string versionsRoot = Path.Combine(gameDir, "versions");
            string installerPath = Path.Combine(
                Path.GetTempPath(),
                $"forge-installer-{mcVersion}-{forgeVersion}-{Guid.NewGuid():N}.jar");

            LogService.Info(
                "forge.install",
                $"开始安装: MC={mcVersion}, Forge={forgeVersion}, build={(build?.ToString() ?? "未知")}, " +
                $"下载源={downloadSourceIndex}, gameDir={gameDir}, java={javaPath}, customName='{customName}'");

            try
            {
                progress?.Report(new DownloadProgressReport
                {
                    TotalFiles = 1,
                    CompletedFiles = 0,
                    CurrentFileName = "正在下载 Forge 安装器..."
                });

                await DownloadInstallerAsync(
                    mcVersion, forgeVersion, build, downloadSourceIndex, installerPath, cancellationToken);

                long installerSize = new FileInfo(installerPath).Length;
                LogService.Info("forge.install", $"安装器已就绪: {installerPath} ({installerSize} 字节)");

                // 安装器内嵌 install_profile.json，其中 version 即安装器将要生成的版本目录名
                string? predictedId = TryReadInstallerVersion(installerPath);
                string defaultName = predictedId ?? $"{mcVersion}-forge-{forgeVersion}";
                bool hasCustomName = !string.IsNullOrWhiteSpace(customName);

                LogService.Info(
                    "forge.install",
                    $"安装器默认版本目录名: {defaultName}（预测值: {predictedId ?? "未读取到，使用回退值"}），自定义名称={hasCustomName}");

                string finalName = defaultName;
                if (hasCustomName)
                {
                    if (!InstanceNameService.TryResolve(
                            customName,
                            defaultName,
                            mcVersion,
                            versionsRoot,
                            InstanceLoaderKind.Forge,
                            out finalName,
                            out string? nameError))
                    {
                        throw new InvalidOperationException(nameError ?? "Forge 实例名称无效。");
                    }

                    // Forge 安装器总是先生成默认名目录，再执行重命名；
                    // 若自定义目标目录已存在，无法在不破坏旧实例的前提下安全替换，直接报错。
                    if (!string.Equals(finalName, defaultName, StringComparison.Ordinal) &&
                        Directory.Exists(Path.Combine(versionsRoot, finalName)))
                    {
                        throw new InvalidOperationException(
                            $"目标实例名已存在：versions/{finalName}。请更换名称，或先手动删除旧实例。");
                    }

                    LogService.Info("forge.install", $"自定义实例名解析结果: {finalName}");
                }

                // Forge 安装器会检查 launcher_profiles.json，没有则直接失败；
                // 本启动器不会生成该文件，因此在运行安装器前补齐一个最小可用版本。
                EnsureLauncherProfiles(gameDir);

                // 安装器自身依赖 vanilla client jar；确认基础版本已由下载阶段准备好
                string clientJar = Path.Combine(gameDir, "versions", mcVersion, $"{mcVersion}.jar");
                if (File.Exists(clientJar))
                {
                    LogService.Info("forge.install", $"原版客户端 Jar 已存在: {clientJar}");
                }
                else
                {
                    LogService.Warn("forge.install", $"原版客户端 Jar 不存在: {clientJar}，安装器将自行尝试从 Mojang 下载");
                }

                await LogJavaVersionAsync(javaPath, cancellationToken);

                // 快照现有版本目录，作为安装器生成名称检测失败时的回退手段
                HashSet<string> before = ListVersionDirectories(versionsRoot);
                LogService.Info("forge.install", $"安装前 versions 目录数量: {before.Count}");

                progress?.Report(new DownloadProgressReport
                {
                    TotalFiles = 1,
                    CompletedFiles = 0,
                    CurrentFileName = "正在静默安装 Forge（首次安装可能需要几分钟，请稍候）..."
                });

                await RunInstallerAsync(installerPath, gameDir, javaPath, cancellationToken);

                string? createdName = null;
                if (predictedId != null && Directory.Exists(Path.Combine(versionsRoot, predictedId)))
                {
                    createdName = predictedId;
                }
                else
                {
                    createdName = FindCreatedForgeVersion(versionsRoot, before, mcVersion);
                }

                if (string.IsNullOrWhiteSpace(createdName))
                {
                    throw new InvalidOperationException(
                        "Forge 安装完成，但未能在 versions 目录中找到新生成的版本。请重启启动器后手动刷新版本列表。");
                }

                LogService.Info("forge.install", $"安装器生成的版本目录: {createdName}");

                // 没有自定义名称时，以安装器实际生成的目录为准；
                // 只有用户明确输入了名称，才重命名到目标目录。
                string targetName = hasCustomName ? finalName : createdName;

                if (!string.Equals(createdName, targetName, StringComparison.Ordinal))
                {
                    progress?.Report(new DownloadProgressReport
                    {
                        TotalFiles = 1,
                        CompletedFiles = 0,
                        CurrentFileName = $"正在将实例 {createdName} 重命名为 {targetName}..."
                    });

                    LogService.Info("forge.install", $"重命名实例目录: {createdName} -> {targetName}");
                    await InstanceNameService.RenameVersionDirectoryAsync(versionsRoot, createdName, targetName, cancellationToken);
                }

                LogService.Info("forge.install", $"Forge 安装成功: versions/{targetName}");

                // 安装后校验：确认版本 JSON、Forge 关键参数和基础客户端 Jar 都存在
                var verification = VerifyInstallation(gameDir, targetName, mcVersion);
                LogService.Info(
                    "forge.verify",
                    $"安装校验{(verification.Verified ? "通过" : "警告")}: {verification.Summary}");

                progress?.Report(new DownloadProgressReport
                {
                    TotalFiles = 1,
                    CompletedFiles = 1,
                    CurrentFileName = $"Forge 安装完成: {targetName}"
                });

                return new ForgeInstallResult(targetName, verification.Verified, verification.Summary);
            }
            catch (Exception ex)
            {
                LogService.Error("forge.install", "安装失败: " + ex);
                throw;
            }
            finally
            {
                try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { /* 忽略清理失败 */ }
                try { if (File.Exists(installerPath + ".log")) File.Delete(installerPath + ".log"); } catch { /* 忽略 */ }
            }
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 8
            };

            return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        }

        /// <summary>
        /// 下载安装器：按下载源选择 BMCLAPI（build 号）或官方 Maven，失败时互相回退。
        /// 校验文件是合法 Jar（PK 魔数），避免写入 HTML 报错页。
        /// </summary>
        private static async Task DownloadInstallerAsync(
            string mcVersion,
            string forgeVersion,
            long? build,
            int downloadSourceIndex,
            string installerPath,
            CancellationToken cancellationToken)
        {
            string officialUrl =
                $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mcVersion}-{forgeVersion}/" +
                $"forge-{mcVersion}-{forgeVersion}-installer.jar";

            string? bmclApiUrl = build != null
                ? $"https://bmclapi2.bangbang93.com/forge/download/{build}"
                : null;

            var candidates = new List<(string Source, string Url)>();
            if (downloadSourceIndex == 0)
            {
                // 0 = BMCLAPI，优先镜像
                if (bmclApiUrl != null) candidates.Add(("bmclapi", bmclApiUrl));
                candidates.Add(("official", officialUrl));
            }
            else
            {
                candidates.Add(("official", officialUrl));
                if (bmclApiUrl != null) candidates.Add(("bmclapi", bmclApiUrl));
            }

            var failures = new List<Exception>();

            foreach (var (source, url) in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stopwatch = Stopwatch.StartNew();

                // 单个源最多 2 分钟，避免某个镜像挂起时阻塞后续官方源回退
                using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptTimeout.CancelAfter(TimeSpan.FromSeconds(120));

                try
                {
                    LogService.Info("forge.installer", $"尝试下载源 {source}: {url}");

                    using var response = await HttpClient.GetAsync(
                        url, HttpCompletionOption.ResponseHeadersRead, attemptTimeout.Token);

                    LogService.Info(
                        "forge.installer",
                        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}, " +
                        $"Content-Type={response.Content.Headers.ContentType?.MediaType ?? "?"}, " +
                        $"Content-Length={response.Content.Headers.ContentLength?.ToString() ?? "?"}");

                    response.EnsureSuccessStatusCode();
                    byte[] data = await response.Content.ReadAsByteArrayAsync(attemptTimeout.Token);

                    LogService.Info(
                        "forge.installer",
                        $"收到 {data.Length} 字节，耗时 {stopwatch.ElapsedMilliseconds}ms");

                    if (data.Length < 1024)
                        throw new InvalidDataException("下载内容过小，可能不是安装器。");

                    // Zip/Jar 魔数：0x50 0x4B
                    if (data.Length < 4 || data[0] != 0x50 || data[1] != 0x4B)
                        throw new InvalidDataException("下载内容不是有效的 Jar 文件。");

                    await File.WriteAllBytesAsync(installerPath, data, attemptTimeout.Token);
                    LogService.Info("forge.installer", $"安装器保存成功（来源 {source}）: {installerPath}");
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    string reason = ex is OperationCanceledException
                        ? "请求超时（120 秒）"
                        : $"{ex.GetType().Name}: {ex.Message}";

                    LogService.Warn(
                        "forge.installer",
                        $"源 {source} 失败（耗时 {stopwatch.ElapsedMilliseconds}ms）: {reason}");
                }
            }

            LogService.Error(
                "forge.installer",
                $"所有安装器下载源均失败: {string.Join(" | ", failures.Select(f => f.Message))}");

            throw new IOException(
                $"Forge 安装器下载失败（已尝试 {candidates.Count} 个源）: {failures[^1].Message}",
                failures[^1]);
        }

        /// <summary>
        /// Forge 安装器要求目标目录存在 launcher_profiles.json（或 Microsoft Store 版本）。
        /// 本启动器不会生成该文件，因此这里创建一个最小可用的 profiles 文件；
        /// 若已有文件但 JSON 损坏，则备份后重建，避免安装器直接拒绝运行。
        /// </summary>
        private static void EnsureLauncherProfiles(string gameDir)
        {
            string standardPath = Path.Combine(gameDir, "launcher_profiles.json");
            string msStorePath = Path.Combine(gameDir, "launcher_profiles_microsoft_store.json");

            if (File.Exists(msStorePath))
            {
                LogService.Info("forge.profile", $"已存在 Microsoft Store 版 launcher profiles: {msStorePath}");
                return;
            }

            if (File.Exists(standardPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(standardPath));
                    LogService.Info("forge.profile", $"已存在有效 launcher_profiles.json: {standardPath}");
                    return;
                }
                catch (Exception ex)
                {
                    string backupPath = standardPath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    try
                    {
                        File.Move(standardPath, backupPath, overwrite: true);
                        LogService.Warn("forge.profile", $"launcher_profiles.json 解析失败，已备份到 {backupPath}: {ex.Message}");
                    }
                    catch (Exception moveEx)
                    {
                        LogService.Warn("forge.profile", $"launcher_profiles.json 解析失败且无法备份: {moveEx.Message}");
                    }
                }
            }

            string json = JsonSerializer.Serialize(
                new
                {
                    profiles = new Dictionary<string, object>(),
                    settings = new Dictionary<string, object>(),
                    version = 3
                },
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(standardPath, json);
            LogService.Info("forge.profile", $"已创建最小 launcher_profiles.json: {standardPath}");
        }

        /// <summary>
        /// 记录 java -version 输出，用于判断安装器失败是否由 Java 版本不兼容引起。
        /// 该步骤失败不会中断安装。
        /// </summary>
        private static async Task LogJavaVersionAsync(string javaPath, CancellationToken cancellationToken)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = javaPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-version");

                using var process = new Process { StartInfo = psi };
                if (!process.Start())
                {
                    LogService.Warn("forge.java", $"无法启动 Java 检查进程: {javaPath}");
                    return;
                }

                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));

                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                    LogService.Warn("forge.java", $"java -version 超时: {javaPath}");
                    return;
                }

                string output = (await stderrTask) + (await stdoutTask);
                output = output.Replace("\r", string.Empty).Replace("\n", " | ").Trim();
                LogService.Info("forge.java", $"{javaPath} -version => {output}");
            }
            catch (Exception ex)
            {
                LogService.Warn("forge.java", $"检测 Java 版本失败（{javaPath}）: {ex.Message}");
            }
        }

        /// <summary>
        /// 从安装器 jar 的 install_profile.json 读取将生成的版本目录名。
        /// </summary>
        private static string? TryReadInstallerVersion(string installerPath)
        {
            try
            {
                using var archive = ZipFile.OpenRead(installerPath);
                ZipArchiveEntry? entry = archive.GetEntry("install_profile.json");
                if (entry == null)
                {
                    LogService.Warn("forge.installer", "安装器中不存在 install_profile.json，将使用回退版本名");
                    return null;
                }

                using Stream stream = entry.Open();
                using var doc = JsonDocument.Parse(stream);

                string? version = doc.RootElement.TryGetProperty("version", out var versionProp)
                    ? versionProp.GetString()
                    : null;

                LogService.Info("forge.installer", $"install_profile.json version = {version ?? "<空>"}");
                return version;
            }
            catch (Exception ex)
            {
                LogService.Warn("forge.installer", $"读取 install_profile.json 失败: {ex.Message}");
                return null;
            }
        }

        private static async Task RunInstallerAsync(
            string installerPath,
            string gameDir,
            string javaPath,
            CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-jar");
            psi.ArgumentList.Add(installerPath);
            psi.ArgumentList.Add("--installClient");
            psi.ArgumentList.Add(gameDir);

            string commandLine = $"\"{javaPath}\" -jar \"{installerPath}\" --installClient \"{gameDir}\"";
            LogService.Info("forge.installer", $"启动安装器进程: {commandLine}");
            LogService.Info("forge.installer", $"工作目录将从安装器自身目录继承；JAVA_HOME={Environment.GetEnvironmentVariable("JAVA_HOME") ?? "<未设置>"}");

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var stopwatch = Stopwatch.StartNew();

            // 关键：必须异步读取 stdout/stderr，否则安装器输出缓冲写满会阻塞进程
            process.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                stdout.AppendLine(e.Data);
                LogService.Debug("forge.stdout", e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                stderr.AppendLine(e.Data);
                LogService.Warn("forge.stderr", e.Data);
            };

            if (!process.Start())
                throw new InvalidOperationException($"无法启动 Forge 安装器进程：{javaPath}");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                LogService.Error("forge.installer", "安装器执行超时（15 分钟），已终止进程");
                throw new TimeoutException("Forge 安装超时（超过 15 分钟），已终止安装进程。");
            }

            // 确保异步 stdout/stderr 事件已全部处理完，错误尾部才完整
            process.WaitForExit();

            LogService.Info(
                "forge.installer",
                $"安装器退出: ExitCode={process.ExitCode}, 耗时={stopwatch.Elapsed.TotalSeconds:F1}s");

            if (process.ExitCode != 0)
            {
                string combined = (stdout.ToString() + Environment.NewLine + stderr.ToString()).Trim();
                string detail = combined.Length > 2000 ? combined[^2000..] : combined;

                LogService.Error("forge.installer", $"安装器失败输出尾部:\n{detail}");

                throw new InvalidOperationException(
                    $"Forge 安装器执行失败（退出码 {process.ExitCode}，Java: {javaPath}）。" +
                    $"\n{detail}");
            }
        }

        /// <summary>
        /// 安装后校验：检查版本 JSON、Forge 必需的 JVM/游戏参数以及基础客户端 Jar。
        /// 仅用于日志与界面提示，不阻止已经成功退出的安装器结果。
        /// </summary>
        private static (bool Verified, string Summary) VerifyInstallation(
            string gameDir,
            string versionName,
            string mcVersion)
        {
            string jsonPath = Path.Combine(gameDir, "versions", versionName, $"{versionName}.json");
            if (!File.Exists(jsonPath))
                return (false, $"版本 JSON 不存在: {jsonPath}");

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                JsonElement root = doc.RootElement;

                string mainClass = root.TryGetProperty("mainClass", out var mcElem)
                    ? mcElem.GetString() ?? string.Empty
                    : string.Empty;

                string id = root.TryGetProperty("id", out var idElem)
                    ? idElem.GetString() ?? string.Empty
                    : string.Empty;

                string forgeVersion = FindForgeVersion(root);

                string inheritsFrom = root.TryGetProperty("inheritsFrom", out var inhElem)
                    ? inhElem.GetString() ?? string.Empty
                    : string.Empty;

                int libraryCount = root.TryGetProperty("libraries", out var libsElem) &&
                                   libsElem.ValueKind == JsonValueKind.Array
                    ? libsElem.GetArrayLength()
                    : 0;

                bool hasArguments = root.TryGetProperty("arguments", out _);
                bool legacy = !hasArguments && root.TryGetProperty("minecraftArguments", out _);

                bool hasAddOpens = JsonArgsContain(root, "jvm", "--add-opens");
                bool hasAddModules = JsonArgsContain(root, "jvm", "--add-modules");
                bool hasLaunchTarget = JsonArgsContain(root, "game", "--launchTarget");

                string baseJar = Path.Combine(gameDir, "versions", mcVersion, $"{mcVersion}.jar");
                bool hasBaseJar = File.Exists(baseJar);

                bool verified = !string.IsNullOrWhiteSpace(mainClass) &&
                                libraryCount > 0 &&
                                hasBaseJar &&
                                (legacy ||
                                 (string.Equals(inheritsFrom, mcVersion, StringComparison.OrdinalIgnoreCase) &&
                                  hasAddOpens && hasAddModules && hasLaunchTarget));

                string summary =
                    $"id={id}, forgeVersion={forgeVersion}, mainClass={mainClass}, inheritsFrom={inheritsFrom}, libraries={libraryCount}, " +
                    $"arguments={hasArguments}, legacy={legacy}, addOpens={hasAddOpens}, " +
                    $"addModules={hasAddModules}, launchTarget={hasLaunchTarget}, baseJar={hasBaseJar}";

                return (verified, summary);
            }
            catch (Exception ex)
            {
                return (false, $"校验失败: {ex.Message}");
            }
        }

        private static bool JsonArgsContain(JsonElement root, string section, string value)
        {
            if (!root.TryGetProperty("arguments", out var argumentsElem) ||
                argumentsElem.ValueKind != JsonValueKind.Object ||
                !argumentsElem.TryGetProperty(section, out var argsElem) ||
                argsElem.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in argsElem.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String &&
                    string.Equals(item.GetString(), value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string FindForgeVersion(JsonElement root)
        {
            if (!root.TryGetProperty("libraries", out var libraries) ||
                libraries.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            foreach (var library in libraries.EnumerateArray())
            {
                string name = library.TryGetProperty("name", out var nameElem)
                    ? nameElem.GetString() ?? string.Empty
                    : string.Empty;

                if (!name.StartsWith("net.minecraftforge:fmlloader:", StringComparison.Ordinal) &&
                    !name.StartsWith("net.minecraftforge:forge:", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] parts = name.Split(':');
                if (parts.Length >= 3) return parts[2];
            }

            return string.Empty;
        }

        private static HashSet<string> ListVersionDirectories(string versionsRoot)
        {
            if (!Directory.Exists(versionsRoot))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return Directory.GetDirectories(versionsRoot)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static string? FindCreatedForgeVersion(
            string versionsRoot,
            HashSet<string> before,
            string mcVersion)
        {
            if (!Directory.Exists(versionsRoot)) return null;

            var created = Directory.GetDirectories(versionsRoot)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Where(name => !before.Contains(name))
                .Where(name => IsForgeVersion(versionsRoot, name, mcVersion))
                .ToList();

            return created.Count == 1 ? created[0] : created.FirstOrDefault();
        }

        private static bool IsForgeVersion(string versionsRoot, string name, string mcVersion)
        {
            string jsonPath = Path.Combine(versionsRoot, name, $"{name}.json");
            if (!File.Exists(jsonPath)) return false;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("inheritsFrom", out var inherited) &&
                    !string.Equals(inherited.GetString(), mcVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // Forge 1.20+ 主类为 BootstrapLauncher；旧版主类为 Launch。名称包含 forge 即可判为 Forge 实例
                return name.Contains("forge", StringComparison.OrdinalIgnoreCase) ||
                       (root.TryGetProperty("mainClass", out var mainClass) &&
                        (mainClass.GetString() ?? string.Empty).Contains("cpw.mods", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }
    }
}
