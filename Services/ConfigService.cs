using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SMCL.Services
{
    // 定义账号数据模型
    public class AccountModel
    {
        public string Username { get; set; } = string.Empty;
        public string Uuid { get; set; } = string.Empty;
        public string AccessToken { get; set; } = "0";
        public string AccountType { get; set; } = "离线账户"; // "离线账户" | "LittleSkin" | "Microsoft"
        public string? YggdrasilServerUrl { get; set; } // 外置登录 API 地址
        public string? SkinPath { get; set; }
    }

    // 启动器全局配置模型
    public class LauncherConfig
    {
        public string MinecraftRoot { get; set; } = Path.Combine(Environment.CurrentDirectory, ".minecraft");
        public string? SelectedGameVersion { get; set; }
        public string? SelectedJavaPath { get; set; }
        public string MaxMemory { get; set; } = "4096";
        public bool IsSimplifiedLogEnabled { get; set; } = false;
        public List<AccountModel> Accounts { get; set; } = new List<AccountModel>();
        public string? SelectedAccountUuid { get; set; }
        public int VersionIsolationIndex { get; set; } = 0;
        public string JvmArgs { get; set; } = string.Empty;
        public int DownloadSourceIndex { get; set; } = 0;
        public int MaxDownloadThreads { get; set; } = 6;
        public int GameLaunchBehaviorIndex { get; set; } = 0;
        public bool AutoOpenConsole { get; set; } = false;
        public bool AutoCheckUpdates { get; set; } = true;
        public bool HideInheritedVanilla { get; set; } = true;
    }

    public static class ConfigService
    {
        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        /// <summary>
        /// 加载本地配置文件
        /// </summary>
        public static LauncherConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    return JsonSerializer.Deserialize<LauncherConfig>(json) ?? new LauncherConfig();
                }
            }
            catch (Exception)
            {
                // 若解析失败则返回默认配置
            }

            return new LauncherConfig();
        }

        /// <summary>
        /// 保存配置到本地
        /// </summary>
        public static void Save(LauncherConfig config)
        {
            try
            {
                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception)
            {
                // 忽略写入错误
            }
        }

        /// <summary>
        /// 生成标准离线 Minecraft UUID (MD5: OfflinePlayer:Username)
        /// 遵循 V3 UUID 标准，确保和大部分服务端的离线验证一致
        /// </summary>
        public static string GenerateOfflineUuid(string username)
        {
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
            
            // 强制设置为 UUID v3 规范
            hash[6] = (byte)(hash[6] & 0x0f | 0x30);
            hash[8] = (byte)(hash[8] & 0x3f | 0x80);
            
            return new Guid(hash).ToString("N"); // 返回无连字符的 32 位字符串
        }
    }
}