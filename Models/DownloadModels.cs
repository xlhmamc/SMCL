using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SMCL.Models
{
    // 官方/镜像 Version Manifest 结构
    public class VersionManifest
    {
        [JsonPropertyName("latest")]
        public LatestVersion? Latest { get; set; }

        [JsonPropertyName("versions")]
        public List<VersionManifestItem> Versions { get; set; } = new();
    }

    public class LatestVersion
    {
        [JsonPropertyName("release")]
        public string Release { get; set; } = "";

        [JsonPropertyName("snapshot")]
        public string Snapshot { get; set; } = "";
    }

    public class VersionManifestItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = ""; // "release" | "snapshot"

        [JsonPropertyName("url")]
        public string Url { get; set; } = "";

        [JsonPropertyName("releaseTime")]
        public string ReleaseTime { get; set; } = "";
    }

    // 单个下载任务单元
    public class DownloadItem
    {
        public string Url { get; set; } = "";
        public string LocalPath { get; set; } = "";
        public string Sha1 { get; set; } = "";
        public long Size { get; set; }
    }

    // 下载进度通知数据
    public class DownloadProgressReport
    {
        public int TotalFiles { get; set; }
        public int CompletedFiles { get; set; }
        public string CurrentFileName { get; set; } = "";
        public double Percentage => TotalFiles == 0 ? 0 : (double)CompletedFiles / TotalFiles * 100;
    }
}