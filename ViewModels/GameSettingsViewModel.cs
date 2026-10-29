using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using SMCL.Models;
using SMCL.Services;

namespace SMCL.ViewModels
{
    // 实例配置的数据模型
    public class InstanceConfigData
    {
        public bool UseCustomSettings { get; set; } = false;
        public int CustomMemory { get; set; } = 4096;
        public string CustomJvmArgs { get; set; } = string.Empty;
        public string CustomMcArgs { get; set; } = string.Empty;
    }

    public class GameSettingsViewModel : ViewModelBase
    {
        public ICommand? BackCommand { get; set; }

        private static readonly Bitmap ActiveBg = new Bitmap(AssetLoader.Open(new Uri("avares://SMCL/Assets/Button1.png")));
        private static readonly Bitmap InactiveBg = new Bitmap(AssetLoader.Open(new Uri("avares://SMCL/Assets/Button0.png")));

        private string _errorMessage = string.Empty;
        public string ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, value);
        }

        private string _instanceName = "1.20.1";
        public string InstanceName
        {
            get => _instanceName;
            set
            {
                if (SetProperty(ref _instanceName, value))
                {
                    LoadInstanceConfig(); 
                    RefreshMods();
                }
            }
        }

        // --- 重命名专用属性 ---
        private string _renameInput = string.Empty;
        public string RenameInput
        {
            get => _renameInput;
            set => SetProperty(ref _renameInput, value);
        }

        public string MinecraftRoot { get; set; } = ".minecraft";
        public int VersionIsolationIndex { get; set; } = 0;

        // --- 配置属性 ---
        private bool _useCustomSettings = false;
        public bool UseCustomSettings
        {
            get => _useCustomSettings;
            set { if (SetProperty(ref _useCustomSettings, value)) SaveInstanceConfig(); }
        }

        private int _customMemory = 4096;
        public int CustomMemory
        {
            get => _customMemory;
            set { if (SetProperty(ref _customMemory, value)) SaveInstanceConfig(); }
        }

        private string _customJvmArgs = string.Empty;
        public string CustomJvmArgs
        {
            get => _customJvmArgs;
            set { if (SetProperty(ref _customJvmArgs, value)) SaveInstanceConfig(); }
        }

        private string _customMcArgs = string.Empty;
        public string CustomMcArgs
        {
            get => _customMcArgs;
            set { if (SetProperty(ref _customMcArgs, value)) SaveInstanceConfig(); }
        }

        // --- 模组列表相关 ---
        public ObservableCollection<ModModel> Mods { get; } = new ObservableCollection<ModModel>();
        private readonly ModrinthService _modrinthService = new ModrinthService();
        private static readonly HttpClient _iconHttpClient = new HttpClient();

        // --- 页签控制 ---
        private string _selectedTab = "GameConfig";
        public string SelectedTab
        {
            get => _selectedTab;
            set
            {
                if (SetProperty(ref _selectedTab, value))
                {
                    OnPropertyChanged(nameof(GameConfigBtnBg));
                    OnPropertyChanged(nameof(ModManagerBtnBg));
                    OnPropertyChanged(nameof(IsGameConfigSelected));
                    OnPropertyChanged(nameof(IsModManagerSelected));
                    if (value == "ModManager") RefreshMods();
                }
            }
        }

        public Bitmap GameConfigBtnBg => SelectedTab == "GameConfig" ? ActiveBg : InactiveBg;
        public Bitmap ModManagerBtnBg => SelectedTab == "ModManager" ? ActiveBg : InactiveBg;
        public bool IsGameConfigSelected => SelectedTab == "GameConfig";
        public bool IsModManagerSelected => SelectedTab == "ModManager";

        public ICommand SwitchTabCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand RefreshModsCommand { get; }
        public ICommand DeleteModCommand { get; }
        public ICommand RenameInstanceCommand { get; }

        public GameSettingsViewModel()
        {
            SwitchTabCommand = new RelayCommand<string>(tab => { if (tab != null) SelectedTab = tab; });
            
            OpenFolderCommand = new RelayCommand<string>(folderType =>
            {
                string dir = folderType switch
                {
                    "Root" => Path.Combine(MinecraftRoot, "versions", InstanceName),
                    "Mods" => GetModsFolderPath(),
                    "Saves" => Path.Combine(MinecraftRoot, "versions", InstanceName, "saves"),
                    _ => MinecraftRoot
                };

                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true, Verb = "open" });
                }
                catch (Exception ex) { ErrorMessage = $"无法打开文件夹: {ex.Message}"; }
            });

            RefreshModsCommand = new RelayCommand(RefreshMods);
            DeleteModCommand = new RelayCommand<ModModel>(DeleteMod);
            RenameInstanceCommand = new RelayCommand(ExecuteRenameInstance);
        }

        // ================= 核心：模组列表与图标加载 =================

        public string GetModsFolderPath()
        {
            bool isIsolated = VersionIsolationIndex == 0;
            string versionDir = Path.Combine(MinecraftRoot, "versions", InstanceName);
            return isIsolated ? Path.Combine(versionDir, "mods") : Path.Combine(MinecraftRoot, "mods");
        }

        public void RefreshMods()
        {
            Mods.Clear();
            string modsDir = GetModsFolderPath();
            if (!Directory.Exists(modsDir))
            {
                try { Directory.CreateDirectory(modsDir); } catch { return; }
            }

            // 过滤并排序
            var files = Directory.GetFiles(modsDir, "*.*")
                .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || 
                            f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f);

            foreach (var file in files)
            {
                bool isEnabled = file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
                var mod = new ModModel
                {
                    FileName = Path.GetFileName(file),
                    FilePath = file,
                    IsEnabled = isEnabled
                };

                mod.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(ModModel.IsEnabled))
                    {
                        ToggleModState(mod);
                    }
                };
                // 【修复】不再在这里挂载事件，由 UI 直接 TwoWay 绑定
                Mods.Add(mod);
                _ = LoadModIconAsync(mod); 
            }
        }

        private bool _isToggling = false;

        public void ToggleModState(ModModel mod)
        {
            if (_isToggling) return;
            _isToggling = true;

            string oldPath = mod.FilePath;
            string newPath = mod.IsEnabled 
                ? (oldPath.EndsWith(".disabled") ? oldPath.Substring(0, oldPath.Length - 9) : oldPath)
                : (oldPath.EndsWith(".disabled") ? oldPath : oldPath + ".disabled");

            if (oldPath != newPath)
            {
                try
                {
                    if (File.Exists(oldPath))
                    {
                        File.Move(oldPath, newPath);
                        mod.FilePath = newPath;
                        mod.FileName = Path.GetFileName(newPath);
                    }
                }
                catch (Exception ex)
                {
                    ErrorMessage = $"修改模组状态失败（可能被游戏占用）：{ex.Message}";
                    // 回滚状态，利用 Dispatcher 推送到下一次消息循环执行，防止同帧冲突
                    Dispatcher.UIThread.Post(() => 
                    {
                        _isToggling = false;
                        mod.IsEnabled = !mod.IsEnabled;
                    });
                    return; // 直接返回，交给 Post 里的 finally 类似逻辑处理
                }
            }
            _isToggling = false;
        }

        private async Task LoadModIconAsync(ModModel mod)
        {
            try
            {
                string sha1 = "";
                using (var stream = File.OpenRead(mod.FilePath))
                {
                    byte[] hashBytes = SHA1.HashData(stream);
                    sha1 = Convert.ToHexString(hashBytes).ToLower();
                }

                string cacheDir = Path.Combine(MinecraftRoot, "cache", "mod_icons");
                string cachePath = Path.Combine(cacheDir, $"{sha1}.png");

                if (File.Exists(cachePath))
                {
                    using var fs = File.OpenRead(cachePath);
                    var bmp = new Bitmap(fs);
                    await Dispatcher.UIThread.InvokeAsync(() => mod.Icon = bmp);
                    return;
                }

                var pidMap = await _modrinthService.GetIconUrlsFromHashesAsync(new List<string> { sha1 });
                if (pidMap.TryGetValue(sha1, out var projectId) && !string.IsNullOrEmpty(projectId))
                {
                    string? iconUrl = await _modrinthService.GetProjectIconUrlAsync(projectId);
                    if (!string.IsNullOrEmpty(iconUrl))
                    {
                        byte[] imgData = await _iconHttpClient.GetByteArrayAsync(iconUrl);
                        Directory.CreateDirectory(cacheDir);
                        await File.WriteAllBytesAsync(cachePath, imgData);

                        using var ms = new MemoryStream(imgData);
                        var bmp = new Bitmap(ms);
                        await Dispatcher.UIThread.InvokeAsync(() => mod.Icon = bmp);
                    }
                }
            }
            catch { /* 忽略图标加载错误 */ }
        }

        private void DeleteMod(ModModel? mod)
        {
            if (mod != null && File.Exists(mod.FilePath))
            {
                try
                {
                    File.Delete(mod.FilePath);
                    Mods.Remove(mod);
                }
                catch (Exception ex) { ErrorMessage = $"删除模组失败：{ex.Message}"; }
            }
        }

        public void AddModFile(string sourcePath)
        {
            if (!File.Exists(sourcePath) || !sourcePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) return;
            
            string modsDir = GetModsFolderPath();
            if (!Directory.Exists(modsDir)) Directory.CreateDirectory(modsDir);

            string destPath = Path.Combine(modsDir, Path.GetFileName(sourcePath));
            try
            {
                File.Copy(sourcePath, destPath, overwrite: true);
                RefreshMods();
            }
            catch (Exception ex) { ErrorMessage = $"添加模组失败：{ex.Message}"; }
        }

        // ================= 重命名实例逻辑 =================

        private void ExecuteRenameInstance()
        {
            string oldName = InstanceName;
            string newName = RenameInput?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(newName)) { ErrorMessage = "新名称不能为空！"; return; }
            if (newName == oldName) { ErrorMessage = "新名称与原名称相同！"; return; }
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { ErrorMessage = "名称包含非法字符！"; return; }

            string basePath = Path.Combine(MinecraftRoot, "versions");
            string oldDir = Path.Combine(basePath, oldName);
            string newDir = Path.Combine(basePath, newName);

            if (Directory.Exists(newDir)) { ErrorMessage = "该名称已存在，请换一个！"; return; }
            if (!Directory.Exists(oldDir)) { ErrorMessage = "找不到当前版本的文件夹！"; return; }

            try
            {
                // 1. 重命名文件夹
                Directory.Move(oldDir, newDir);

                // 2. 重命名核心 JSON
                string oldJson = Path.Combine(newDir, $"{oldName}.json");
                string newJson = Path.Combine(newDir, $"{newName}.json");
                if (File.Exists(oldJson)) File.Move(oldJson, newJson);

                // 3. 修改 JSON 内部 ID
                if (File.Exists(newJson))
                {
                    string jsonContent = File.ReadAllText(newJson);
                    jsonContent = System.Text.RegularExpressions.Regex.Replace(
                        jsonContent, 
                        $"\"id\"\\s*:\\s*\"{oldName}\"", 
                        $"\"id\":\"{newName}\""
                    );
                    File.WriteAllText(newJson, jsonContent);
                }

                // 4. 更新状态和配置
                InstanceName = newName;
                RenameInput = string.Empty;
                ErrorMessage = "重命名成功！请返回主界面刷新列表。";
                
                var config = ConfigService.Load();
                if (config.SelectedGameVersion == oldName)
                {
                    config.SelectedGameVersion = newName;
                    ConfigService.Save(config);
                }
            }
            catch (Exception ex) { ErrorMessage = $"重命名失败（游戏可能正在运行）：{ex.Message}"; }
        }

        // ================= 配置文件读写 =================

        private string GetInstanceConfigPath()
        {
            string versionDir = Path.Combine(MinecraftRoot, "versions", InstanceName);
            return Path.Combine(versionDir, "instance_settings.json");
        }

        public void LoadInstanceConfig()
        {
            string configPath = GetInstanceConfigPath();
            if (File.Exists(configPath))
            {
                try
                {
                    var data = JsonSerializer.Deserialize<InstanceConfigData>(File.ReadAllText(configPath));
                    if (data != null)
                    {
                        _useCustomSettings = data.UseCustomSettings;
                        _customMemory = data.CustomMemory;
                        _customJvmArgs = data.CustomJvmArgs;
                        _customMcArgs = data.CustomMcArgs;

                        OnPropertyChanged(nameof(UseCustomSettings));
                        OnPropertyChanged(nameof(CustomMemory));
                        OnPropertyChanged(nameof(CustomJvmArgs));
                        OnPropertyChanged(nameof(CustomMcArgs));
                    }
                }
                catch { }
            }
        }

        private void SaveInstanceConfig()
        {
            try
            {
                string versionDir = Path.Combine(MinecraftRoot, "versions", InstanceName);
                if (!Directory.Exists(versionDir)) return; // 防止意外建空文件夹

                var data = new InstanceConfigData
                {
                    UseCustomSettings = this.UseCustomSettings,
                    CustomMemory = this.CustomMemory,
                    CustomJvmArgs = this.CustomJvmArgs,
                    CustomMcArgs = this.CustomMcArgs
                };
                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetInstanceConfigPath(), json);
            }
            catch { }
        }
    }
}