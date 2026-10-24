using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SMCL.ViewModels;

namespace SMCL.Views
{
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
        }

        private async void OnBrowseMinecraftFolderClick(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择 Minecraft 根目录 (.minecraft)",
                AllowMultiple = false
            });

            if (folders.Count > 0 && DataContext is SettingsViewModel vm)
            {
                vm.MinecraftRoot = folders[0].Path.LocalPath;
            }
        }
    }
}