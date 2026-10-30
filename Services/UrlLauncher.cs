using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SMCL.Services
{
    /// <summary>
    /// 使用系统默认浏览器打开链接。
    /// </summary>
    public static class UrlLauncher
    {
        public static bool Open(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    return true;
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    Process.Start("xdg-open", url);
                    return true;
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start("open", url);
                    return true;
                }

                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            catch
            {
                // 某些桌面环境没有 xdg-open 时再尝试 ShellExecute
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
