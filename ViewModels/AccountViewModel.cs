using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using SMCL.Services;
using SMCL.Models;

namespace SMCL.ViewModels
{
    public class AccountItem : ViewModelBase
    {
        private string _username = string.Empty;
        public string Username
        {
            get => _username;
            set => SetProperty(ref _username, value);
        }

        public string Uuid { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
        public string AccountType { get; set; } = string.Empty;

        private string? _skinPath;
        public string? SkinPath
        {
            get => _skinPath;
            set => SetProperty(ref _skinPath, value);
        }

        private bool _isEditing;
        public bool IsEditing
        {
            get => _isEditing;
            set => SetProperty(ref _isEditing, value);
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value) && value)
                {
                    OnLog?.Invoke($"Selected account set to '{Username}' ({Uuid})", "Launcher.Account", LogLevel.INFO);
                    OnSelected?.Invoke(this);
                }
            }
        }

        public Action<AccountItem>? OnSelected { get; set; }
        public Action<AccountItem>? OnAccountUpdated { get; set; }
        public Action<AccountItem>? OnDeleteRequested { get; set; }
        public Action<string, string, LogLevel>? OnLog { get; set; }

        public ICommand EndEditCommand { get; }
        public ICommand DeleteCommand { get; }

        public AccountItem(AccountModel model)
        {
            _username = model.Username;
            Uuid = model.Uuid;
            AccessToken = model.AccessToken;
            AccountType = model.AccountType;

            EndEditCommand = new RelayCommand(() =>
            {
                if (IsEditing)
                {
                    IsEditing = false;
                    
                    if (AccountType == "离线账户" && !string.IsNullOrWhiteSpace(Username))
                    {
                        Uuid = ConfigService.GenerateOfflineUuid(Username);
                    }

                    OnLog?.Invoke($"Profile renamed to '{Username}' (UUID: {Uuid})", "Launcher.Account", LogLevel.INFO);
                    OnAccountUpdated?.Invoke(this);
                }
            });

            DeleteCommand = new RelayCommand(() =>
            {
                OnLog?.Invoke($"Deleting account profile for '{Username}'...", "Launcher.Account", LogLevel.WARN);
                OnDeleteRequested?.Invoke(this);
            });
        }

        public AccountModel ToModel() => new AccountModel
        {
            Username = Username,
            Uuid = Uuid,
            AccessToken = AccessToken,
            AccountType = AccountType
        };
        private string? _yggdrasilServerUrl;
        public string? YggdrasilServerUrl
        {
            get => _yggdrasilServerUrl;
            set => SetProperty(ref _yggdrasilServerUrl, value);
        }
    }

    public class AccountViewModel : ViewModelBase
    {
        public Action<string, string, LogLevel>? LogAction { get; set; }
        public ICommand? BackCommand { get; set; }
        public ObservableCollection<AccountItem> Accounts { get; } = new ObservableCollection<AccountItem>();

        public ICommand AddOfflineAccountCommand { get; }
        public ICommand AddLittleSkinAccountCommand { get; }
        public ICommand AddMicrosoftAccountCommand { get; }

        private void Log(string message, string source = "Launcher.AccountManager", LogLevel level = LogLevel.INFO)
        {
            LogAction?.Invoke(message, source, level);
        }

        public AccountModel? GetSelectedAccount()
        {
            var selected = Accounts.FirstOrDefault(a => a.IsSelected)?.ToModel();
            if (selected == null)
            {
                Log("No active account is currently selected.", level: LogLevel.WARN);
            }
            return selected;
        }

        public AccountViewModel()
        {
            LoadAccountsFromConfig();

            AddOfflineAccountCommand = new RelayCommand(() =>
            {
                string newName = $"Steve_{new Random().Next(100, 999)}";
                var newAccount = new AccountModel
                {
                    Username = newName,
                    Uuid = ConfigService.GenerateOfflineUuid(newName),
                    AccessToken = "0",
                    AccountType = "离线账户"
                };
                Log($"Creating new offline account: '{newName}'");
                AddAccount(newAccount);
            });

            AddLittleSkinAccountCommand = new RelayCommand(() =>
            {
                Log("LittleSkin authentication flow invoked.", level: LogLevel.TRACE);
            });

            AddMicrosoftAccountCommand = new RelayCommand(() =>
            {
                Log("Microsoft OAuth authentication flow invoked.", level: LogLevel.TRACE);
            });
        }

        private void LoadAccountsFromConfig()
        {
            var config = ConfigService.Load();
            foreach (var model in config.Accounts)
            {
                var item = CreateAccountItem(model);
                if (model.Uuid == config.SelectedAccountUuid)
                {
                    item.IsSelected = true;
                }
                Accounts.Add(item);
            }
            Log($"Loaded {Accounts.Count} account profile(s) into memory.");
        }

        public void AddAccount(AccountModel model)
        {
            var newItem = CreateAccountItem(model);

            // 取消其他账号选中状态，默认选中当前新添加的账号
            foreach (var acc in Accounts)
            {
                acc.IsSelected = false;
            }
            newItem.IsSelected = true;

            Accounts.Add(newItem);

            SaveAccountsToConfig();
        }

        private AccountItem CreateAccountItem(AccountModel model)
        {
            return new AccountItem(model)
            {
                OnSelected = SelectAccount,
                OnAccountUpdated = _ => SaveAccountsToConfig(),
                OnDeleteRequested = DeleteAccount,
                OnLog = (msg, src, level) => Log(msg, src, level)
            };
        }

        private void DeleteAccount(AccountItem item)
        {
            bool wasSelected = item.IsSelected;
            Accounts.Remove(item);
            Log($"Removed account '{item.Username}' from launcher storage.", level: LogLevel.INFO);

            if (wasSelected && Accounts.Count > 0)
            {
                Accounts[0].IsSelected = true;
                Log($"Reassigned active profile selection to '{Accounts[0].Username}'.", level: LogLevel.INFO);
            }

            SaveAccountsToConfig();
        }

        private void SelectAccount(AccountItem selectedItem)
        {
            foreach (var item in Accounts)
            {
                if (item != selectedItem)
                {
                    item.IsSelected = false;
                }
            }

            var config = ConfigService.Load();
            config.SelectedAccountUuid = selectedItem.Uuid;
            ConfigService.Save(config);
        }

        public void SaveAccountsToConfig()
        {
            var config = ConfigService.Load();
            config.Accounts = Accounts.Select(a => a.ToModel()).ToList();
            
            var selected = Accounts.FirstOrDefault(a => a.IsSelected);
            config.SelectedAccountUuid = selected?.Uuid;

            ConfigService.Save(config);
        }
    }
}