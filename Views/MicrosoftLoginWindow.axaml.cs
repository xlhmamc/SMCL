using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SMCL.Services;
using Avalonia.Input.Platform;

namespace SMCL.Views
{
    public partial class MicrosoftLoginWindow : Window
    {
        public MicrosoftAuthResult? AuthResult { get; private set; }
        private MicrosoftDeviceCodeResponse? _deviceCodeRes;

        public MicrosoftLoginWindow()
        {
            InitializeComponent();
            StartDeviceCodeAuth();
        }

        private async void StartDeviceCodeAuth()
        {
            var (data, errorMsg) = await MicrosoftAuthService.GetDeviceCodeAsync();

            if (data == null)
            {
                StatusTextBlock.Text = errorMsg ?? "获取验证码失败，请检查网络设置";
                StatusTextBlock.Foreground = Avalonia.Media.Brushes.Red;
                return;
            }

            _deviceCodeRes = data;
            UserCodeTextBlock.Text = _deviceCodeRes.user_code;
            OpenBrowserButton.IsEnabled = true;
            StatusTextBlock.Text = "等待在浏览器中完成授权...";

            // 开始后台轮询授权结果
            AuthResult = await MicrosoftAuthService.PollAndAuthenticateAsync(_deviceCodeRes);

            if (AuthResult != null && AuthResult.Success)
            {
                Close(true); // 授权成功，关闭弹窗
            }
            else if (AuthResult != null)
            {
                StatusTextBlock.Text = AuthResult.ErrorMessage ?? "授权失败";
                StatusTextBlock.Foreground = Avalonia.Media.Brushes.Red;
            }
        }

        private async void OnOpenBrowserClick(object? sender, RoutedEventArgs e)
        {
            if (_deviceCodeRes == null) return;

            // 1. 复制验证码到剪贴板
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(_deviceCodeRes.user_code);
            }

            // 2. 打开浏览器访问验证网址
            string url = _deviceCodeRes.verification_uri;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    Process.Start("xdg-open", url);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    Process.Start("open", url);
            }
            catch { }
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            Close(false);
        }
    }
}