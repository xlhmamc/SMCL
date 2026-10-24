using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SMCL.Services
{
    public class YggdrasilAuthResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? AccessToken { get; set; }
        public string? Uuid { get; set; }
        public string? Username { get; set; }
    }

    public class YggdrasilAuthService
    {
        private static readonly HttpClient HttpClient = new HttpClient();
        public const string LittleSkinApiUrl = "https://littleskin.cn/api/yggdrasil";

        public static async Task<YggdrasilAuthResult> AuthenticateAsync(string username, string password, string serverUrl = LittleSkinApiUrl)
        {
            try
            {
                string endpoint = $"{serverUrl.TrimEnd('/')}/authserver/authenticate";

                var requestBody = new
                {
                    agent = new { name = "Minecraft", version = 1 },
                    username = username,
                    password = password,
                    requestUser = true
                };

                string jsonContent = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var response = await HttpClient.PostAsync(endpoint, content);
                string responseJson = await response.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;

                if (response.IsSuccessStatusCode)
                {
                    string accessToken = root.GetProperty("accessToken").GetString() ?? "";
                    var selectedProfile = root.GetProperty("selectedProfile");
                    string uuid = selectedProfile.GetProperty("id").GetString() ?? "";
                    string name = selectedProfile.GetProperty("name").GetString() ?? "";

                    return new YggdrasilAuthResult
                    {
                        Success = true,
                        AccessToken = accessToken,
                        Uuid = uuid,
                        Username = name
                    };
                }
                else
                {
                    string errorMsg = root.TryGetProperty("errorMessage", out var err) 
                        ? err.GetString() ?? "登录失败" 
                        : "账号或密码错误";

                    return new YggdrasilAuthResult
                    {
                        Success = false,
                        ErrorMessage = errorMsg
                    };
                }
            }
            catch (Exception ex)
            {
                return new YggdrasilAuthResult
                {
                    Success = false,
                    ErrorMessage = $"网络请求异常: {ex.Message}"
                };
            }
        }
    }
}