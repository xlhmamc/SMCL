using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using SMCL.ViewModels;

namespace SMCL.Models
{
    public partial class ModModel : ViewModelBase
    {
        private string _fileName = string.Empty;
        public string FileName
        {
            get => _fileName;
            set => SetProperty(ref _fileName, value);
        }

        private string _filePath = string.Empty;
        public string FilePath
        {
            get => _filePath;
            set => SetProperty(ref _filePath, value);
        }

        private bool _isEnabled;
        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        [ObservableProperty]
        private Bitmap? _icon;
    }
}