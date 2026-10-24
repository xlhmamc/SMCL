using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SMCL.Models
{
    // 新增：表示依赖项
    public class ModrinthDependency
    {
        public string ProjectId { get; set; } = string.Empty;
        public string VersionId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string DependencyType { get; set; } = string.Empty; // required, optional, incompatible
    }

    public class ModrinthVersion
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string VersionNumber { get; set; } = string.Empty;
        public List<string> GameVersions { get; set; } = new();
        public List<string> Loaders { get; set; } = new();
        public string DownloadUrl { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string VersionType { get; set; } = "release";
        public DateTime DatePublished { get; set; }
        
        // 新增：该版本包含的依赖项列表
        public List<ModrinthDependency> Dependencies { get; set; } = new(); 
    }

    public partial class ModVersionGroup : ObservableObject
    {
        public string GameVersion { get; set; } = string.Empty;
        public List<ModrinthVersion> Versions { get; set; } = new();

        [ObservableProperty]
        private bool _isExpanded; 
    }
}