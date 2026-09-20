using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WindowSpy
{
    /// <summary>DeepSeek Chat API 客户端（同步封装，供步骤引擎调用）</summary>
    public static class DeepSeekClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        private const string ApiBase = "https://api.deepseek.com";

        /// <summary>配置文件：exe 旁 deepseek.json（ApiKey + Model）</summary>
        public static string ApiKey { get; set; } = "";
        public static string Model { get; set; } = "deepseek-chat";

        private static string ConfigPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "deepseek.json");

        public static void SaveConfig()
        {
            try
            {
                var json = JsonSerializer.Serialize(new { apiKey = ApiKey, model = Model },
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }

        public static void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                if (doc.RootElement.TryGetProperty("apiKey", out var k)) ApiKey = k.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("model", out var m)) Model = m.GetString() ?? "deepseek-chat";
            }
            catch { }
        }

        /// <summary>发送一条 user 消息，返回模型回复文本（失败抛异常）</summary>
        public static string Ask(string prompt, string? model = null)
        {
            if (string.IsNullOrWhiteSpace(ApiKey))
                throw new InvalidOperationException("未配置 DeepSeek API Key");

            var body = JsonSerializer.Serialize(new
            {
                model = string.IsNullOrWhiteSpace(model) ? Model : model,
                stream = false,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                }
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, ApiBase + "/chat/completions");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

            using var resp = Http.SendAsync(req).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("choices")[0]
                     .GetProperty("message").GetProperty("content").GetString() ?? "";
        }
    }
}
