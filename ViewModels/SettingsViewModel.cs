using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using SMCL.Services;
using System;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SMCL.Models;

namespace SMCL.ViewModels
{
    public class SettingsViewModel : ViewModelBase
    {
        public ICommand? BackCommand { get; set; }
        public ICommand SwitchTabCommand { get; }

        #region 页面切换与按钮背景控制
        private bool _isGlobalGameSelected = true;
        public bool IsGlobalGameSelected
        {
            get => _isGlobalGameSelected;
            set
            {
                if (SetProperty(ref _isGlobalGameSelected, value))
                {
                    OnPropertyChanged(nameof(IsLauncherSelected));
                    OnPropertyChanged(nameof(GlobalGameBtnBg));
                    OnPropertyChanged(nameof(LauncherBtnBg));
                }
            }
        }

        public bool IsLauncherSelected => !IsGlobalGameSelected;

        // 将类型从 string 改为 Bitmap?
        public Bitmap? GlobalGameBtnBg => GetBitmap(IsGlobalGameSelected 
            ? "avares://SMCL/Assets/Button1.png" 
            : "avares://SMCL/Assets/Button0.png");

        public Bitmap? LauncherBtnBg => GetBitmap(IsLauncherSelected 
            ? "avares://SMCL/Assets/Button1.png" 
            : "avares://SMCL/Assets/Button0.png");

        // 辅助方法：通过 Avalonia AssetLoader 读取资源图片
        private static Bitmap? GetBitmap(string uri)
        {
            try
            {
                return new Bitmap(AssetLoader.Open(new Uri(uri)));
            }
            catch
            {
                return null;
            }
        }
        #endregion

        #region 全局游戏设置属性
        private int _versionIsolationIndex;
        public int VersionIsolationIndex
        {
            get => _versionIsolationIndex;
            set { if (SetProperty(ref _versionIsolationIndex, value)) SaveSettings(); }
        }

        private string _minecraftRoot = ".minecraft";
        public string MinecraftRoot
        {
            get => _minecraftRoot;
            set { if (SetProperty(ref _minecraftRoot, value)) SaveSettings(); }
        }

        private string _jvmArgs = string.Empty;
        public string JvmArgs
        {
            get => _jvmArgs;
            set { if (SetProperty(ref _jvmArgs, value)) SaveSettings(); }
        }

        private string _maxMemory = "4096";
        public string MaxMemory
        {
            get => _maxMemory;
            set { if (SetProperty(ref _maxMemory, value)) SaveSettings(); }
        }
        public int ParsedMaxMemory
        {
            get
            {
                if (int.TryParse(_maxMemory, out int result) && result >= 512)
                    return result;
                return 4096; // 兜底默认值 4G
            }
        }
        #endregion

        #region 启动器设置属性
        private int _downloadSourceIndex;
        public int DownloadSourceIndex
        {
            get => _downloadSourceIndex;
            set { if (SetProperty(ref _downloadSourceIndex, value)) SaveSettings(); }
        }

        private int _maxDownloadThreads = 6;
        public int MaxDownloadThreads
        {
            get => _maxDownloadThreads;
            set { if (SetProperty(ref _maxDownloadThreads, value)) SaveSettings(); }
        }

        private int _gameLaunchBehaviorIndex;
        public int GameLaunchBehaviorIndex
        {
            get => _gameLaunchBehaviorIndex;
            set { if (SetProperty(ref _gameLaunchBehaviorIndex, value)) SaveSettings(); }
        }

        private bool _isSimplifiedLogEnabled;
        public bool IsSimplifiedLogEnabled
        {
            get => _isSimplifiedLogEnabled;
            set { if (SetProperty(ref _isSimplifiedLogEnabled, value)) SaveSettings(); }
        }

        private bool _autoOpenConsole;
        public bool AutoOpenConsole
        {
            get => _autoOpenConsole;
            set { if (SetProperty(ref _autoOpenConsole, value)) SaveSettings(); }
        }

        private bool _autoCheckUpdates = true;
        public bool AutoCheckUpdates
        {
            get => _autoCheckUpdates;
            set { if (SetProperty(ref _autoCheckUpdates, value)) SaveSettings(); }
        }

        private bool _hideInheritedVanilla = true;
        public bool HideInheritedVanilla
        {
            get => _hideInheritedVanilla;
            set { if (SetProperty(ref _hideInheritedVanilla, value)) SaveSettings(); }
        }
        #endregion

        public SettingsViewModel()
        {
            SwitchTabCommand = new RelayCommand<string>(tab =>
            {
                IsGlobalGameSelected = tab == "GlobalGame";
            });

            LoadSettings();
        }

        public void LoadSettings()
        {
            var config = ConfigService.Load();

            _versionIsolationIndex = config.VersionIsolationIndex;
            _minecraftRoot = string.IsNullOrWhiteSpace(config.MinecraftRoot) ? ".minecraft" : config.MinecraftRoot;
            _jvmArgs = config.JvmArgs ?? string.Empty;
            _maxMemory = string.IsNullOrWhiteSpace(config.MaxMemory) ? "4096" : config.MaxMemory;

            _downloadSourceIndex = config.DownloadSourceIndex;
            _maxDownloadThreads = config.MaxDownloadThreads <= 0 ? 6 : config.MaxDownloadThreads;
            _gameLaunchBehaviorIndex = config.GameLaunchBehaviorIndex;
            _isSimplifiedLogEnabled = config.IsSimplifiedLogEnabled;
            _autoOpenConsole = config.AutoOpenConsole;
            _autoCheckUpdates = config.AutoCheckUpdates;
            _hideInheritedVanilla = config.HideInheritedVanilla;
            
            OnPropertyChanged(nameof(HideInheritedVanilla));
            OnPropertyChanged(nameof(VersionIsolationIndex));
            OnPropertyChanged(nameof(MinecraftRoot));
            OnPropertyChanged(nameof(JvmArgs));
            OnPropertyChanged(nameof(MaxMemory));
            OnPropertyChanged(nameof(ParsedMaxMemory));
            OnPropertyChanged(nameof(DownloadSourceIndex));
            OnPropertyChanged(nameof(MaxDownloadThreads));
            OnPropertyChanged(nameof(GameLaunchBehaviorIndex));
            OnPropertyChanged(nameof(IsSimplifiedLogEnabled));
            OnPropertyChanged(nameof(AutoOpenConsole));
            OnPropertyChanged(nameof(AutoCheckUpdates));
        }

        public void SaveSettings()
        {
            var config = ConfigService.Load();

            config.VersionIsolationIndex = VersionIsolationIndex;
            config.MinecraftRoot = MinecraftRoot;
            config.JvmArgs = JvmArgs;
            config.MaxMemory = MaxMemory;

            config.DownloadSourceIndex = DownloadSourceIndex;
            config.MaxDownloadThreads = MaxDownloadThreads;
            config.GameLaunchBehaviorIndex = GameLaunchBehaviorIndex;
            config.IsSimplifiedLogEnabled = IsSimplifiedLogEnabled;
            config.AutoOpenConsole = AutoOpenConsole;
            config.AutoCheckUpdates = AutoCheckUpdates;
            config.HideInheritedVanilla = HideInheritedVanilla;

            ConfigService.Save(config);
        }
    }
}