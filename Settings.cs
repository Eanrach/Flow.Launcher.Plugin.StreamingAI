using System.Text.Json.Serialization;

namespace Flow.Launcher.Plugin.StreamingAI
{
    /// <summary>
    /// 后端供应商。OpenAI 走 OpenAI 兼容协议（可对接绝大多数第三方 endpoint）；
    /// Ollama 走原生 /api/chat 协议，并支持从 /api/tags 自动发现本地模型。
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum Provider
    {
        OpenAI,
        Ollama
    }

    /// <summary>
    /// 鉴权方式。OpenAI 系用 Bearer；Azure OpenAI 用 ApiKey（请求头 api-key）；
    /// 本地模型（Ollama / LM Studio）通常无需密钥，用 None。
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AuthMode
    {
        Bearer,
        ApiKey,
        None
    }

    /// <summary>
    /// 插件设置。字段即 JSON 持久化的内容，可被 Flow Launcher 的设置存储自动序列化。
    /// </summary>
    public class Settings
    {
        public Settings()
        {
            // 默认值：标准 OpenAI 兼容端点
            Endpoint = "https://api.openai.com/v1/chat/completions";
            Provider = Provider.OpenAI;
            OllamaBaseUrl = "http://localhost:11434";
            ApiKey = "";
            Model = "gpt-4o-mini";
            SystemPrompt = "You are a helpful assistant. Answer concisely and in the same language as the user.";
            Temperature = 0.7;
            MaxTokens = 2048;
            AuthMode = AuthMode.Bearer;
            ApiKeyHeaderName = "api-key";
            TimeoutSeconds = 120;
        }

        /// <summary>后端供应商</summary>
        public Provider Provider { get; set; }

        /// <summary>Ollama 基址（仅 Provider=Ollama 时使用，例如 http://localhost:11434）</summary>
        public string OllamaBaseUrl { get; set; }

        /// <summary>OpenAI 兼容的 chat/completions 完整地址</summary>
        public string Endpoint { get; set; }

        /// <summary>API Key（Bearer / ApiKey 模式使用；None 模式可留空）</summary>
        public string ApiKey { get; set; }

        /// <summary>模型名称，例如 gpt-4o-mini / deepseek-chat / llama3 / qwen2.5</summary>
        public string Model { get; set; }

        /// <summary>系统提示词</summary>
        public string SystemPrompt { get; set; }

        /// <summary>采样温度 0~2</summary>
        public double Temperature { get; set; }

        /// <summary>单次回复最大 token 数</summary>
        public int MaxTokens { get; set; }

        /// <summary>鉴权方式</summary>
        public AuthMode AuthMode { get; set; }

        /// <summary>ApiKey 模式下自定义请求头名称（如 Azure 的 api-key）</summary>
        public string ApiKeyHeaderName { get; set; }

        /// <summary>请求超时（秒），仅作用于建连与首字节</summary>
        public int TimeoutSeconds { get; set; }
    }
}
