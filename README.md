# Flow.Launcher.Plugin.StreamingAI

一个 **支持流式输出** 的 Flow Launcher AI 对话插件。输入问题 → 回车发送 → 答案在结果列表里**逐字流式刷新**，完整内容实时显示在下方的预览面板，可多轮对话、可停止、可复制。

核心特点是**供应商无关**：基于 OpenAI 兼容的 `/chat/completions` 流式接口（`stream:true` + SSE），通过配置 Endpoint / 鉴权方式 / 模型，即可对接几乎所有主流供应商。此外内置 **Ollama 原生模式**，使用 Ollama 自家的 `/api/chat` 协议并支持从 `/api/tags` **自动发现本地模型**。

## 支持接入的供应商

| 供应商 | 模式 | 鉴权方式 | Endpoint / 基址示例 |
| --- | --- | --- | --- |
| OpenAI | OpenAI 兼容 | Bearer | `https://api.openai.com/v1/chat/completions` |
| Azure OpenAI | OpenAI 兼容 | ApiKey（`api-key`） | `https://<res>.openai.azure.com/openai/deployments/<deploy>/chat/completions?api-version=2024-02-15-preview` |
| DeepSeek | OpenAI 兼容 | Bearer | `https://api.deepseek.com/v1/chat/completions` |
| OpenRouter | OpenAI 兼容 | Bearer | `https://openrouter.ai/api/v1/chat/completions` |
| Groq | OpenAI 兼容 | Bearer | `https://api.groq.com/openai/v1/chat/completions` |
| Ollama（本地） | **Ollama 原生** | None | `http://localhost:11434`（基址，插件自动拼 `/api/chat`） |
| Ollama（本地） | OpenAI 兼容 | None | `http://localhost:11434/v1/chat/completions` |
| LM Studio（本地） | OpenAI 兼容 | None | `http://localhost:1234/v1/chat/completions` |

只要供应商提供 OpenAI 格式兼容接口，直接填 Endpoint + Key 即可，无需改代码。

## Ollama 原生模式

设置面板「后端供应商」选 **Ollama 原生** 即启用：

- 走 Ollama 自带的 `POST /api/chat`（`stream:true`），逐行读取 `message.content`，无需依赖 Ollama 的 OpenAI 兼容层。
- 鉴权强制为 None、自动隐藏，无需填 Key。
- 点击 **「从 Ollama 拉取模型列表」** 按钮，会从 `{基址}/api/tags` 拉取本地已安装模型并填入模型下拉框（也可手填自定义模型名）。
- 基址默认 `http://localhost:11434`，可改（远程 Ollama、带端口等）。
- 参数走 Ollama 的 `options`（`temperature` / `num_predict`）。

> 若你更习惯 OpenAI 兼容路径，也可在「OpenAI 兼容」模式下把 Endpoint 填成 `http://localhost:11434/v1/chat/completions`。两种都支持，原生模式多了模型自动发现。

## 编译

需要 **.NET 9 SDK**（本插件目标框架 `net9.0-windows10.0.19041`，引用 `Flow.Launcher.Plugin` 5.3.2，已编译验证通过）。

```powershell
cd Flow.Launcher.Plugin.StreamingAI
dotnet build -c Release
```

产物在 `bin/Release/`（已设置 `AppendTargetFrameworkToOutputPath=false`，无框架子目录）。
> 注：输出里会附带 `Microsoft.Windows.SDK.NET.dll` / `WinRT.Runtime.dll` 等大体积 WinRT 依赖，这是 `net9.0-windows` 目标的正常现象，一并复制即可。

## 安装到 Flow Launcher

1. 把 `bin/Release/` 下的全部文件（含 `Flow.Launcher.Plugin.StreamingAI.dll`、`plugin.json`、`icon.png` 及依赖 DLL）复制到一个目录：
   ```
   %LOCALAPPDATA%\FlowLauncher\Plugins\6E8B4F2A-1C3D-4E5F-9A0B-2D4F6E8C1A3B\
   ```
   （目录名 = `plugin.json` 里的 `ID`）
2. 重启 Flow Launcher（`pm restart` 或任务栏退出重开）。
3. 在插件列表里启用 **Streaming AI Chat**。

> 也可把本工程直接放进 Flow Launcher 的开发者插件目录进行热重载调试。

## 使用

- 默认触发词 `ai`（可在 Flow Launcher 的**插件设置界面**里自定义 Action Keyword）。
- `ai 用一句话解释量子计算` → 回车 → 结果列表开始**流式**显示回答，完整文本同步出现在右侧预览面板。
- 回车（在回答结果上）= 复制完整回复；`Ctrl+C` 同样可复制。
- 右键结果 → 复制完整回复 / 清空对话 / 打开设置。
- 生成中可点「⏹ 停止生成」；「🔄 重新生成」用同一问题重问；「🗑 清空对话」重置上下文。
- 多轮：连续提问时，历史消息会作为上下文一并发给模型。


## 设置项

在 Flow Launcher 设置 → 插件 → Streaming AI Chat → 设置面板：

- **后端供应商**：`OpenAI 兼容` / `Ollama 原生`（切换会显示/隐藏对应配置项）
- **API Endpoint**：（OpenAI 兼容模式）chat/completions 完整地址
- **鉴权方式**：（OpenAI 兼容模式）Bearer / ApiKey / None（本地模型选 None）
- **API Key**：Bearer 或 ApiKey 模式使用；None 模式留空
- **API Key 请求头名称**：ApiKey 模式使用（Azure 填 `api-key`）
- **Ollama 基址 / 从 Ollama 拉取模型列表**：（Ollama 原生模式）填基址后一键拉取本地模型
- **模型名称**：下拉选择或手填，如 `gpt-4o-mini` / `deepseek-chat` / `llama3.2`
- **系统提示词 / 温度 / 最大 Token / 超时**

面板内「测试连接」会用 `ping` 发一次最小请求验证连通性；超时或错误会以弹窗提示具体原因。

### 失败与超时提示

插件带**首 token 超时**机制：在「超时时间（秒）」内若未收到任何数据（如 Endpoint 不可达、服务未启动、API Key 未填、网络不通），会终止请求并在结果列表显示为**明确的错误提示**，不再无限卡在「等待首个 token」。一旦收到首个片段即撤掉超时，长回答不会被误截断。

## 文件结构

```
StreamingAI/
├─ plugin.json                     # 插件清单（ID / 触发词 / 入口）
├─ Flow.Launcher.Plugin.StreamingAI.csproj
├─ Main.cs                         # 插件主逻辑：结果列表流式刷新、多轮、停止/清空、右键菜单
├─ Settings.cs                     # 设置模型 + 鉴权枚举
├─ ChatService.cs                  # OpenAI 兼容流式客户端（SSE 解析）
├─ SettingsControl.cs              # WPF 设置面板（纯代码构建）
└─ icon.png
```

## 实现要点

- 流式刷新：后台 `HttpClient`（`ResponseHeadersRead` + SSE）逐块读取 `delta.content`，写入共享 `StringBuilder` 并置位刷新标志；用 `System.Timers.Timer`（线程池 tick，不依赖任何 Dispatcher）以 ~120ms 节流，在回调里把 `API.ReQuery()` **派发回真正的 UI 线程**重绘结果列表。UI 线程的同步上下文在 Flow Launcher 于 UI 线程调用本插件的代码路径里捕获（`Query` / 各 `Action` / 右键菜单），存为 `_uiContext`（`SynchronizationContext`），定时器用 `_uiContext.Post(ReQuery)` 派发。**不要依赖 `Application.Current.Dispatcher`**——在插件上下文里它常为 `null`，会导致刷新静默失效、一直卡在「等待首个 token」直到手动触发 ReQuery（经典坑）。**也不要用 `DispatcherTimer`**——若插件 `Init` 在非 UI 线程被调用，它会绑定到不泵消息的线程而永不 tick。
- 多供应商：仅依赖 OpenAI 兼容协议；`AuthMode` 处理 Bearer / 自定义请求头 / 无鉴权三种情况，从而覆盖 Azure、Ollama（兼容层）等差异。
- Ollama 原生：`Provider.Ollama` 走 `/api/chat`，并从 `/api/tags` 发现本地模型。
- 超时与失败：`HttpClient.Timeout` 设为无限，统一由每次请求的「首 token 超时令牌」控制；超时会转为 `TimeoutException` 显式提示，避免被误判为用户取消而静默卡死。
- 设置持久化：使用 `API.LoadSettingJsonStorage<T>()` / `SaveSettingJsonStorage<T>()`。
