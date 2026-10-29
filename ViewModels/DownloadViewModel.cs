using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SMCL.Models;
using SMCL.Services;

namespace SMCL.ViewModels
{
    public partial class DownloadViewModel : ViewModelBase
    {
        private readonly DownloadService _downloadService = new DownloadService();
        private readonly ModService _modService = new ModService();
        private readonly ModrinthService _modrinthService = new ModrinthService();

        public event Action<string>? VersionInstalled;
        
        private static readonly HttpClient HttpClient = new HttpClient();

        private Bitmap? _btnBgNormal;
        private Bitmap? _btnBgActive;
        
        [ObservableProperty]
        private bool _isModDetailVisible = false;

        [ObservableProperty]
        private ModrinthSearchResult? _currentModDetail;

        [ObservableProperty]
        private ObservableCollection<ModVersionGroup> _modVersionGroups = new();

        [ObservableProperty]
        private ModrinthVersion? _selectedModVersion;

        [ObservableProperty]
        private bool _isLoadingVersions;

        [ObservableProperty]
        private ObservableCollection<ModrinthSearchResult> _searchResults = new();

        [ObservableProperty]
        private string _searchQuery = "";

        [ObservableProperty]
        private bool _isSearchingMods;

        [ObservableProperty]
        private IRelayCommand? _backCommand;

        [ObservableProperty]
        private string _statusText = "准备就绪";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartDownloadCommand))]
        private bool _isDownloading;

        [ObservableProperty]
        private double _downloadProgress;

        [ObservableProperty] private Bitmap? _minecraftBtnBg;
        [ObservableProperty] private Bitmap? _javaBtnBg;
        [ObservableProperty] private Bitmap? _modsBtnBg;
        [ObservableProperty] private Bitmap? _texturePacksBtnBg;

        [ObservableProperty]
        private ObservableCollection<VersionManifestItem> _availableVersions = new();

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GoToConfigCommand))]
        [NotifyCanExecuteChangedFor(nameof(StartDownloadCommand))]
        private VersionManifestItem? _selectedVersion;

        [ObservableProperty]
        private bool _isConfiguringVersion;

        [ObservableProperty]
        private string _customInstanceName = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasInstanceNameError))]
        private string? _instanceNameError;

        [ObservableProperty]
        private ObservableCollection<string> _availableInstances = new();

        [ObservableProperty]
        private string? _selectedDownloadInstance;

        // ================== 翻页功能属性 ==================
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(PrevPageCommand))]
        [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
        private int _currentPage = 1;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(PrevPageCommand))]
        [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
        private int _totalPages = 1;

        // 用于记录当前状态，翻页时要用到
        private string _lastQuery = "";
        private bool _isSearchMode = false;
        // =================================================

        public bool HasInstanceNameError => !string.IsNullOrWhiteSpace(InstanceNameError);

        partial void OnCustomInstanceNameChanged(string value)
        {
            if (HasInstanceNameError) InstanceNameError = null;
        }

        partial void OnSelectedDownloadInstanceChanged(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            
            if (_isSearchMode)
                _ = SearchModsAsync();
            else
                _ = LoadRecommendedModsAsync();
        }

        private string _selectedLoader = "Vanilla";
        public string SelectedLoader
        {
            get => _selectedLoader;
            set
            {
                if (SetProperty(ref _selectedLoader, value))
                {
                    OnPropertyChanged(nameof(IsVanillaSelected));
                    OnPropertyChanged(nameof(IsFabricSelected));
                    OnPropertyChanged(nameof(IsForgeSelected));
                    
                    if (value != "Vanilla" && SelectedVersion != null)
                    {
                        _ = LoadLoaderVersionsAsync(value, SelectedVersion.Id);
                    }
                }
            }
        }

        public bool IsVanillaSelected => SelectedLoader == "Vanilla";
        public bool IsFabricSelected => SelectedLoader == "Fabric";
        public bool IsForgeSelected => SelectedLoader == "Forge";

        [ObservableProperty]
        private ObservableCollection<string> _loaderVersions = new();

        [ObservableProperty]
        private string? _selectedLoaderVersion;

        [ObservableProperty]
        private bool _autoInstallFabricApi = true;

        [ObservableProperty] private bool _isMinecraftSelected = true;
        [ObservableProperty] private bool _isJavaSelected;
        [ObservableProperty] private bool _isModsSelected;
        [ObservableProperty] private bool _isTexturePacksSelected;

        public DownloadViewModel()
        {
            InitBitmaps();
            UpdateTabStyles();
            _ = LoadVersionsAsync();
            _ = LoadRecommendedModsAsync();
            LoadAvailableInstances();
        }

        private void InitBitmaps()
        {
            _btnBgNormal = LoadAssetBitmap("avares://SMCL/Assets/Button.png", "avares://SMCL/Assets/button.png");
            _btnBgActive = LoadAssetBitmap("avares://SMCL/Assets/Button2.png", "avares://SMCL/Assets/button2.png");
        }

        private static Bitmap? LoadAssetBitmap(params string[] uris)
        {
            foreach (var uri in uris)
            {
                try
                {
                    var stream = AssetLoader.Open(new Uri(uri));
                    return new Bitmap(stream);
                }
                catch { }
            }
            return null;
        }

        private void UpdateTabStyles()
        {
            MinecraftBtnBg = IsMinecraftSelected ? _btnBgActive : _btnBgNormal;
            JavaBtnBg = IsJavaSelected ? _btnBgActive : _btnBgNormal;
            ModsBtnBg = IsModsSelected ? _btnBgActive : _btnBgNormal;
            TexturePacksBtnBg = IsTexturePacksSelected ? _btnBgActive : _btnBgNormal;
        }

        private void LoadAvailableInstances()
        {
            try
            {
                var config = ConfigService.Load();
                string gameDir = string.IsNullOrWhiteSpace(config.MinecraftRoot)
                    ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".minecraft")
                    : config.MinecraftRoot;
                
                var versions = GameScannerService.ScanGameVersions(gameDir);
                AvailableInstances.Clear();
                foreach(var v in versions) AvailableInstances.Add(v);
                
                // 默认选中主界面正在使用的版本
                if (!string.IsNullOrEmpty(config.SelectedGameVersion) && versions.Contains(config.SelectedGameVersion))
                    SelectedDownloadInstance = config.SelectedGameVersion;
                else if (versions.Count > 0)
                    SelectedDownloadInstance = versions[0];
            }
            catch { }
        }

        [RelayCommand]
        private void SwitchTab(string tabName)
        {
            IsMinecraftSelected = tabName == "Minecraft";
            IsJavaSelected = tabName == "Java";
            IsModsSelected = tabName == "Mods";
            IsTexturePacksSelected = tabName == "TexturePacks";

            IsConfiguringVersion = false;
            UpdateTabStyles();

            if (IsMinecraftSelected && AvailableVersions.Count == 0)
            {
                _ = LoadVersionsAsync();
            }
            else if (IsModsSelected && SearchResults.Count == 0)
            {
                _ = LoadRecommendedModsAsync();
            }
        }

        [RelayCommand]
        public async Task LoadVersionsAsync()
        {
            StatusText = "正在获取游戏版本列表...";
            try
            {
                var manifest = await _downloadService.GetVersionManifestAsync();
                if (manifest != null)
                {
                    AvailableVersions.Clear();
                    foreach (var ver in manifest.Versions)
                    {
                        if (ver.Type == "release")
                        {
                            AvailableVersions.Add(ver);
                        }
                    }
                    StatusText = $"加载完成，共 {AvailableVersions.Count} 个官方正式版";
                }
                else
                {
                    StatusText = "获取版本列表失败，请检查网络连接";
                }
            }
            catch (Exception ex)
            {
                StatusText = $"连接失败: {ex.Message}";
            }
        }

        private bool CanGoToConfig() => SelectedVersion != null;

        [RelayCommand(CanExecute = nameof(CanGoToConfig))]
        private void GoToConfig()
        {
            if (SelectedVersion == null) return;
            CustomInstanceName = string.Empty;
            InstanceNameError = null;
            SelectedLoader = "Vanilla";
            LoaderVersions.Clear();
            IsConfiguringVersion = true;
        }

        [RelayCommand]
        private void BackToVersionList()
        {
            IsConfiguringVersion = false;
        }

        [RelayCommand]
        private void SelectLoader(string loader)
        {
            SelectedLoader = loader;
        }

        private async Task LoadLoaderVersionsAsync(string loader, string mcVersion)
        {
            LoaderVersions.Clear();
            SelectedLoaderVersion = null;
            StatusText = $"正在获取 {loader} 版本列表...";

            try
            {
                if (loader == "Fabric")
                {
                    string url = $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}";
                    string json = await HttpClient.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(json);
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("loader", out var l) && l.TryGetProperty("version", out var v))
                            LoaderVersions.Add(v.GetString()!);
                    }
                }
                else if (loader == "Forge")
                {
                    string url = $"https://bmclapi2.bangbang93.com/forge/minecraft/{mcVersion}";
                    string json = await HttpClient.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(json);
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("version", out var v))
                            LoaderVersions.Add(v.GetString()!);
                    }
                }

                if (LoaderVersions.Count > 0)
                {
                    SelectedLoaderVersion = LoaderVersions[0];
                    StatusText = $"获取到 {LoaderVersions.Count} 个可用版本";
                }
                else
                {
                    StatusText = "未找到适合该游戏版本的加载器版本！";
                }
            }
            catch (Exception ex)
            {
                StatusText = $"获取加载器版本失败: {ex.Message}";
            }
        }

        private bool CanStartDownload() => SelectedVersion != null && !IsDownloading && (IsVanillaSelected || SelectedLoaderVersion != null);

        [RelayCommand(CanExecute = nameof(CanStartDownload))]
        public async Task StartDownloadAsync()
        {
            if (SelectedVersion == null) return;

            var config = ConfigService.Load();
            string gameDir = string.IsNullOrWhiteSpace(config.MinecraftRoot)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".minecraft")
                : config.MinecraftRoot;

            string versionsRoot = Path.Combine(gameDir, "versions");
            string baseVersion = SelectedVersion.Id;
            string? loaderVersion = SelectedLoaderVersion;

            bool isFabric = SelectedLoader == "Fabric" && !string.IsNullOrEmpty(loaderVersion);
            bool isForge = SelectedLoader == "Forge" && !string.IsNullOrEmpty(loaderVersion);

            string defaultVersionName = isFabric
                ? $"fabric-loader-{loaderVersion}-{baseVersion}"
                : baseVersion;

            string finalVersionName = defaultVersionName;
            bool createVanillaAlias = false;

            if (isFabric || IsVanillaSelected)
            {
                if (!InstanceNameService.TryResolve(
                        CustomInstanceName,
                        defaultVersionName,
                        baseVersion,
                        versionsRoot,
                        isFabricInstance: isFabric,
                        out finalVersionName,
                        out string? nameError))
                {
                    InstanceNameError = nameError ?? "实例名称无效，请修改后重试。";
                    StatusText = InstanceNameError;
                    return;
                }

                InstanceNameError = null;

                createVanillaAlias = IsVanillaSelected &&
                                     !string.Equals(finalVersionName, baseVersion, StringComparison.Ordinal);
            }

            IsDownloading = true;

            IProgress<DownloadProgressReport> progressHandler = new Progress<DownloadProgressReport>(report =>
            {
                DownloadProgress = report.Percentage;
                StatusText = $"[{report.CompletedFiles}/{report.TotalFiles}] {report.CurrentFileName}";
            });

            try
            {
                _downloadService.DownloadSourceIndex = config.DownloadSourceIndex;

                StatusText = $"正在下载原版 {baseVersion} 核心...";
                await _downloadService.DownloadGameVersionAsync(baseVersion, SelectedVersion.Url, gameDir, progressHandler);

                string? installedVersionName = null;
                string? extraMessage = null;

                if (isFabric)
                {
                    await InstallFabricAsync(baseVersion, loaderVersion!, gameDir, finalVersionName, progressHandler);

                    if (AutoInstallFabricApi)
                    {
                        extraMessage = await InstallFabricApiModAsync(
                            baseVersion, loaderVersion!, finalVersionName, gameDir, config, progressHandler);
                    }

                    installedVersionName = finalVersionName;
                }
                else if (isForge)
                {
                    await InstallForgeAsync(baseVersion, loaderVersion!, gameDir, progressHandler);

                    if (!string.IsNullOrWhiteSpace(CustomInstanceName))
                    {
                        extraMessage = "（Forge 的版本目录名由 Forge 安装器决定，自定义名称未应用）";
                    }
                }
                else
                {
                    if (createVanillaAlias)
                    {
                        progressHandler.Report(new DownloadProgressReport
                        {
                            TotalFiles = 1,
                            CompletedFiles = 0,
                            CurrentFileName = $"正在创建自定义实例 {finalVersionName}..."
                        });

                        await InstanceNameService.CreateVanillaAliasAsync(versionsRoot, baseVersion, finalVersionName);

                        progressHandler.Report(new DownloadProgressReport
                        {
                            TotalFiles = 1,
                            CompletedFiles = 1,
                            CurrentFileName = $"自定义实例 {finalVersionName} 创建完成"
                        });
                    }

                    installedVersionName = finalVersionName;
                }

                if (installedVersionName != null)
                {
                    StatusText = string.IsNullOrEmpty(extraMessage)
                        ? $"实例 {installedVersionName} 安装完成！"
                        : $"实例 {installedVersionName} 安装完成！{extraMessage}";

                    VersionInstalled?.Invoke(installedVersionName);
                }
                else
                {
                    StatusText = string.IsNullOrEmpty(extraMessage)
                        ? "安装完成！"
                        : $"安装完成！{extraMessage}";
                }
            }
            catch (Exception ex)
            {
                StatusText = $"安装中断: {ex.Message}";
            }
            finally
            {
                IsDownloading = false;
            }
        }

        private static string ResolveModsDirectory(string gameDir, string versionName, LauncherConfig config)
        {
            bool isVersionIsolated = config.VersionIsolationIndex == 0;
            return isVersionIsolated
                ? Path.Combine(gameDir, "versions", versionName, "mods")
                : Path.Combine(gameDir, "mods");
        }

        private static string RewriteProfileId(string profileJson, string versionName)
        {
            try
            {
                if (JsonNode.Parse(profileJson) is JsonObject root)
                {
                    root["id"] = versionName;
                    return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Fabric Profile] 改写 id 失败: {ex.Message}");
            }

            return profileJson;
        }

        private async Task<string?> InstallFabricApiModAsync(
            string mcVersion,
            string loaderVersion,
            string versionName,
            string gameDir,
            LauncherConfig config,
            IProgress<DownloadProgressReport> progress)
        {
            string modsDir = ResolveModsDirectory(gameDir, versionName, config);

            try
            {
                Directory.CreateDirectory(modsDir);

                var result = await _modService.EnsureFabricApiAsync(mcVersion, loaderVersion, modsDir, progress);

                string action = result.AlreadyInstalled ? "已就绪" : "已安装";
                string note = string.IsNullOrWhiteSpace(result.Note) ? string.Empty : $"（{result.Note}）";

                StatusText = $"Fabric API {result.VersionNumber} {action}：{result.FilePath}{note}";

                return $"（Fabric API {result.VersionNumber} {action}{note}）";
            }
            catch (Exception ex)
            {
                StatusText = $"警告：Fabric API 自动安装失败（Loader 已装好）：{ex.Message}";
                Debug.WriteLine($"[Fabric API Install Error] {ex}");
                return $"（警告：Fabric API 自动安装失败：{ex.Message}）";
            }
        }

        private async Task InstallFabricAsync(
            string mcVersion,
            string loaderVersion,
            string gameDir,
            string versionName,
            IProgress<DownloadProgressReport> progress)
        {
            string versionDir = Path.Combine(gameDir, "versions", versionName);
            Directory.CreateDirectory(versionDir);

            progress.Report(new DownloadProgressReport { CurrentFileName = "正在配置 Fabric 配置..." });

            string profileUrl = $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}/{loaderVersion}/profile/json";
            string profileJson = await HttpClient.GetStringAsync(profileUrl);

            profileJson = RewriteProfileId(profileJson, versionName);

            string jsonPath = Path.Combine(versionDir, $"{versionName}.json");
            await File.WriteAllTextAsync(jsonPath, profileJson);

            using var doc = JsonDocument.Parse(profileJson);
            if (doc.RootElement.TryGetProperty("libraries", out var libraries))
            {
                var libList = libraries.EnumerateArray().ToList();
                int totalLibs = libList.Count;
                int completedLibs = 0;

                foreach (var lib in libList)
                {
                    string? name = lib.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    string? url = lib.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;

                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;

                    var parts = name.Split(':');
                    if (parts.Length >= 3)
                    {
                        string group = parts[0].Replace('.', '/');
                        string artifact = parts[1];
                        string version = parts[2];
                        string classifier = parts.Length > 3 ? $"-{parts[3]}" : "";
                        string fileName = $"{artifact}-{version}{classifier}.jar";
                        string relPath = $"{group}/{artifact}/{version}/{fileName}";

                        string baseUrl = url.EndsWith("/") ? url : url + "/";
                        string downloadUrl = $"{baseUrl}{relPath}";

                        string localPath = Path.Combine(gameDir, "libraries", relPath.Replace('/', Path.DirectorySeparatorChar));

                        if (!File.Exists(localPath) || new FileInfo(localPath).Length < 1024)
                        {
                            if (File.Exists(localPath)) File.Delete(localPath);

                            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                            byte[] data = await HttpClient.GetByteArrayAsync(downloadUrl);
                            
                            if (data.Length < 1024)
                            {
                                throw new Exception($"下载的依赖库文件异常: {fileName}");
                            }
                            
                            await File.WriteAllBytesAsync(localPath, data);
                        }

                        completedLibs++;
                        progress.Report(new DownloadProgressReport 
                        { 
                            TotalFiles = totalLibs, 
                            CompletedFiles = completedLibs, 
                            CurrentFileName = $"下载 Fabric 依赖: {fileName}" 
                        });
                    }
                }
            }
        }

        private async Task InstallForgeAsync(string mcVersion, string forgeVersion, string gameDir, IProgress<DownloadProgressReport> progress)
        {
            string installerPath = Path.Combine(Path.GetTempPath(), $"forge-installer-{forgeVersion}.jar");

            progress.Report(new DownloadProgressReport { CurrentFileName = "正在下载 Forge 安装器..." });
            
            string officialUrl = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mcVersion}-{forgeVersion}/forge-{mcVersion}-{forgeVersion}-installer.jar";
            byte[] installerData = await HttpClient.GetByteArrayAsync(officialUrl);
            await File.WriteAllBytesAsync(installerPath, installerData);

            progress.Report(new DownloadProgressReport { CurrentFileName = "正在静默运行 Forge 安装器 (请稍候)..." });

            string javaExe = "java"; 
            var config = ConfigService.Load();
            if (!string.IsNullOrEmpty(config.SelectedJavaPath)) javaExe = config.SelectedJavaPath;

            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            psi.ArgumentList.Add("-jar");
            psi.ArgumentList.Add(installerPath);
            psi.ArgumentList.Add("--installClient");
            psi.ArgumentList.Add(gameDir);

            using var process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                    throw new Exception("Forge 安装器执行失败，可能是 Java 版本不兼容或网络问题！");
            }

            try { File.Delete(installerPath); } catch { }
            progress.Report(new DownloadProgressReport { CurrentFileName = "Forge 安装完成" });
        }

        // ==========================================
        // 翻页逻辑：统一搜索执行
        // ==========================================
        private async Task ExecuteSearchAsync(string query, int page)
        {
            if (IsSearchingMods) return;
            
            // 核心修改：使用下载页选中的实例
            string selectedVer = SelectedDownloadInstance ?? "";
            if (string.IsNullOrEmpty(selectedVer)) 
            {
                StatusText = "请先在顶部选择一个游戏实例！";
                return;
            }

            string mcVersion = GetBaseVersionFromJson(selectedVer) ?? selectedVer;
            string loader = "Vanilla";
            if (selectedVer.Contains("fabric", StringComparison.OrdinalIgnoreCase)) loader = "fabric";
            else if (selectedVer.Contains("forge", StringComparison.OrdinalIgnoreCase)) loader = "forge";

            if (loader == "Vanilla")
            {
                StatusText = "提示：当前选中的是原版，无法加载模组。请选择带 Fabric/Forge 的实例。";
                SearchResults.Clear();
                return;
            }

            CurrentPage = page;
            IsSearchingMods = true;
            StatusText = $"正在获取第 {page} 页...";

            int offset = (page - 1) * 20;
            var response = await _modrinthService.SearchAsync(query, mcVersion, loader, offset, 20);

            TotalPages = Math.Max(1, (int)Math.Ceiling((double)response.TotalHits / 20));
            if (CurrentPage > TotalPages) CurrentPage = 1;

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                SearchResults.Clear();
                foreach (var mod in response.Results) SearchResults.Add(mod);
                
                StatusText = response.TotalHits > 0 
                    ? $"共找到 {response.TotalHits} 个模组，第 {CurrentPage}/{TotalPages} 页" 
                    : "未找到相关模组";
            });
            
            foreach (var mod in response.Results)
            {
                Console.WriteLine($"[图片加载] 尝试加载: {mod.Title}, 链接: '{mod.IconUrl}'");
                
                if (string.IsNullOrWhiteSpace(mod.IconUrl)) 
                {
                    Console.WriteLine($"[图片加载] 跳过 {mod.Title}，因为没有图标链接。");
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try 
                    {
                        var bmp = await _modrinthService.DownloadImageAsync(mod.IconUrl);
                        if (bmp != null)
                        {
                            Console.WriteLine($"[图片加载] 成功: {mod.Title}");
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => mod.CoverImage = bmp);
                        }
                        else
                        {
                            Console.WriteLine($"[图片加载] 失败(返回Null): {mod.Title}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[图片加载] 异常 {mod.Title}: {ex.Message}");
                    }
                });
            }
            
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsSearchingMods = false;
                PrevPageCommand.NotifyCanExecuteChanged();
                NextPageCommand.NotifyCanExecuteChanged();
            });
        }

        [RelayCommand]
        public async Task SearchModsAsync()
        {
            _lastQuery = SearchQuery;
            _isSearchMode = true;
            await ExecuteSearchAsync(_lastQuery, 1); // 搜索新词时重置到第一页
        }

        [RelayCommand]
        public async Task LoadRecommendedModsAsync()
        {
            _lastQuery = ""; // 推荐列表为空查询
            _isSearchMode = false;
            await ExecuteSearchAsync(_lastQuery, 1);
        }

        private bool CanGoPrev() => CurrentPage > 1 && !IsSearchingMods;
        [RelayCommand(CanExecute = nameof(CanGoPrev))]
        private async Task PrevPageAsync() => await ExecuteSearchAsync(_lastQuery, CurrentPage - 1);

        private bool CanGoNext() => CurrentPage < TotalPages && !IsSearchingMods;
        [RelayCommand(CanExecute = nameof(CanGoNext))]
        private async Task NextPageAsync() => await ExecuteSearchAsync(_lastQuery, CurrentPage + 1);
        // ==========================================

        [RelayCommand]
        public async Task ViewModDetailAsync(ModrinthSearchResult? mod)
        {
            if (mod == null) return;
            CurrentModDetail = mod;
            IsModDetailVisible = true;
            IsLoadingVersions = true;
            ModVersionGroups.Clear();
            SelectedModVersion = null;

            var config = ConfigService.Load();
            string selectedVer = config.SelectedGameVersion ?? "";
            string mcVersion = GetBaseVersionFromJson(selectedVer) ?? selectedVer;

            var groups = await _modrinthService.GetAllVersionsGroupedAsync(mod.ProjectId, mcVersion);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var g in groups) ModVersionGroups.Add(g);
                IsLoadingVersions = false;
            });
        }

        [RelayCommand]
        private void CloseModDetail()
        {
            IsModDetailVisible = false;
            
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                CurrentModDetail = null;
                SelectedModVersion = null;
                ModVersionGroups.Clear();
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        [RelayCommand]
        public async Task InstallSelectedModAsync()
        {
            if (SelectedModVersion == null || CurrentModDetail == null) return;

            // ... 前面的代码（配置路径、清理旧版本等）保持不变 ...
            var config = ConfigService.Load();
            string selectedVer = config.SelectedGameVersion ?? "";
            if (string.IsNullOrEmpty(selectedVer)) return;

            try
            {
                IsDownloading = true;
                DownloadProgress = 0;
                StatusText = "Preparing to download mod...";
                CurrentModDetail.DownloadStatus = "Preparing...";

                string baseDir = string.IsNullOrWhiteSpace(config.MinecraftRoot) 
                    ? System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".minecraft") 
                    : config.MinecraftRoot;
                
                bool isIsolated = config.VersionIsolationIndex == 0;
                string modsDir = isIsolated 
                    ? System.IO.Path.Combine(baseDir, "versions", selectedVer, "mods") 
                    : System.IO.Path.Combine(baseDir, "mods");
                
                System.IO.Directory.CreateDirectory(modsDir);

                string mcVersion = GetBaseVersionFromJson(selectedVer) ?? selectedVer;
                string loader = selectedVer.Contains("forge", StringComparison.OrdinalIgnoreCase) ? "forge" : "fabric";

                // 1. 清理该模组的其他旧版本 (保持你原来的逻辑)
                var allHistoricalFileNames = ModVersionGroups
                    .SelectMany(g => g.Versions)
                    .Select(v => v.FileName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (System.IO.Directory.Exists(modsDir))
                {
                    var existingFiles = System.IO.Directory.GetFiles(modsDir, "*.jar");
                    foreach (var file in existingFiles)
                    {
                        string fileName = System.IO.Path.GetFileName(file);
                        if (allHistoricalFileNames.Contains(fileName) && 
                            !string.Equals(fileName, SelectedModVersion.FileName, StringComparison.OrdinalIgnoreCase))
                        {
                            System.IO.File.Delete(file);
                            Debug.WriteLine($"[ModManager] Replaced existing version of {CurrentModDetail.Title} (removed {fileName})");
                        }
                    }
                }

                // 2. 核心优化：扫描本地已存在的依赖，防止重复下载前置模组
                StatusText = "Checking installed dependencies...";
                var installedProjectIds = await _modrinthService.GetLocalProjectIdsAsync(modsDir);
                
                // 从已安装列表中移除主模组本身，因为用户明确想要安装/更新这个模组
                if (installedProjectIds.Contains(SelectedModVersion.Id)) 
                {
                    // 注意：这里的 ProjectId 应该是 CurrentModDetail.ProjectId，而不是 SelectedModVersion.Id
                    installedProjectIds.Remove(CurrentModDetail.ProjectId);
                }

                var downloadedProjectIds = new HashSet<string>();

                // 3. 递归下载主模组及其依赖（传入本地已安装列表）
                await DownloadModAndDependenciesAsync(SelectedModVersion, modsDir, mcVersion, loader, downloadedProjectIds, installedProjectIds);

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CurrentModDetail.DownloadStatus = "✅ Install Success";
                    StatusText = "Mod and dependencies installed successfully!";
                });
            }
            catch (Exception ex)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CurrentModDetail.DownloadStatus = $"❌ Failed: {ex.Message}";
                    StatusText = $"Install failed: {ex.Message}";
                });
            }
            finally
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsDownloading = false;
                });
            }
        }

        private async Task DownloadModAndDependenciesAsync(
            ModrinthVersion modVersion, 
            string modsDir, 
            string mcVersion, 
            string loader, 
            HashSet<string> downloadedIds,
            HashSet<string> installedProjectIds) // 新增参数
        {
            if (downloadedIds.Contains(modVersion.Id)) return;
            downloadedIds.Add(modVersion.Id);

            string localPath = System.IO.Path.Combine(modsDir, modVersion.FileName);

            // 如果文件已存在，直接跳过
            if (System.IO.File.Exists(localPath))
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Debug.WriteLine($"[ModManager] Skipped existing file: {modVersion.FileName}");
                    StatusText = $"Skipped existing: {modVersion.FileName}";
                });
            }
            else
            {
                // 执行下载
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    StatusText = $"Downloading {modVersion.Name} ({modVersion.VersionNumber})...";
                    Debug.WriteLine($"[ModManager] Automatically downloaded {modVersion.Name} ({modVersion.VersionNumber})");
                });

                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SMCL-Launcher/1.0");
                
                using var response = await client.GetAsync(modVersion.DownloadUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? -1L;
                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new System.IO.FileStream(localPath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None, 8192, true);

                byte[] buffer = new byte[8192];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    totalRead += bytesRead;
                    
                    if (totalBytes > 0)
                    {
                        double progress = (double)totalRead / totalBytes * 100;
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => DownloadProgress = progress);
                    }
                }
            }

            // 解析 required（必须的）依赖
            var requiredDeps = modVersion.Dependencies.Where(d => d.DependencyType == "required").ToList();
            foreach (var dep in requiredDeps)
            {
                // 【核心修复】如果本地已经安装了这个 ProjectId，就不下载依赖了
                if (!string.IsNullOrEmpty(dep.ProjectId) && installedProjectIds.Contains(dep.ProjectId))
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        StatusText = $"Skipped prerequisite: {dep.ProjectId}, already installed locally.";
                        Debug.WriteLine($"[ModManager] Skipped prerequisite {dep.ProjectId}, already installed locally.");
                    });
                    continue;
                }

                ModrinthVersion? depVersion = null;
                
                if (!string.IsNullOrEmpty(dep.VersionId))
                {
                    depVersion = await _modrinthService.GetVersionByIdAsync(dep.VersionId);
                }
                else if (!string.IsNullOrEmpty(dep.ProjectId))
                {
                    depVersion = await _modrinthService.GetLatestCompatibleVersionAsync(dep.ProjectId, mcVersion, loader);
                }

                if (depVersion != null)
                {
                    // 递归下载依赖的依赖
                    await DownloadModAndDependenciesAsync(depVersion, modsDir, mcVersion, loader, downloadedIds, installedProjectIds);
                }
            }
        }

        private string? GetBaseVersionFromJson(string versionName)
        {
            var config = ConfigService.Load();
            string gameDir = string.IsNullOrWhiteSpace(config.MinecraftRoot)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".minecraft")
                : config.MinecraftRoot;

            string jsonPath = Path.Combine(gameDir, "versions", versionName, $"{versionName}.json");
            if (File.Exists(jsonPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                    if (doc.RootElement.TryGetProperty("inheritsFrom", out var inheritElem))
                    {
                        string? baseVer = inheritElem.GetString();
                        if (!string.IsNullOrEmpty(baseVer))
                            return baseVer;
                    }
                }
                catch { }
            }
            return null;
        }

        [RelayCommand]
        private void SelectModVersion(ModrinthVersion? version)
        {
            if (version != null) SelectedModVersion = version;
        }
    }
}