using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SMCL.ViewModels;
using SMCL.Models;
using SMCL.Services;

namespace SMCL.Views
{
    public partial class AccountView : UserControl
    {
        public AccountView()
        {
            InitializeComponent();
        }

        private void OnUsernameDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (sender is TextBlock textBlock && textBlock.DataContext is AccountItem item)
            {
                item.IsEditing = true;
            }
        }

        private void OnEditingLostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox && textBox.DataContext is AccountItem item)
            {
                item.EndEditCommand.Execute(null);
            }
        }

        private void OnEditingKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (sender is TextBox textBox && textBox.DataContext is AccountItem item)
                {
                    item.EndEditCommand.Execute(null);
                }
            }
        }

        private async void OnChangeSkinClicked(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is AccountItem item)
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select Skin Image File (.png)",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("PNG Image")
                        {
                            Patterns = new[] { "*.png" }
                        }
                    }
                });

                if (files.Count > 0)
                {
                    item.SkinPath = files[0].Path.LocalPath;
                    item.OnLog?.Invoke($"Custom skin file set for '{item.Username}': {item.SkinPath}", "Launcher.AccountUI", LogLevel.INFO);
                    
                    if (this.DataContext is AccountViewModel vm)
                    {
                        vm.SaveAccountsToConfig();
                    }
                }
                else
                {
                    item.OnLog?.Invoke($"Skin selection canceled for account '{item.Username}'.", "Launcher.AccountUI", LogLevel.TRACE);
                }
            }
        }
        /// <summary>
        /// 点击添加 LittleSkin 账号时弹出登录框
        /// </summary>
        private async void OnAddLittleSkinClick(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is Window parentWindow)
            {
                var loginWindow = new LittleSkinLoginWindow();
                bool? isSuccess = await loginWindow.ShowDialog<bool?>(parentWindow);

                if (isSuccess == true && loginWindow.AuthResult != null && DataContext is AccountViewModel vm)
                {
                    var res = loginWindow.AuthResult;

                    // 构造 AccountModel 传入 AddAccount 方法
                    vm.AddAccount(new AccountModel
                    {
                        Username = res.Username ?? "LittleSkin 用户",
                        Uuid = res.Uuid ?? "",
                        AccessToken = res.AccessToken ?? "0",
                        AccountType = "LittleSkin",
                        YggdrasilServerUrl = "https://littleskin.cn/api/yggdrasil"
                    });
                }
            }
        }

        // 在 AccountView.axaml.cs 中添加：
        private async void OnAddMicrosoftClick(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is Window parentWindow)
            {
                var loginWindow = new MicrosoftLoginWindow();
                bool? isSuccess = await loginWindow.ShowDialog<bool?>(parentWindow);

                if (isSuccess == true && loginWindow.AuthResult != null && DataContext is AccountViewModel vm)
                {
                    var res = loginWindow.AuthResult;

                    vm.AddAccount(new AccountModel
                    {
                        Username = res.Username ?? "Microsoft 用户",
                        Uuid = res.Uuid ?? "",
                        AccessToken = res.AccessToken ?? "0",
                        AccountType = "Microsoft"
                    });
                }
            }
        }
    }
}