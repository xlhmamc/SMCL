using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SMCL.Models;
using SMCL.ViewModels;

namespace SMCL.Views
{
    public partial class GameSettingsView : UserControl
    {
        public GameSettingsView()
        {
            InitializeComponent();
        }

        private async void OnAddModClick(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要添加的 Minecraft Mod",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Minecraft Mod (*.jar)")
                    {
                        Patterns = new[] { "*.jar" }
                    }
                }
            });

            if (files.Count > 0 && DataContext is GameSettingsViewModel vm)
            {
                foreach (var file in files)
                {
                    vm.AddModFile(file.Path.LocalPath);
                }
            }
            if (sender is ToggleSwitch toggle && toggle.DataContext is ModModel mod && DataContext is GameSettingsViewModel vm1)
            {
                vm1.ToggleModState(mod);
            }
        }
    }
}