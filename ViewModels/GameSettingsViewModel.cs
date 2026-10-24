using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json; // 新增
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.Input;
using SMCL.Models;

namespace SMCL.ViewModels
{
    // 新增：实例配置的数据模型
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

        // 错误提示信息，用于通知 View 层弹窗
        private string _errorMessage = string.Empty;
        public string ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, value);
        }

        // 当前选中的版本实例名称
        private string _instanceName = "1.20.1";
        public string InstanceName
        {
            get => _instanceName;
            set
            {
                if (SetProperty(ref _instanceName, value))
                {
                    LoadInstanceConfig(); // === 新增：切换版本时加载对应配置 ===
                    RefreshMods();
                }
            }
        }

        public string MinecraftRoot { get; set; } = ".minecraft";
        public int VersionIsolationIndex { get; set; } = 0;

        // 独立游戏配置属性
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

        // 模组列表集合
        public ObservableCollection<ModModel> Mods { get; } = new ObservableCollection<ModModel>();

        // 侧边栏标签页控制 (保持不变)
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

                    if (value == "ModManager")
                    {
                        RefreshMods();
                    }
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

        public GameSettingsViewModel()
        {
            SwitchTabCommand = new RelayCommand<string>(tab =>
            {
                if (tab != null) SelectedTab = tab;
            });

            OpenFolderCommand = new RelayCommand<string>(folderType =>
            {
                string modsDir = GetModsFolderPath();
                if (!Directory.Exists(modsDir)) Directory.CreateDirectory(modsDir);

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = modsDir,
                        UseShellExecute = true,
                        Verb = "open"
                    });
                }
                catch (Exception ex) { ErrorMessage = $"无法打开文件夹: {ex.Message}"; }
            });

            RefreshModsCommand = new RelayCommand(RefreshMods);
            DeleteModCommand = new RelayCommand<ModModel>(DeleteMod);
        }

        // === 新增：获取配置文件路径 ===
        private string GetInstanceConfigPath()
        {
            // 无论是否隔离，实例配置都放在 versions/<版本号>/instance_settings.json
            string versionDir = Path.Combine(MinecraftRoot, "versions", InstanceName);
            if (!Directory.Exists(versionDir)) Directory.CreateDirectory(versionDir);
            return Path.Combine(versionDir, "instance_settings.json");
        }

        // === 新增：加载实例配置 ===
        public void LoadInstanceConfig()
        {
            string configPath = GetInstanceConfigPath();
            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    var data = JsonSerializer.Deserialize<InstanceConfigData>(json);
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
                catch { /* 忽略损坏的配置文件 */ }
            }
            else
            {
                // 如果不存在，重置为默认值
                _useCustomSettings = false;
                _customMemory = 4096;
                _customJvmArgs = string.Empty;
                _customMcArgs = string.Empty;
                
                OnPropertyChanged(nameof(UseCustomSettings));
                OnPropertyChanged(nameof(CustomMemory));
                OnPropertyChanged(nameof(CustomJvmArgs));
                OnPropertyChanged(nameof(CustomMcArgs));
            }
        }

        // === 新增：保存实例配置 ===
        private void SaveInstanceConfig()
        {
            try
            {
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

        public string GetModsFolderPath()
        {
            bool isIsolated = VersionIsolationIndex == 0;
            string versionDir = Path.Combine(MinecraftRoot, "versions", InstanceName);
            string basePath = isIsolated ? versionDir : MinecraftRoot;
            return Path.Combine(basePath, "mods");
        }

        public void RefreshMods()
        {
            Mods.Clear();
            string modsDir = GetModsFolderPath();
            if (!Directory.Exists(modsDir))
            {
                try { Directory.CreateDirectory(modsDir); } catch { return; }
            }

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

                Mods.Add(mod);
            }
        }

        private void ToggleModState(ModModel mod)
        {
            try
            {
                string oldPath = mod.FilePath;
                string newPath;

                if (mod.IsEnabled)
                {
                    newPath = oldPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                        ? oldPath.Substring(0, oldPath.Length - ".disabled".Length)
                        : oldPath;
                }
                else
                {
                    newPath = oldPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                        ? oldPath
                        : oldPath + ".disabled";
                }

                if (oldPath != newPath && File.Exists(oldPath))
                {
                    File.Move(oldPath, newPath);
                    mod.FilePath = newPath;
                    mod.FileName = Path.GetFileName(newPath);
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"修改模组状态失败（游戏可能正在运行）：{ex.Message}";
                // 如果失败，反向回滚 UI 的勾选状态
                mod.PropertyChanged -= (s, e) => { }; // 简易防死循环
                mod.IsEnabled = !mod.IsEnabled;
            }
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
                catch (Exception ex)
                {
                    ErrorMessage = $"删除模组失败：{ex.Message}";
                }
            }
        }

        public void AddModFile(string sourcePath)
        {
            if (!File.Exists(sourcePath)) return;

            string modsDir = GetModsFolderPath();
            if (!Directory.Exists(modsDir)) Directory.CreateDirectory(modsDir);

            string destPath = Path.Combine(modsDir, Path.GetFileName(sourcePath));
            try
            {
                File.Copy(sourcePath, destPath, overwrite: true);
                RefreshMods();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"添加模组失败：{ex.Message}";
            }
        }
    }
}