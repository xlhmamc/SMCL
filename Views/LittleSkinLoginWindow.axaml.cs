using Avalonia.Controls;
using Avalonia.Interactivity;
using SMCL.Services;

namespace SMCL.Views
{
    public partial class LittleSkinLoginWindow : Window
    {
        public YggdrasilAuthResult? AuthResult { get; private set; }

        public LittleSkinLoginWindow()
        {
            InitializeComponent();
        }

        private async void OnLoginClick(object? sender, RoutedEventArgs e)
        {
            string username = EmailTextBox.Text?.Trim() ?? "";
            string password = PasswordTextBox.Text ?? "";

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                ShowError("账号和密码不能为空！");
                return;
            }

            LoginButton.IsEnabled = false;
            LoginButton.Content = "登录中...";
            ErrorTextBlock.IsVisible = false;

            // 调用 LittleSkin 认证 API
            var result = await YggdrasilAuthService.AuthenticateAsync(username, password);

            if (result.Success)
            {
                AuthResult = result;
                Close(true); // 登录成功关闭窗口并返回 true
            }
            else
            {
                ShowError(result.ErrorMessage ?? "登录失败，请检查账号密码");
                LoginButton.IsEnabled = true;
                LoginButton.Content = "登录";
            }
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            Close(false);
        }

        private void ShowError(string message)
        {
            ErrorTextBlock.Text = message;
            ErrorTextBlock.IsVisible = true;
        }
    }
}