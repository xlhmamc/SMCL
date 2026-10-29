using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression; // 新增：用于安全解压 Native 动态库
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using SMCL.Models;
using SMCL.Services;
using System.Text.Json;

using LauncherConfig = SMCL.Services.LauncherConfig;

namespace SMCL.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private static readonly string LogFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smcl-latest.log");
        private static readonly object LogLock = new object();
        
        private string _currentAccountName = "未登录账号";
        public string CurrentAccountName
        {
            get => _currentAccountName;
            set => SetProperty(ref _currentAccountName, value);
        }
        private readonly GameProcessService _processService = new GameProcessService();
        private readonly FabricCompatibilityService _fabricCompatibilityService = new FabricCompatibilityService();
        private readonly Dictionary<string, string> _javaVersionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly LauncherConfig _config;

        private ViewModelBase? _currentPage;
        public ViewModelBase? CurrentPage
        {
            get => _currentPage;
            set
            {
                if (SetProperty(ref _currentPage, value))
                {
                    RefreshCurrentAccount();
                }
            }
        }

        public DownloadViewModel DownloadVM { get; } = new DownloadViewModel();
        public SettingsViewModel SettingsVM { get; } = new SettingsViewModel();
        public GameSettingsViewModel GameSettingsVM { get; } = new GameSettingsViewModel();
        public AccountViewModel AccountVM { get; } = new AccountViewModel();

        public ObservableCollection<LogModel> AllLogs { get; } = new ObservableCollection<LogModel>();
        public ObservableCollection<LogModel> DisplayLogs { get; } = new ObservableCollection<LogModel>();

        public ObservableCollection<string> GameVersions { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> JavaPaths { get; } = new ObservableCollection<string>();

        private string _minecraftRoot = Path.Combine(Environment.CurrentDirectory, ".minecraft");
        public string MinecraftRoot
        {
            get => _minecraftRoot;
            set
            {
                if (SetProperty(ref _minecraftRoot, value))
                {
                    Log($"Changed game root directory: {value}", "game.GameRepository", LogLevel.INFO);
                    _config.MinecraftRoot = value;
                    SaveConfiguration();
                    ScanGameVersionsAsync();
                }
            }
        }

        private string? _selectedGameVersion;
        public string? SelectedGameVersion
        {
            get => _selectedGameVersion;
            set
            {
                if (SetProperty(ref _selectedGameVersion, value))
                {
                    _config.SelectedGameVersion = value;
                    SaveConfiguration();
                    OnGameVersionSelected(value);
                }
            }
        }

        private string? _selectedJavaPath;
        public string? SelectedJavaPath
        {
            get => _selectedJavaPath;
            set
            {
                if (SetProperty(ref _selectedJavaPath, value))
                {
                    _config.SelectedJavaPath = value;
                    SaveConfiguration();
                    UpdateJavaVersionDisplay(value);
                }
            }
        }

        private string _selectedJavaVersion = "Not Selected";
        public string SelectedJavaVersion
        {
            get => _selectedJavaVersion;
            set => SetProperty(ref _selectedJavaVersion, value);
        }

        public ICommand NavigateHomeCommand { get; }
        public ICommand NavigateDownloadCommand { get; }
        public ICommand NavigateSettingsCommand { get; }
        public ICommand NavigateGameSettingsCommand { get; }
        public ICommand NavigateAccountCommand { get; }
        public ICommand LaunchGameCommand { get; }
        public ICommand RefreshGameVersionsCommand { get; }

        public MainViewModel()
        {
            try
            {
                File.WriteAllText(LogFilePath, $"=== SMCL Log Started at {DateTime.Now} ==={Environment.NewLine}");
                Console.WriteLine($"[SMCL] 日志绝对路径: {LogFilePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SMCL ERROR] 创建日志失败: {ex.Message}");
            }

            _config = ConfigService.Load();
            _minecraftRoot = _config.MinecraftRoot ?? Path.Combine(Environment.CurrentDirectory, ".minecraft"); 
            RefreshCurrentAccount();

            NavigateHomeCommand = new RelayCommand(() => CurrentPage = null);
            NavigateDownloadCommand = new RelayCommand(() => CurrentPage = DownloadVM);
            NavigateSettingsCommand = new RelayCommand(() => CurrentPage = SettingsVM);
            NavigateGameSettingsCommand = new RelayCommand(() => CurrentPage = GameSettingsVM);
            NavigateAccountCommand = new RelayCommand(() => CurrentPage = AccountVM);

            SettingsVM.MinecraftRoot = MinecraftRoot;
            SettingsVM.MaxMemory = _config.MaxMemory;
            SettingsVM.IsSimplifiedLogEnabled = _config.IsSimplifiedLogEnabled;

            SettingsVM.PropertyChanged += OnSettingsPropertyChanged;

            InitializeLauncherLogs();

            LaunchGameCommand = new AsyncRelayCommand(LaunchGameAsync);
            RefreshGameVersionsCommand = new AsyncRelayCommand(ScanGameVersionsAsync);

            DownloadVM.BackCommand = (IRelayCommand?)NavigateHomeCommand;
            DownloadVM.VersionInstalled += OnVersionInstalled;
            SettingsVM.BackCommand = NavigateHomeCommand;
            GameSettingsVM.BackCommand = NavigateHomeCommand;
            AccountVM.BackCommand = NavigateHomeCommand;
            AccountVM.LogAction = (msg, src, lvl) => Log(msg, src, lvl);
        }

        private void SaveConfiguration()
        {
            ConfigService.Save(_config);
        }

        private void InitializeLauncherLogs()
        {
            Log("Okaerinasai, Sensei!", "Launcher.main", LogLevel.INFO);
            Log("SMCL has launched!", "Launcher.main", LogLevel.INFO);
            Log($"Operating System: {SystemInfoService.GetOSVersion()}", "Launcher.main", LogLevel.INFO);
            Log($"System Architecture: {SystemInfoService.GetArchitecture()}", "Launcher.main", LogLevel.INFO);
            Log($"Current Directory: {Environment.CurrentDirectory}", "Launcher.main", LogLevel.INFO);

            Log("System Info:", "util.platform.SystemInfo.initialize", LogLevel.INFO);
            Log($" - CPU Cores: {Environment.ProcessorCount} Threads", "", LogLevel.INFO);
            Log($" - Memory: {SystemInfoService.GetMemoryInfo()}", "", LogLevel.INFO);

            Log("Start initializing application", "ui.Controllers.initialize", LogLevel.INFO);

            Task.Run(async () =>
            {
                var javas = ScanComprehensiveJavaExecutables();
                Log($"Finished Java lookup, found {javas.Count} Java environments", "java.JavaManager.searchPotentialJavaExecutables", LogLevel.TRACE);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    JavaPaths.Clear();
                    _javaVersionMap.Clear();
                    foreach (var java in javas)
                    {
                        Log($" - JDK ({java.Version}): {java.Path}", "", LogLevel.INFO);
                        JavaPaths.Add(java.Path);
                        _javaVersionMap[java.Path] = java.Version;
                    }

                    if (!string.IsNullOrEmpty(_config.SelectedJavaPath) && JavaPaths.Contains(_config.SelectedJavaPath))
                    {
                        SelectedJavaPath = _config.SelectedJavaPath;
                    }
                    else if (JavaPaths.Count > 0)
                    {
                        SelectedJavaPath = JavaPaths.FirstOrDefault();
                    }
                });

                ScanGameVersionsAsync();
            });
        }

        private List<(string Path, string Version)> ScanComprehensiveJavaExecutables()
        {
            var results = new List<(string Path, string Version)>();
            var foundPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void TryAddJavaPath(string? path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;

                try
                {
                    string fullPath = Path.GetFullPath(path);
                    if (File.Exists(fullPath) && foundPaths.Add(fullPath))
                    {
                        string ver = SystemInfoService.QueryJavaVersion(fullPath);
                        results.Add((fullPath, ver));
                    }
                }
                catch { }
            }

            try
            {
                var existing = SystemInfoService.SearchAllJavaExecutables();
                foreach (var j in existing)
                {
                    TryAddJavaPath(j.Path);
                }
            }
            catch { }

            string javaExeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "java.exe" : "java";

            foreach (var envVar in new[] { "JAVA_HOME", "JDK_HOME" })
            {
                string? envPath = Environment.GetEnvironmentVariable(envVar);
                if (!string.IsNullOrEmpty(envPath))
                {
                    TryAddJavaPath(Path.Combine(envPath, "bin", javaExeName));
                }
            }

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    TryAddJavaPath(Path.Combine(dir, javaExeName));
                }
            }

            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var launcherDirs = new List<string>
            {
                Path.Combine(MinecraftRoot, "runtime"),
                Path.Combine(userHome, ".local", "share", "hmcl", "java"),
                Path.Combine(userHome, ".local", "share", "multimc", "java"),
                Path.Combine(userHome, ".minecraft", "assets", "indexes")
            };

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                launcherDirs.Add(Path.Combine(appData, ".hmcl", "java"));
                launcherDirs.Add(Path.Combine(localAppData, "HMCL", "java"));
            }

            foreach (var lDir in launcherDirs)
            {
                if (Directory.Exists(lDir))
                {
                    try
                    {
                        var files = Directory.GetFiles(lDir, javaExeName, SearchOption.AllDirectories);
                        foreach (var f in files)
                        {
                            TryAddJavaPath(f);
                        }
                    }
                    catch { }
                }
            }

            var candidateDirs = new List<string>();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                candidateDirs.AddRange(new[]
                {
                    Path.Combine(pf, "Java"),
                    Path.Combine(pf86, "Java"),
                    Path.Combine(pf, "Eclipse Adoptium"),
                    Path.Combine(pf, "Amazon Corretto"),
                    Path.Combine(pf, "Zulu"),
                    Path.Combine(pf, "BellSoft"),
                    Path.Combine(pf, "Microsoft"),
                    Path.Combine(localAppData, "Programs", "AdoptOpenJDK")
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                candidateDirs.AddRange(new[] { "/usr/lib/jvm", "/usr/java", "/opt/java" });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                candidateDirs.AddRange(new[] { "/Library/Java/JavaVirtualMachines", "/System/Library/Java/JavaVirtualMachines" });
            }

            foreach (var baseDir in candidateDirs)
            {
                if (Directory.Exists(baseDir))
                {
                    try
                    {
                        var files = Directory.GetFiles(baseDir, javaExeName, SearchOption.AllDirectories);
                        foreach (var f in files)
                        {
                            TryAddJavaPath(f);
                        }
                    }
                    catch { }
                }
            }

            return results;
        }

        private async Task ScanGameVersionsAsync()
        {
            await Task.Run(async () =>
            {
                Log($"Refreshing Game Repository in {MinecraftRoot}...", "game.GameRepository", LogLevel.TRACE);
                
                var rawVersions = GameScannerService.ScanGameVersions(MinecraftRoot);
                var inheritedBases = new HashSet<string>();

                foreach (var v in rawVersions)
                {
                    string jsonPath = Path.Combine(MinecraftRoot, "versions", v, $"{v}.json");
                    if (File.Exists(jsonPath))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                            if (doc.RootElement.TryGetProperty("inheritsFrom", out var inheritElem))
                            {
                                string? baseVer = inheritElem.GetString();
                                if (!string.IsNullOrEmpty(baseVer)) inheritedBases.Add(baseVer);
                            }
                        }
                        catch { }
                    }
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    GameVersions.Clear();
                    int hiddenCount = 0;

                    // 【修改点】读取用户的设置
                    bool shouldHide = _config.HideInheritedVanilla;

                    foreach (var ver in rawVersions)
                    {
                        bool isModLoader = ver.Contains("fabric", StringComparison.OrdinalIgnoreCase) ||
                                           ver.Contains("forge", StringComparison.OrdinalIgnoreCase) ||
                                           ver.Contains("neoforge", StringComparison.OrdinalIgnoreCase) ||
                                           ver.Contains("quilt", StringComparison.OrdinalIgnoreCase);

                        // 【修改点】根据用户设置决定是否隐藏
                        if (shouldHide && inheritedBases.Contains(ver) && !isModLoader)
                        {
                            hiddenCount++;
                            continue; 
                        }

                        GameVersions.Add(ver);
                    }

                    if (!string.IsNullOrEmpty(_config.SelectedGameVersion) && GameVersions.Contains(_config.SelectedGameVersion))
                    {
                        SelectedGameVersion = _config.SelectedGameVersion;
                    }
                    else
                    {
                        SelectedGameVersion = GameVersions.FirstOrDefault();
                    }

                    if (hiddenCount > 0)
                    {
                        Log($"已智能隐藏 {hiddenCount} 个被加载器作为底层依赖的原版实例", "game.GameRepository", LogLevel.INFO);
                    }
                    Log($"Repository refresh finished, found {GameVersions.Count} valid instances", "game.GameRepository", LogLevel.INFO);
                });
            });
        }

        private (int MinJava, int MaxJava, int RecJava) GetJavaVersionRange(string mcVersion)
        {
            // 优先从版本 JSON 的 inheritsFrom 读取基础版本：
            // 自定义实例名（如 MyPack）可能不含版本号，或名称中混入其他版本数字。
            var match = TryGetBaseVersionFromInstanceJson(mcVersion);
            if (!match.Success)
            {
                match = Regex.Match(mcVersion, @"1\.(\d+)(?:\.(\d+))?");
            }

            if (match.Success)
            {
                int minor = int.Parse(match.Groups[1].Value);
                int patch = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;

                if (minor > 20 || (minor == 20 && patch >= 5))
                {
                    return (21, 25, 21);
                }
                if (minor >= 18)
                {
                    return (17, 21, 17);
                }
                if (minor == 17)
                {
                    return (16, 17, 16);
                }
            }

            return (8, 15, 8);
        }

        private Match TryGetBaseVersionFromInstanceJson(string versionName)
        {
            try
            {
                string jsonPath = Path.Combine(MinecraftRoot, "versions", versionName, $"{versionName}.json");
                if (!File.Exists(jsonPath)) return Match.Empty;

                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                string? baseVersion = doc.RootElement.TryGetProperty("inheritsFrom", out var inherited)
                    ? inherited.GetString()
                    : null;

                return string.IsNullOrWhiteSpace(baseVersion)
                    ? Match.Empty
                    : Regex.Match(baseVersion, @"1\.(\d+)(?:\.(\d+))?");
            }
            catch
            {
                return Match.Empty;
            }
        }

        private void OnGameVersionSelected(string? gameVersion)
        {
            if (string.IsNullOrWhiteSpace(gameVersion)) return;

            // 先同步全局的环境参数给实例
            GameSettingsVM.MinecraftRoot = MinecraftRoot;
            GameSettingsVM.VersionIsolationIndex = SettingsVM.VersionIsolationIndex;
            
            // 最后赋值 InstanceName，这会触发 GameSettingsViewModel 里的 LoadInstanceConfig
            GameSettingsVM.InstanceName = gameVersion;

            var (minJava, maxJava, recJava) = GetJavaVersionRange(gameVersion);
            Log($"Switched game version to [{gameVersion}]. Valid Java range: Java {minJava} ~ {maxJava} (Recommended: Java {recJava})", "java.JavaManager", LogLevel.INFO);

            if (!string.IsNullOrEmpty(SelectedJavaPath) && _javaVersionMap.TryGetValue(SelectedJavaPath, out var verStr))
            {
                int currentMajor = SystemInfoService.ExtractMajorVersion(verStr);

                if (currentMajor < minJava)
                {
                    Log($"[ERROR] Selected Java ({verStr}) is below minimum required Java {minJava}. Game will fail to launch!", "java.JavaManager", LogLevel.ERROR);
                }
                else if (currentMajor > maxJava)
                {
                    Log($"[WARNING] Selected Java ({verStr}) exceeds maximum supported Java {maxJava}. Game or mods may crash!", "java.JavaManager", LogLevel.WARN);
                }
                else if (currentMajor == recJava)
                {
                    Log($"[PERFECT MATCH] Selected Java ({verStr}) matches recommended Java {recJava}.", "java.JavaManager", LogLevel.INFO, true);
                }
                else
                {
                    Log($"[VALID] Selected Java ({verStr}) is within supported range (Java {minJava} ~ {maxJava}).", "java.JavaManager", LogLevel.INFO, true);
                }
            }
            else
            {
                Log("NOTICE: No Java environment selected", "java.JavaManager", LogLevel.WARN);
            }

            if (!string.IsNullOrEmpty(SelectedJavaPath))
            {
                ExecuteAndLogJavaVersion(SelectedJavaPath);
            }
        }

        private void ExecuteAndLogJavaVersion(string? javaPath)
        {
            if (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath))
            {
                Log($"Invalid Java path, cannot execute -version check: {javaPath ?? "Not Set"}", "java.JavaManager", LogLevel.WARN);
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = javaPath,
                        Arguments = "-version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(startInfo);
                    if (process != null)
                    {
                        string stderr = process.StandardError.ReadToEnd();
                        string stdout = process.StandardOutput.ReadToEnd();
                        process.WaitForExit(3000);

                        string output = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                        if (!string.IsNullOrWhiteSpace(output))
                        {
                            Log($"[Java Executable Check] {javaPath}:", "java.JavaManager", LogLevel.INFO);
                            var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var line in lines)
                            {
                                Log($"  > {line.Trim()}", "java.JavaManager", LogLevel.INFO);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Failed to execute java -version ({javaPath}): {ex.Message}", "java.JavaManager", LogLevel.ERROR);
                }
            });
        }

        private void UpdateJavaVersionDisplay(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                SelectedJavaVersion = "Not Selected";
                return;
            }

            if (_javaVersionMap.TryGetValue(path, out var cachedVersion))
            {
                SelectedJavaVersion = cachedVersion;
            }
            else
            {
                Task.Run(() =>
                {
                    string ver = SystemInfoService.QueryJavaVersion(path);
                    Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _javaVersionMap[path] = ver;
                        SelectedJavaVersion = ver;
                    });
                });
            }

            ExecuteAndLogJavaVersion(path);
        }

        #region 智能解析版本继承、启动主类与依赖库（已修复跨平台架构不匹配与动态库解压Bug）
        private (string mainClass, string assetIndex, string? parentJarPath, List<string> libraryPaths) ResolveVersionInfo(string versionDir, string gameVersion, string nativesDir)
        {
            string mainClass = "net.minecraft.client.main.Main";
            string assetIndex = gameVersion;
            string? parentJarPath = null;
            string? inheritsFrom = null;
            var libraryPaths = new List<string>();

            string jsonPath = Path.Combine(versionDir, $"{gameVersion}.json");

            string currentOsKey = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" :
                                RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "windows";

            void ParseJson(string path, bool isCurrentVersion)
            {
                if (!File.Exists(path)) return;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    var root = doc.RootElement;

                    if (isCurrentVersion && root.TryGetProperty("mainClass", out var mcElement))
                    {
                        string? mc = mcElement.GetString();
                        if (!string.IsNullOrEmpty(mc)) mainClass = mc;
                    }

                    if (root.TryGetProperty("assetIndex", out var aiElement) && aiElement.TryGetProperty("id", out var idElement))
                    {
                        assetIndex = idElement.GetString() ?? assetIndex;
                    }
                    else if (root.TryGetProperty("assets", out var assetsElem))
                    {
                        assetIndex = assetsElem.GetString() ?? assetIndex;
                    }

                    if (isCurrentVersion && root.TryGetProperty("inheritsFrom", out var inhElem))
                    {
                        inheritsFrom = inhElem.GetString();
                    }

                    if (root.TryGetProperty("libraries", out var libsElement) && libsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var lib in libsElement.EnumerateArray())
                        {
                            // 1. 严格校验 rules（过滤其他平台的依赖库）
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
                                        if (osElem.TryGetProperty("name", out var osnameElem))
                                        {
                                            string osName = osnameElem.GetString() ?? "";
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

                            string? name = lib.TryGetProperty("name", out var nameElem) ? nameElem.GetString() : null;
                            
                            // 2. 严密判定是否为 Native 动态库
                            bool hasNativesProp = lib.TryGetProperty("natives", out var nativesElem);
                            bool isNativeByName = name != null && name.Contains("natives-");

                            if (hasNativesProp || isNativeByName)
                            {
                                bool isForCurrentOs = false;
                                string? nativeClassifierKey = null;

                                if (hasNativesProp)
                                {
                                    if (nativesElem.TryGetProperty(currentOsKey, out var classifierElem))
                                    {
                                        nativeClassifierKey = classifierElem.GetString()?
                                            .Replace("${arch}", RuntimeInformation.OSArchitecture == Architecture.X64 ? "64" : "32");
                                        isForCurrentOs = true;
                                    }
                                }
                                else if (isNativeByName)
                                {
                                    if (name!.Contains($"natives-{currentOsKey}"))
                                    {
                                        isForCurrentOs = true;
                                        nativeClassifierKey = currentOsKey;
                                    }
                                }

                                // 如果是其他平台的 Native 库，直接丢弃
                                if (!isForCurrentOs) continue;

                                // 获取 Native 库的相对路径并解压
                                string? relNativePath = null;
                                if (hasNativesProp && nativeClassifierKey != null &&
                                    lib.TryGetProperty("downloads", out var dlElem) &&
                                    dlElem.TryGetProperty("classifiers", out var classifiersElem) &&
                                    classifiersElem.TryGetProperty(nativeClassifierKey, out var classElem) &&
                                    classElem.TryGetProperty("path", out var pathElem))
                                {
                                    relNativePath = pathElem.GetString();
                                }

                                if (string.IsNullOrEmpty(relNativePath) && lib.TryGetProperty("downloads", out var dlElem2) &&
                                    dlElem2.TryGetProperty("artifact", out var artElem) &&
                                    artElem.TryGetProperty("path", out var pathElem2))
                                {
                                    relNativePath = pathElem2.GetString();
                                }

                                if (string.IsNullOrEmpty(relNativePath) && !string.IsNullOrEmpty(name))
                                {
                                    var parts = name.Split(':');
                                    if (parts.Length >= 3)
                                    {
                                        string group = parts[0].Replace('.', '/');
                                        string artifact = parts[1];
                                        string version = parts[2];
                                        string classifier = parts.Length > 3 ? parts[3] : $"natives-{currentOsKey}";
                                        relNativePath = $"{group}/{artifact}/{version}/{artifact}-{version}-{classifier}.jar";
                                    }
                                }

                                if (!string.IsNullOrEmpty(relNativePath))
                                {
                                    string fullNativeJar = Path.Combine(MinecraftRoot, "libraries", relNativePath.Replace('/', Path.DirectorySeparatorChar));
                                    if (File.Exists(fullNativeJar))
                                    {
                                        ExtractNativeJar(fullNativeJar, nativesDir);
                                    }
                                }

                                // 【核心关键】Native 库必须在这里直接 continue 截断，绝对不能加入 Classpath！
                                continue;
                            }

                            // 3. 处理普通业务依赖库（Non-native libraries）
                            string? relPath = null;
                            if (lib.TryGetProperty("downloads", out var dlElemNormal) &&
                                dlElemNormal.TryGetProperty("artifact", out var artElemNormal) &&
                                artElemNormal.TryGetProperty("path", out var pathElemNormal))
                            {
                                relPath = pathElemNormal.GetString();
                            }

                            if (string.IsNullOrEmpty(relPath) && !string.IsNullOrEmpty(name))
                            {
                                if (name.Contains("natives-") && !name.Contains($"natives-{currentOsKey}")) continue;

                                var parts = name.Split(':');
                                if (parts.Length >= 3)
                                {
                                    string group = parts[0].Replace('.', '/');
                                    string artifact = parts[1];
                                    string version = parts[2];
                                    string classifier = parts.Length > 3 ? $"-{parts[3]}" : "";
                                    relPath = $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
                                }
                            }

                            if (!string.IsNullOrEmpty(relPath))
                            {
                                string fullLibPath = Path.Combine(MinecraftRoot, "libraries", relPath.Replace('/', Path.DirectorySeparatorChar));
                                if (!libraryPaths.Contains(fullLibPath))
                                {
                                    libraryPaths.Add(fullLibPath);
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            ParseJson(jsonPath, true);

            if (!string.IsNullOrEmpty(inheritsFrom))
            {
                string parentJsonPath = Path.Combine(MinecraftRoot, "versions", inheritsFrom, $"{inheritsFrom}.json");
                ParseJson(parentJsonPath, false);

                string pJar = Path.Combine(MinecraftRoot, "versions", inheritsFrom, $"{inheritsFrom}.jar");
                if (File.Exists(pJar))
                {
                    parentJarPath = pJar;
                }
            }
            else
            {
                var baseVerMatch = Regex.Match(gameVersion, @"\d+\.\d+(\.\d+)?");
                if (baseVerMatch.Success && baseVerMatch.Value != gameVersion)
                {
                    string baseVer = baseVerMatch.Value;
                    string pJar = Path.Combine(MinecraftRoot, "versions", baseVer, $"{baseVer}.jar");
                    if (File.Exists(pJar))
                    {
                        parentJarPath = pJar;
                    }
                }
            }

            return (mainClass, assetIndex, parentJarPath, libraryPaths);
        }

        /// <summary>
        /// 安全提取 Native 压缩包中的动态库（.so / .dll / .dylib）到 natives 目录
        /// </summary>
            private void ExtractNativeJar(string jarPath, string targetDir)
            {
                try
                {
                    Directory.CreateDirectory(targetDir);
                    using var archive = ZipFile.OpenRead(jarPath);
                    int count = 0;
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.FullName.StartsWith("META-INF", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(entry.Name))
                            continue;

                        string destPath = Path.Combine(targetDir, entry.Name);
                        entry.ExtractToFile(destPath, overwrite: true);
                        count++;
                    }
                    if (count > 0)
                    {
                        Log($"成功从 {Path.GetFileName(jarPath)} 提取了 {count} 个原生库文件", "launcher.Core", LogLevel.INFO);
                    }
                }
                catch (Exception ex)
                {
                    Log($"解压原生库失败 {Path.GetFileName(jarPath)}: {ex.Message}", "launcher.Core", LogLevel.ERROR);
                }
            }

        private void ForceExtractNatives(string nativesDir)
        {
            // 【关键】每次启动前，彻底清空旧的 natives 文件夹，防止残留坏文件
            if (Directory.Exists(nativesDir))
            {
                try { Directory.Delete(nativesDir, true); } catch { }
            }
            Directory.CreateDirectory(nativesDir);

            string currentOsKey = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" :
                                RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "windows";
            string libDir = Path.Combine(MinecraftRoot, "libraries");
            if (!Directory.Exists(libDir)) return;

            var nativesJars = Directory.GetFiles(libDir, $"*natives-{currentOsKey}*.jar", SearchOption.AllDirectories)
                           .Where(f => f.Contains("3.3.1"))
                           .ToArray();
            
            if (nativesJars.Length == 0)
            {
                Log($"[警告] 在 libraries 目录中未找到任何 natives-{currentOsKey} 的库包！", "launcher.Core", LogLevel.ERROR);
            }
            else
            {
                Log($"发现 {nativesJars.Length} 个原生库包，准备解压到 natives 目录...", "launcher.Core", LogLevel.INFO);
                foreach (var jar in nativesJars)
                {
                    ExtractNativeJar(jar, nativesDir);
                }
            }
        }

        private async Task EnsureLwjglNativesAsync()
        {
            string osName = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" :
                            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos" : "windows";
            
            string[] requiredNatives = {
                $"org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-{osName}.jar",
                $"org/lwjgl/lwjgl-glfw/3.3.1/lwjgl-glfw-3.3.1-natives-{osName}.jar",
                $"org/lwjgl/lwjgl-jemalloc/3.3.1/lwjgl-jemalloc-3.3.1-natives-{osName}.jar",
                $"org/lwjgl/lwjgl-openal/3.3.1/lwjgl-openal-3.3.1-natives-{osName}.jar",
                $"org/lwjgl/lwjgl-opengl/3.3.1/lwjgl-opengl-3.3.1-natives-{osName}.jar",
                $"org/lwjgl/lwjgl-stb/3.3.1/lwjgl-stb-3.3.1-natives-{osName}.jar",
                $"org/lwjgl/lwjgl-tinyfd/3.3.1/lwjgl-tinyfd-3.3.1-natives-{osName}.jar"
            };

            int sourceIndex = ConfigService.Load().DownloadSourceIndex;
            
            var sourcesList = new List<string>();
            
            if (sourceIndex == 0)
            {
                sourcesList.Add("https://bmclapi2.bangbang93.com/libraries/");
                sourcesList.Add("https://libraries.minecraft.net/"); // 备用
            }
            else if (sourceIndex == 1)
            {
                sourcesList.Add("https://download.mcbbs.net/maven/");
                sourcesList.Add("https://libraries.minecraft.net/");
            }
            else
            {
                sourcesList.Add("https://libraries.minecraft.net/"); // 官方首选
                sourcesList.Add("https://bmclapi2.bangbang93.com/libraries/");
            }

            string[] sources = sourcesList.ToArray();

            using var httpClient = new System.Net.Http.HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            foreach (var relPath in requiredNatives)
            {
                string localPath = Path.Combine(MinecraftRoot, "libraries", relPath.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(localPath))
                {
                    Log($"检测到缺失原生库: {Path.GetFileName(localPath)}，开始尝试下载...", "launcher.Core", LogLevel.WARN);
                    
                    bool downloadSuccess = false;
                    foreach (var baseUrl in sources)
                    {
                        try
                        {
                            string url = baseUrl + relPath;
                            Log($"  尝试源: {baseUrl} ...", "launcher.Core", LogLevel.INFO);
                            
                            byte[] data = await httpClient.GetByteArrayAsync(url);
                            
                            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                            await File.WriteAllBytesAsync(localPath, data);
                            
                            Log($"  [成功] 已从 {baseUrl} 下载并保存: {Path.GetFileName(localPath)}", "launcher.Core", LogLevel.INFO);
                            downloadSuccess = true;
                            break; // 下载成功，跳出源循环，继续下载下一个文件
                        }
                        catch (Exception ex)
                        {
                            // 注意：为了日志整洁，这里只记录警告，不中断流程
                            Log($"  [失败] 源 {baseUrl} 不可用: {ex.Message}", "launcher.Core", LogLevel.WARN);
                        }
                    }

                    if (!downloadSuccess)
                    {
                        Log($"  [严重] 所有源均下载失败，请检查网络或手动放置文件: {Path.GetFileName(localPath)}", "launcher.Core", LogLevel.ERROR);
                    }
                }
            }
        }

        #endregion

        private async Task LaunchGameAsync()
        {
            if (string.IsNullOrEmpty(SelectedGameVersion))
            {
                Log("未选择游戏版本，启动取消！", "launcher.Core", LogLevel.ERROR);
                return;
            }

            string javaExec = string.IsNullOrEmpty(SelectedJavaPath) ? "java" : SelectedJavaPath;
            if (!File.Exists(javaExec))
            {
                Log("Java 路径无效或未选择，请前往设置配置！", "launcher.Core", LogLevel.ERROR);
                return;
            }

            var account = AccountVM.GetSelectedAccount();
            if (account == null)
            {
                var config = ConfigService.Load();
                account = config.Accounts?.FirstOrDefault(a => 
                    !string.IsNullOrEmpty(config.SelectedAccountUuid) && a.Uuid == config.SelectedAccountUuid)
                    ?? config.Accounts?.FirstOrDefault();
            }

            if (account == null)
            {
                Log("未选择游戏账号，请先在【账户管理】中添加并选择一个账号！", "launcher.Core", LogLevel.ERROR);
                return;
            }

            if (SettingsVM.AutoOpenConsole)
            {
                await Dispatcher.UIThread.InvokeAsync(() => CurrentPage = null);
            }

            string userUuid = !string.IsNullOrEmpty(account.Uuid) ? account.Uuid : Guid.NewGuid().ToString("N");
            string accessToken = !string.IsNullOrEmpty(account.AccessToken) ? account.AccessToken : "0";
            string userType = account.AccountType == "离线账户" ? "Legacy" : "msa";

            string maxMemory = GameSettingsVM.UseCustomSettings 
                ? GameSettingsVM.CustomMemory.ToString() 
                : SettingsVM.MaxMemory;

            Log($"===== 开始构建启动参数 =====", "launcher.Core", LogLevel.INFO);
            Log($"> 玩家: {account.Username} ({account.AccountType})", "launcher.Core", LogLevel.INFO);
            Log($"> 内存: {maxMemory} MB", "launcher.Core", LogLevel.INFO);
            Log($"> Java: {javaExec}", "launcher.Core", LogLevel.INFO);

            // === Fabric 兼容性预检查 ===
            // 必须与下方的版本隔离逻辑保持一致，否则会扫描错误的 mods 目录
            string launchVersionDir = Path.Combine(MinecraftRoot, "versions", SelectedGameVersion);
            bool launchIsVersionIsolated = SettingsVM.VersionIsolationIndex == 0;
            string launchGameDir = launchIsVersionIsolated ? launchVersionDir : MinecraftRoot;
            string launchModsDir = Path.Combine(launchGameDir, "mods");

            try
            {
                var fabricReport = await _fabricCompatibilityService.EnsureCompatibleAsync(
                    launchVersionDir,
                    SelectedGameVersion,
                    launchModsDir,
                    progress: null,
                    log: message => Log(message, "fabric.Compatibility", LogLevel.WARN));

                if (fabricReport.IsFabric && !fabricReport.IsCompatible)
                {
                    foreach (var issue in fabricReport.Issues)
                    {
                        Log(
                            $"模组 '{issue.ModName}' ({issue.ModId}) {issue.ModVersion} 需要 Fabric Loader {issue.LoaderConstraint}，" +
                            $"当前实例为 {fabricReport.LoaderVersion}。",
                            "fabric.Compatibility",
                            LogLevel.ERROR);
                    }

                    Log(
                        "已阻止启动：请重新安装该 Fabric 实例（安装器会自动选择与所选 Loader 兼容的 Fabric API），" +
                        "或手动升级 Fabric Loader 后重试。",
                        "launcher.Core",
                        LogLevel.ERROR);

                    return;
                }
            }
            catch (Exception ex)
            {
                // 兼容性检查本身失败（如无网络）不应阻断启动，由 Fabric 自行报错
                Log($"Fabric 兼容性预检查失败（将继续启动）：{ex.Message}", "fabric.Compatibility", LogLevel.WARN);
            }

            try
            {
                await Task.Run(() =>
                {
                    string assetsDir = Path.Combine(MinecraftRoot, "assets");
                    string nativesDir = Path.Combine(launchVersionDir, "natives");
                    string clientJar = Path.Combine(launchVersionDir, $"{SelectedGameVersion}.jar");

                    Directory.CreateDirectory(nativesDir);

                    if (launchIsVersionIsolated && !Directory.Exists(launchGameDir))
                    {
                        Directory.CreateDirectory(launchGameDir);
                        Directory.CreateDirectory(Path.Combine(launchGameDir, "mods"));
                    }

                    Log($"> 版本隔离: {(launchIsVersionIsolated ? "已启用" : "已禁用")}", "launcher.Core", LogLevel.INFO);
                    Log($"> 游戏工作目录: {launchGameDir}", "launcher.Core", LogLevel.INFO);

                    // 核心修复：传入 nativesDir 并在解析时自动解压与跨平台过滤
                    // 必须传入 versionDir, SelectedGameVersion, nativesDir 三个参数
                    var (mainClass, resolvedAssetIndex, parentJarPath, libraryPaths) = ResolveVersionInfo(launchVersionDir, SelectedGameVersion, nativesDir);

                    EnsureLwjglNativesAsync().GetAwaiter().GetResult();
                    ForceExtractNatives(nativesDir);
                    var cpList = new List<string>();
                    if (!string.IsNullOrEmpty(parentJarPath) && File.Exists(parentJarPath)) cpList.Add(parentJarPath);
                    if (File.Exists(clientJar)) cpList.Add(clientJar);

                    // 严格使用过滤后的合法依赖库，彻底移除危险的全局无脑扫描降级逻辑
                    if (libraryPaths.Count > 0)
                    {
                        cpList.AddRange(libraryPaths);
                    }

                                        string separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ";" : ":";
                    
                    // === 修改点 1：不再给每个路径加引号，直接拼接 ===
                    string classPath = string.Join(separator, cpList);

                    string jvmArgsInput = GameSettingsVM.UseCustomSettings
                        ? GameSettingsVM.CustomJvmArgs
                        : SettingsVM.JvmArgs;

                    // === 修改点 2：创建参数列表 ===
                    var argList = new List<string>();

                    // 1. 基础 JVM 参数
                    argList.Add($"-Xmx{maxMemory}m");

                    // 2. 自定义 JVM 参数 (按空格拆分为独立参数)
                    if (!string.IsNullOrWhiteSpace(jvmArgsInput))
                    {
                        argList.AddRange(jvmArgsInput.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                    }

                    // 3. 库路径和 ClassPath（绝对不要带双引号，ArgumentList 会处理转义）
                    argList.Add($"-Djava.library.path={nativesDir}");
                    argList.Add("-cp");
                    argList.Add(classPath);

                    // 4. 主类
                    argList.Add(mainClass);

                    // 5. 游戏参数（将每一段独立添加）
                    argList.Add("--username"); argList.Add(account.Username);
                    argList.Add("--version"); argList.Add(SelectedGameVersion);
                    argList.Add("--gameDir"); argList.Add(launchGameDir);
                    argList.Add("--assetsDir"); argList.Add(assetsDir);
                    argList.Add("--assetIndex"); argList.Add(resolvedAssetIndex);
                    argList.Add("--uuid"); argList.Add(userUuid);
                    argList.Add("--accessToken"); argList.Add(accessToken);
                    argList.Add("--userType"); argList.Add(userType);
                    argList.Add("--versionType"); argList.Add("SMCL_Launcher");

                    // 6. 自定义游戏参数 (按空格拆分)
                    if (GameSettingsVM.UseCustomSettings && !string.IsNullOrWhiteSpace(GameSettingsVM.CustomMcArgs))
                    {
                        argList.AddRange(GameSettingsVM.CustomMcArgs.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                    }

                    // 生成日志用的字符串（仅用于在启动器界面显示）
                    string finalArgumentsForLog = string.Join(" ", argList);
                    Log($"[DEBUG] 启动参数: {finalArgumentsForLog}", "launcher.ProcessBuilder", LogLevel.TRACE);

                    // === 修改点 3：传递 argList 而不是字符串 ===
                    _processService.StartProcess(
                        javaExec,
                        argList, // 传入 List<string>
                        launchGameDir,
                        (msg, source, level, isMC) =>
                        {
                            Log(msg, source, level, isMC);

                            if (msg.Contains("游戏进程已退出"))
                            {
                                Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    if (SettingsVM.GameLaunchBehaviorIndex == 1)
                                    {
                                        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                                        {
                                            desktop.MainWindow?.Show();
                                        }
                                    }
                                });
                            }
                        }
                    );

                    Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        int behavior = SettingsVM.GameLaunchBehaviorIndex;
                        if (behavior == 1)
                        {
                            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                            {
                                desktop.MainWindow?.Hide();
                            }
                        }
                        else if (behavior == 2)
                        {
                            Environment.Exit(0);
                        }
                    });
                });
            }
            catch (Exception ex)
            {
                Log($"进程启动发生异常: {ex.Message}", "launcher.ProcessBuilder", LogLevel.ERROR);
            }
        }

        private void OnVersionInstalled(string versionName)
        {
            if (string.IsNullOrWhiteSpace(versionName)) return;

            // 安装完成后立即刷新版本列表并选中新实例，无需重启启动器
            _config.SelectedGameVersion = versionName;
            SaveConfiguration();
            ScanGameVersionsAsync();
        }

        private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SettingsViewModel.IsSimplifiedLogEnabled))
            {
                _config.IsSimplifiedLogEnabled = SettingsVM.IsSimplifiedLogEnabled;
                SaveConfiguration();
                RefreshDisplayLogs();
            }
            else if (e.PropertyName == nameof(SettingsViewModel.MaxMemory))
            {
                _config.MaxMemory = SettingsVM.MaxMemory;
                SaveConfiguration();
            }
            else if (e.PropertyName == nameof(SettingsViewModel.MinecraftRoot))
            {
                MinecraftRoot = SettingsVM.MinecraftRoot;
            }
            else if (e.PropertyName == nameof(SettingsViewModel.HideInheritedVanilla))
            {
                _config.HideInheritedVanilla = SettingsVM.HideInheritedVanilla;
                SaveConfiguration();
                ScanGameVersionsAsync();
            }
        }

        public void Log(string message, string source = "@.Launcher.main", LogLevel level = LogLevel.INFO, bool isMinecraftLog = false)
        {
            var logItem = new LogModel(message, source, level, isMinecraftLog);

            try
            {
                lock (LogLock)
                {
                    File.AppendAllText(LogFilePath, logItem.FormattedText + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Log Write Error] {ex.Message}");
            }

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                AllLogs.Add(logItem);
                if (!SettingsVM.IsSimplifiedLogEnabled || isMinecraftLog)
                {
                    DisplayLogs.Add(logItem);
                }
            });
        }

        private void RefreshDisplayLogs()
        {
            DisplayLogs.Clear();
            var filtered = SettingsVM.IsSimplifiedLogEnabled 
                ? AllLogs.Where(x => x.IsMinecraftLog) 
                : AllLogs;

            foreach (var item in filtered)
            {
                DisplayLogs.Add(item);
            }
        }

        public void RefreshCurrentAccount()
        {
            try
            {
                var config = ConfigService.Load();

                if (config?.Accounts == null || config.Accounts.Count == 0)
                {
                    CurrentAccountName = "未登录账号";
                    return;
                }

                var account = config.Accounts.FirstOrDefault(a => 
                    !string.IsNullOrEmpty(config.SelectedAccountUuid) && a.Uuid == config.SelectedAccountUuid);

                account ??= config.Accounts.FirstOrDefault();

                if (account != null && !string.IsNullOrWhiteSpace(account.Username))
                {
                    CurrentAccountName = account.Username;
                }
                else
                {
                    CurrentAccountName = "未登录账号";
                }
            }
            catch
            {
                CurrentAccountName = "未霓关 / 未登录账号";
            }
        }
    }
}