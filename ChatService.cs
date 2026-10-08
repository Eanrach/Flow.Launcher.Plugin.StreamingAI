using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Flow.Launcher.Plugin.StreamingAI
{
    /// <summary>一条对话消息</summary>
    public class ChatMessage
    {
        public string Role { get; set; }
        public string Content { get; set; }
    }

    /// <summary>
    /// 流式聊天客户端。
    /// 根据 Settings.Provider 自动选择协议：
    ///  - OpenAI：POST chat/completions（stream:true），SSE 逐块读取 delta.content，
    ///    可对接 OpenAI、Azure OpenAI、DeepSeek、OpenRouter、Groq、LM Studio、Ollama(/v1) 等。
    ///  - Ollama：原生 POST /api/chat（stream:true），逐行读取 message.content，
    ///    并支持从 /api/tags 自动发现本地模型。
    ///
    /// 关键健壮性：带「首 token 超时」。若 TimeoutSeconds 内未收到任何数据，抛出明确
    /// 的 TimeoutException（由调用方显示为失败），避免无限卡在「等待首个 token」；
    /// 一旦收到首个片段即撤掉超时，长回答不会被误截断。
    /// </summary>
    public class ChatService
    {
        // 复用单个 HttpClient 实例。Timeout 设为无限，统一由每次调用的超时令牌控制，
        // 否则底层 Http.Timeout 抛出的 TaskCanceledException 会被误判为用户取消。
        private static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        /// <summary>发起一次流式对话。每收到一个文本片段就回调 onDelta。</summary>
        public Task StreamAsync(
            List<ChatMessage> history,
            string userPrompt,
            Action<string> onDelta,
            CancellationToken ct,
            Settings s)
        {
            if (s.Provider == Provider.Ollama)
                return StreamOllamaAsync(history, userPrompt, onDelta, ct, s);
            return StreamOpenAIAsync(history, userPrompt, onDelta, ct, s);
        }

        // ============ 公共辅助：构造 messages ============
        private static List<object> BuildMessages(Settings s, List<ChatMessage> history, string userPrompt)
        {
            var messages = new List<object>();
            if (!string.IsNullOrWhiteSpace(s.SystemPrompt))
                messages.Add(new { role = "system", content = s.SystemPrompt });
            if (history != null)
                foreach (var m in history)
                    messages.Add(new { role = m.Role, content = m.Content });
            messages.Add(new { role = "user", content = userPrompt });
            return messages;
        }

        // ============ OpenAI 兼容流式 ============
        private async Task StreamOpenAIAsync(
            List<ChatMessage> history, string userPrompt,
            Action<string> onDelta, CancellationToken ct, Settings s)
        {
            var messages = BuildMessages(s, history, userPrompt);
            var body = new Dictionary<string, object>
            {
                ["model"] = s.Model,
                ["messages"] = messages,
                ["temperature"] = s.Temperature,
                ["max_tokens"] = s.MaxTokens,
                ["stream"] = true,
                ["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, s.Endpoint);
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            switch (s.AuthMode)
            {
                case AuthMode.Bearer:
                    if (!string.IsNullOrEmpty(s.ApiKey))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ApiKey);
                    break;
                case AuthMode.ApiKey:
                    if (!string.IsNullOrEmpty(s.ApiKey) && !string.IsNullOrEmpty(s.ApiKeyHeaderName))
                        req.Headers.TryAddWithoutValidation(s.ApiKeyHeaderName, s.ApiKey);
                    break;
            }

            await StreamWithTimeoutAsync(req, s, ct, onDelta, line =>
            {
                var data = StripPrefix(line);
                if (data.Length == 0) return ParseResult.Skip;
                if (data == "[DONE]") return ParseResult.StopSignal;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out var err))
                        return ParseResult.Error(ExtractErrorMessage(err));
                    if (root.TryGetProperty("choices", out var choices) &&
                        choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        if (choice.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("content", out var content) &&
                            content.ValueKind == JsonValueKind.String)
                        {
                            var text = content.GetString();
                            if (!string.IsNullOrEmpty(text))
                                return new ParseResult(true, text);
                        }
                    }
                    return ParseResult.Skip;
                }
                catch (JsonException) { return ParseResult.Skip; }
            }).ConfigureAwait(false);
        }

        // ============ 原生 Ollama 流式（/api/chat）============
        private async Task StreamOllamaAsync(
            List<ChatMessage> history, string userPrompt,
            Action<string> onDelta, CancellationToken ct, Settings s)
        {
            var baseUrl = (s.OllamaBaseUrl ?? "http://localhost:11434").Trim().TrimEnd('/');
            var endpoint = baseUrl + "/api/chat";
            var messages = BuildMessages(s, history, userPrompt);

            var body = new Dictionary<string, object>
            {
                ["model"] = s.Model,
                ["messages"] = messages,
                ["stream"] = true,
                ["options"] = new Dictionary<string, object>
                {
                    ["temperature"] = s.Temperature,
                    ["num_predict"] = s.MaxTokens
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            await StreamWithTimeoutAsync(req, s, ct, onDelta, line =>
            {
                var data = StripPrefix(line);
                if (data.Length == 0) return ParseResult.Skip;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out var err))
                        return ParseResult.Error(ExtractErrorMessage(err));
                    if (root.TryGetProperty("message", out var msg) &&
                        msg.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.String)
                    {
                        var text = content.GetString();
                        if (!string.IsNullOrEmpty(text))
                            return new ParseResult(true, text);
                    }
                    if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                        return ParseResult.StopSignal;
                    return ParseResult.Skip;
                }
                catch (JsonException) { return ParseResult.Skip; }
            }).ConfigureAwait(false);
        }

        // ============ Ollama 模型发现（/api/tags）============
        /// <summary>列出 Ollama 本地已拉取的模型（GET {OllamaBaseUrl}/api/tags）。</summary>
        public async Task<List<string>> ListOllamaModelsAsync(Settings s, CancellationToken ct = default)
        {
            var baseUrl = (s.OllamaBaseUrl ?? "http://localhost:11434").Trim().TrimEnd('/');
            var url = baseUrl + "/api/tags";

            using var timeoutCts = new CancellationTokenSource();
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            using var resp = await Http.GetAsync(url, linked.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);

            var models = new List<string>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("models", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in arr.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                        models.Add(name.GetString());
                    else if (m.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
                        models.Add(model.GetString());
                }
            }
            return models;
        }

        // ============ 带首 token 超时的流式读取核心 ============
        private static async Task StreamWithTimeoutAsync(
            HttpRequestMessage req, Settings s, CancellationToken ct,
            Action<string> onDelta, Func<string, ParseResult> parse)
        {
            var readTimeout = Math.Max(10, s.TimeoutSeconds);
            using var timeoutCts = new CancellationTokenSource();
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(readTimeout));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var token = linked.Token;

            try
            {
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token)
                                         .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var reader = new StreamReader(stream);

                bool gotFirst = false;
                string line;
                // 首个 token 之前用 linked token（含超时）；收到首个 token 后改用 ct（仅用户取消），
                // 并撤掉超时，避免长回答在生成中途被误杀。
                while ((line = await reader.ReadLineAsync(gotFirst ? ct : token).ConfigureAwait(false)) != null)
                {
                    var r = parse(line);
                    if (r.IsError)
                        throw new InvalidOperationException("接口返回错误：" + r.ErrorMsg);
                    if (r.Emit && !string.IsNullOrEmpty(r.Text))
                    {
                        onDelta(r.Text);
                        if (!gotFirst) { gotFirst = true; timeoutCts.Cancel(); }
                    }
                    if (r.Stop)
                        break;
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // 由超时触发（非用户取消）——转为明确的超时错误，让上层显示为失败。
                throw new TimeoutException(
                    $"在 {readTimeout} 秒内未收到任何数据（首 token 超时）。请检查：\n" +
                    $"· Endpoint / Ollama 地址是否可达、服务是否已启动\n" +
                    $"· 网络是否通畅\n" +
                    $"· 若使用云端 API，API Key 是否已正确填写");
            }
        }

        // ============ 小工具 ============
        private static string StripPrefix(string line)
        {
            var t = line.Trim();
            if (t.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return t.Substring(5).Trim();
            return t;
        }

        private static string ExtractErrorMessage(JsonElement err)
        {
            if (err.ValueKind == JsonValueKind.String)
                return err.GetString();
            if (err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                return m.GetString();
            return "未知错误";
        }

        /// <summary>单行的解析结果。</summary>
        private readonly struct ParseResult
        {
            public bool Emit { get; }
            public string Text { get; }
            public bool Stop { get; }
            public bool IsError { get; }
            public string ErrorMsg { get; }
            public ParseResult(bool emit, string text, bool stop = false, bool isError = false, string errorMsg = null)
                => (Emit, Text, Stop, IsError, ErrorMsg) = (emit, text, stop, isError, errorMsg);

            public static readonly ParseResult Skip = new ParseResult(false, null);
            public static readonly ParseResult StopSignal = new ParseResult(false, null, true);
            public static ParseResult Error(string msg) => new ParseResult(false, null, false, true, msg);
        }
    }
}
