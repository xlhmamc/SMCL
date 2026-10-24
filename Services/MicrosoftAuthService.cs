using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SMCL.Models;

namespace SMCL.Services
{
    public class MicrosoftDeviceCodeResponse
    {
        public string device_code { get; set; } = "";
        public string user_code { get; set; } = "";
        public string verification_uri { get; set; } = "";
        public int expires_in { get; set; }
        public int interval { get; set; } = 5;
    }

    public class MicrosoftAuthResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? AccessToken { get; set; }
        public string? Uuid { get; set; }
        public string? Username { get; set; }
    }

    public class MicrosoftAuthService
    {
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        static MicrosoftAuthService()
        {
            HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SMCL/1.0");
        }

        // Mojang / Xbox 官方 Client ID（适配 /consumers/ 个人账号端点）
        private const string ClientId = "00000000402b5328"; 

        /// <summary>
        /// 第一步：请求设备码
        /// </summary>
        public static async Task<(MicrosoftDeviceCodeResponse? Data, string? ErrorMsg)> GetDeviceCodeAsync()
        {
            try
            {
                var requestParams = new Dictionary<string, string>
                {
                    { "client_id", ClientId },
                    { "scope", "XboxLive.signin offline_access" }
                };

                var content = new FormUrlEncodedContent(requestParams);

                // 必须使用 /consumers/ 端点
                var response = await HttpClient.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode", content);
                string json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return (null, $"微软 API 报错 [{(int)response.StatusCode}]: {json}");
                }

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var data = JsonSerializer.Deserialize<MicrosoftDeviceCodeResponse>(json, options);

                return (data, null);
            }
            catch (TaskCanceledException)
            {
                return (null, "连接微软服务器超时，请检查网络或开启代理");
            }
            catch (Exception ex)
            {
                return (null, $"网络异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 第二步：轮询微软授权 Token
        /// </summary>
        public static async Task<MicrosoftAuthResult> PollAndAuthenticateAsync(MicrosoftDeviceCodeResponse deviceCodeRes)
        {
            string msAccessToken = "";
            int checkInterval = Math.Max(deviceCodeRes.interval, 3) * 1000;
            DateTime expiryTime = DateTime.Now.AddSeconds(deviceCodeRes.expires_in);

            while (DateTime.Now < expiryTime)
            {
                await Task.Delay(checkInterval);

                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                    new KeyValuePair<string, string>("client_id", ClientId),
                    new KeyValuePair<string, string>("device_code", deviceCodeRes.device_code)
                });

                // Token 轮询端点也必须保持 /consumers/
                var response = await HttpClient.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", content);
                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (response.IsSuccessStatusCode)
                {
                    msAccessToken = root.GetProperty("access_token").GetString() ?? "";
                    break;
                }

                if (root.TryGetProperty("error", out var err) && err.GetString() == "authorization_pending")
                {
                    continue;
                }

                return new MicrosoftAuthResult { Success = false, ErrorMessage = "用户取消或授权超时" };
            }

            if (string.IsNullOrEmpty(msAccessToken))
                return new MicrosoftAuthResult { Success = false, ErrorMessage = "获取微软登录 Token 失败" };

            try
            {
                // 2. Xbox Live 认证
                var xblReq = new
                {
                    Properties = new
                    {
                        AuthMethod = "RPS",
                        SiteName = "user.auth.xboxlive.com",
                        RpsTicket = $"d={msAccessToken}"
                    },
                    RelyingParty = "http://auth.xboxlive.com",
                    TokenType = "JWT"
                };
                var xblRes = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", xblReq);
                using var xblDoc = JsonDocument.Parse(xblRes);
                string xblToken = xblDoc.RootElement.GetProperty("Token").GetString() ?? "";
                string userHash = xblDoc.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString() ?? "";

                // 3. XSTS 认证
                var xstsReq = new
                {
                    Properties = new
                    {
                        SandboxId = "RETAIL",
                        UserTokens = new[] { xblToken }
                    },
                    RelyingParty = "rp://api.minecraftservices.com/",
                    TokenType = "JWT"
                };
                var xstsRes = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", xstsReq);
                using var xstsDoc = JsonDocument.Parse(xstsRes);
                string xstsToken = xstsDoc.RootElement.GetProperty("Token").GetString() ?? "";

                // 4. Minecraft 服务认证
                var mcReq = new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" };
                var mcRes = await PostJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox", mcReq);
                using var mcDoc = JsonDocument.Parse(mcRes);
                string mcAccessToken = mcDoc.RootElement.GetProperty("access_token").GetString() ?? "";

                // 5. 获取玩家 Profile（UUID与游戏名）
                using var profileReq = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
                profileReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcAccessToken);
                var profileRes = await HttpClient.SendAsync(profileReq);

                if (!profileRes.IsSuccessStatusCode)
                {
                    return new MicrosoftAuthResult { Success = false, ErrorMessage = "未在此微软账号下找到购买的 Minecraft 游戏！" };
                }

                string profileJson = await profileRes.Content.ReadAsStringAsync();
                using var profileDoc = JsonDocument.Parse(profileJson);
                string uuid = profileDoc.RootElement.GetProperty("id").GetString() ?? "";
                string username = profileDoc.RootElement.GetProperty("name").GetString() ?? "";

                return new MicrosoftAuthResult
                {
                    Success = true,
                    AccessToken = mcAccessToken,
                    Uuid = uuid,
                    Username = username
                };
            }
            catch (Exception ex)
            {
                return new MicrosoftAuthResult { Success = false, ErrorMessage = $"验证流程异常: {ex.Message}" };
            }
        }

        private static async Task<string> PostJsonAsync(string url, object body)
        {
            string json = JsonSerializer.Serialize(body);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var res = await HttpClient.PostAsync(url, content);
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsStringAsync();
        }
    }
}