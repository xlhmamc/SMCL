using System;
using System.Collections.Generic;
using System.IO;

namespace SMCL.Services
{
    public static class GameScannerService
    {
        /// <summary>
        /// 扫描指定 .minecraft 路径下的 versions 目录
        /// </summary>
        public static List<string> ScanGameVersions(string minecraftRoot)
        {
            var versions = new List<string>();
            string versionsDir = Path.Combine(minecraftRoot, "versions");

            if (!Directory.Exists(versionsDir))
            {
                try
                {
                    Directory.CreateDirectory(versionsDir);
                }
                catch { }
                return versions;
            }

            foreach (var dir in Directory.GetDirectories(versionsDir))
            {
                string dirName = Path.GetFileName(dir);
                // 存在对应的 json 文件即判定为有效游戏版本
                string jsonFile = Path.Combine(dir, $"{dirName}.json");
                if (File.Exists(jsonFile))
                {
                    versions.Add(dirName);
                }
            }

            return versions;
        }
    }
}