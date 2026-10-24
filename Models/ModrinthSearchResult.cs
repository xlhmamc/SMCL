using System.Collections.Generic;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SMCL.Models
{
    public class ModrinthSearchResponse
    {
        public List<ModrinthSearchResult> Results { get; set; } = new();
        public int TotalHits { get; set; } = 0; // 匹配到的总数量
    }
    public partial class ModrinthSearchResult : ObservableObject
    {
        public string ProjectId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string IconUrl { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public int Downloads { get; set; }

        [ObservableProperty]
        private Bitmap? _coverImage;

        [ObservableProperty]
        private string _downloadStatus = "";
    }
}