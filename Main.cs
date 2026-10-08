using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Controls;
using Flow.Launcher.Plugin;

namespace Flow.Launcher.Plugin.StreamingAI
{
    /// <summary>对话阶段</summary>
    public enum Phase { Idle, Streaming, Done }

    public class Main : IPlugin, IContextMenu, ISettingProvider, IDisposable
    {
        private PluginInitContext _context;
        private Settings _settings;
        private readonly ChatService _chat = new ChatService();
        private readonly object _lock = new object();

        // ----- 共享状态（由 _lock 保护） -----
        private List<ChatMessage> _history = new List<ChatMessage>();
        private readonly StringBuilder _currentAnswer = new StringBuilder();
        private string _currentPrompt = "";
        private Phase _phase = Phase.Idle;
        private string _lastError = "";
        private string _lastSearch = "";
        private CancellationTokenSource _cts;
        private bool _needRefresh;

        private System.Timers.Timer _refreshTimer;
        private System.Threading.SynchronizationContext _uiContext;

        // ============ 初始化 ============
        public void Init(PluginInitContext context)
        {
            _context = context;
            _settings = context.API.LoadSettingJsonStorage<Settings>();

            // 节流刷新：后台流式的 token 只置位 _needRefresh。
            // 用 System.Timers.Timer（线程池线程 tick，不依赖任何 Dispatcher）。
            // 关键：把 ReQuery 派发回真正的 UI 线程。UI 线程的同步上下文必须在
            // Flow Launcher 于 UI 线程调用本插件的代码路径里捕获（Query / Action 等），
            // 绝不能依赖 Application.Current（插件上下文里常为 null，会导致刷新静默失效）。
            _refreshTimer = new System.Timers.Timer(120) { AutoReset = true };
            _refreshTimer.Elapsed += (s, e) =>
            {
                bool need;
                lock (_lock) { need = _needRefresh; _needRefresh = false; }
                if (!need) return;
                var ctx = _uiContext;
                if (ctx == null) return;
                try
                {
                    ctx.Post(_ =>
                    {
                        try { _context.API.ReQuery(); }
                        catch { /* 重绘失败不中断流式，下一拍会重试 */ }
                    }, null);
                }
                catch { /* 忽略调度异常 */ }
            };
            _refreshTimer.Start();
        }

        // ============ 查询入口 ============
        public List<Result> Query(Query query)
        {
            CaptureUiContext();
            var search = (query.Search ?? "").Trim();
            lock (_lock) { _lastSearch = search; }

            if (string.IsNullOrEmpty(search))
                return BuildEmptyResults();

            lock (_lock)
            {
                if (_phase == Phase.Streaming)
                {
                    if (search == _currentPrompt) return BuildStreamingResults();
                    return BuildBusyResults();
                }
                if (search == _currentPrompt && _currentAnswer.Length > 0)
                    return BuildDoneResults();
            }
            return BuildSendResults(search);
        }

        // ============ 结果构建 ============

        private List<Result> BuildEmptyResults()
        {
            var results = new List<Result>
            {
                new Result
                {
                    Title = "💬 AI 流式对话",
                    SubTitle = "输入问题后按回车发送，例如：ai 用一句话解释量子计算",
                    IcoPath = "icon.png",
                    Score = Result.MaxScore,
                    Action = _ => false
                },
                new Result
                {
                    Title = "⚙ 打开插件设置",
                    SubTitle = $"当前模型：{_settings.Model}　Endpoint：{SafeHost(_settings.Endpoint)}",
                    IcoPath = "icon.png",
                    Action = _ =>
                    {
                        _context.API.OpenSettingDialog();
                        return false;
                    }
                }
            };

            if (_history.Count > 0)
            {
                results.Add(new Result
                {
                    Title = "🗑 清空对话历史",
                    SubTitle = $"当前已记录 {_history.Count} 条消息",
                    IcoPath = "icon.png",
                    Action = _ => { ResetConversation(); _context.API.ReQuery(); return false; }
                });
            }
            return results;
        }

        private List<Result> BuildSendResults(string search)
        {
            var preview = search.Length > 90 ? search.Substring(0, 90) + "…" : search;
            return new List<Result>
            {
                new Result
                {
                    Title = "💬 向 AI 提问",
                    SubTitle = preview,
                    IcoPath = "icon.png",
                    Score = Result.MaxScore,
                    Action = _ =>
                    {
                        CaptureUiContext();
                        StartStreaming(search);
                        _context.API.ReQuery();
                        return false;
                    }
                },
                new Result
                {
                    Title = "⚙ 打开插件设置",
                    SubTitle = "配置 Endpoint / API Key / 模型等",
                    IcoPath = "icon.png",
                    Action = _ => { _context.API.OpenSettingDialog(); return false; }
                }
            };
        }

        private List<Result> BuildStreamingResults()
        {
            var answer = SnapshotAnswer();
            var streaming = _phase == Phase.Streaming;
            var snippet = answer.Length > 200 ? answer.Substring(answer.Length - 200) : answer;
            var title = streaming ? "🤖 正在回复…" : "✅ AI 回复";
            var sub = string.IsNullOrEmpty(answer)
                ? "（等待首个 token…）"
                : snippet + (streaming ? " ▌" : "");
            if (!string.IsNullOrEmpty(_lastError))
                sub = "⚠ 出错：" + _lastError;

            var previewText = (string.IsNullOrEmpty(answer) ? "（等待首个 token…）" : answer)
                              + (streaming ? "\n\n—— 生成中 ——" : "");

            var results = new List<Result>
            {
                new Result
                {
                    Title = title,
                    SubTitle = sub,
                    IcoPath = "icon.png",
                    Score = Result.MaxScore,
                    CopyText = answer,
                    Preview = new Result.PreviewInfo { Description = previewText },
                    SubTitleToolTip = "回车复制完整回复；右键可复制 / 停止 / 清空对话",
                    Action = _ => { CopyAnswer(); return false; }
                }
            };
            if (streaming) results.Add(StopResult());
            results.Add(ClearResult());
            return results;
        }

        private List<Result> BuildBusyResults()
        {
            return new List<Result>
            {
                new Result
                {
                    Title = "⏳ 正在生成上一条回复",
                    SubTitle = "请等待完成，或点击「停止生成」后再提问",
                    IcoPath = "icon.png",
                    Score = Result.MaxScore,
                    Action = _ => false
                },
                StopResult(),
                ClearResult()
            };
        }

        private List<Result> BuildDoneResults()
        {
            var answer = SnapshotAnswer();
            var firstLine = FirstLine(answer);
            var results = new List<Result>
            {
                new Result
                {
                    Title = "✅ " + (firstLine.Length > 70 ? firstLine.Substring(0, 70) + "…" : firstLine),
                    SubTitle = $"共 {answer.Length} 字 · 回车复制完整回复",
                    IcoPath = "icon.png",
                    Score = Result.MaxScore,
                    CopyText = answer,
                    Preview = new Result.PreviewInfo { Description = answer },
                    SubTitleToolTip = "回车复制；右键可复制 / 重新生成 / 清空",
                    Action = _ => { CopyAnswer(); return false; }
                },
                new Result
                {
                    Title = "🔄 重新生成",
                    SubTitle = "使用相同问题重新请求（会移除上一条回答）",
                    IcoPath = "icon.png",
                    Action = _ => { Regenerate(); return false; }
                },
                ClearResult()
            };
            return results;
        }

        private Result StopResult()
        {
            return new Result
            {
                Title = "⏹ 停止生成",
                SubTitle = "中止当前流式回复",
                IcoPath = "icon.png",
                Action = _ =>
                {
                    CaptureUiContext();
                    lock (_lock) { _cts?.Cancel(); _needRefresh = true; }
                    _context.API.ReQuery();
                    return false;
                }
            };
        }

        private Result ClearResult()
        {
            return new Result
            {
                Title = "🗑 清空对话",
                SubTitle = "清除对话历史与当前回复",
                IcoPath = "icon.png",
                Action = _ =>
                {
                    ResetConversation();
                    _context.API.ReQuery();
                    return false;
                }
            };
        }

        // ============ 流式控制 ============

        private void StartStreaming(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return;

            CancellationTokenSource cts;
            lock (_lock)
            {
                _cts?.Cancel();
                _cts?.Dispose();
                cts = new CancellationTokenSource();
                _cts = cts;
            }
            var token = cts.Token;
            var history = SnapshotHistory();
            var cfg = CloneSettings();

            lock (_lock)
            {
                _currentPrompt = prompt;
                _currentAnswer.Clear();
                _phase = Phase.Streaming;
                _lastError = "";
                _needRefresh = true;
            }

            Task.Run(async () =>
            {
                try
                {
                    await _chat.StreamAsync(history, prompt,
                        delta =>
                        {
                            lock (_lock) { _currentAnswer.Append(delta); _needRefresh = true; }
                        }, token, cfg).ConfigureAwait(false);

                    lock (_lock)
                    {
                        _history.Add(new ChatMessage { Role = "user", Content = prompt });
                        _history.Add(new ChatMessage { Role = "assistant", Content = _currentAnswer.ToString() });
                        _phase = Phase.Done;
                        _needRefresh = true;
                    }
                }
                catch (OperationCanceledException)
                {
                    lock (_lock)
                    {
                        if (_currentAnswer.Length > 0)
                        {
                            _history.Add(new ChatMessage { Role = "user", Content = prompt });
                            _history.Add(new ChatMessage { Role = "assistant", Content = _currentAnswer.ToString() });
                        }
                        _phase = Phase.Done;
                        _needRefresh = true;
                    }
                }
                catch (Exception ex)
                {
                    lock (_lock)
                    {
                        _lastError = ex.Message;
                        _phase = Phase.Done;
                        _needRefresh = true;
                    }
                }
            }, token);
        }

        private void Regenerate()
        {
            string prompt;
            lock (_lock)
            {
                prompt = _currentPrompt;
                // 移除最后一组 Q&A，避免重复
                if (_history.Count >= 2)
                {
                    _history.RemoveAt(_history.Count - 1);
                    _history.RemoveAt(_history.Count - 1);
                }
            }
            if (string.IsNullOrEmpty(prompt)) return;
            StartStreaming(prompt);
            _context.API.ReQuery();
        }

        private void ResetConversation()
        {
            lock (_lock)
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
                _history.Clear();
                _currentAnswer.Clear();
                _currentPrompt = "";
                _phase = Phase.Idle;
                _lastError = "";
                _needRefresh = true;
            }
        }

        private void CopyAnswer()
        {
            var answer = SnapshotAnswer();
            if (!string.IsNullOrEmpty(answer))
                _context.API.CopyToClipboard(answer);
        }

        // ============ 右键菜单 ============
        public List<Result> LoadContextMenus(Result selectedResult)
        {
            var answer = SnapshotAnswer();
            var menus = new List<Result>();
            if (!string.IsNullOrEmpty(answer))
            {
                menus.Add(new Result
                {
                    Title = "📋 复制完整回复",
                    IcoPath = "icon.png",
                    Action = _ => { _context.API.CopyToClipboard(answer); return false; }
                });
            }
            menus.Add(new Result
            {
                Title = "🗑 清空对话",
                IcoPath = "icon.png",
                Action = _ => { ResetConversation(); _context.API.ReQuery(); return false; }
            });
            menus.Add(new Result
            {
                Title = "⚙ 打开设置",
                IcoPath = "icon.png",
                Action = _ => { _context.API.OpenSettingDialog(); return false; }
            });
            return menus;
        }

        // ============ 设置面板 ============
        public Control CreateSettingPanel()
        {
            return new SettingsControl(this, _context, _settings);
        }

        // ============ 辅助 ============
        /// <summary>
        /// 捕获「真正的 UI 线程」同步上下文，供后台定时器把 ReQuery 安全地派发回 UI 线程。
        /// 必须在 Flow Launcher 于 UI 线程调用本插件的代码路径里调用（Query / 各 Action / 右键菜单等）。
        /// 优先取当前线程的 DispatcherSynchronizationContext（WPF 下即绑定 UI 线程）；
        /// 兜底取 Flow Launcher 主线程的 Dispatcher。
        /// </summary>
        private void CaptureUiContext()
        {
            if (_uiContext != null) return;
            var ctx = System.Threading.SynchronizationContext.Current;
            if (ctx is System.Windows.Threading.DispatcherSynchronizationContext)
            {
                _uiContext = ctx;
                return;
            }
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher != null)
                _uiContext = new System.Windows.Threading.DispatcherSynchronizationContext(app.Dispatcher);
        }

        private string SnapshotAnswer() { lock (_lock) return _currentAnswer.ToString(); }
        private List<ChatMessage> SnapshotHistory() { lock (_lock) return new List<ChatMessage>(_history); }

        private Settings CloneSettings()
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_settings);
            return System.Text.Json.JsonSerializer.Deserialize<Settings>(json);
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var idx = text.IndexOf('\n');
            return (idx > 0 ? text.Substring(0, idx) : text).Trim();
        }

        private static string SafeHost(string endpoint)
        {
            try { return new Uri(endpoint).Host; }
            catch { return endpoint; }
        }

        public void Dispose()
        {
            try { _refreshTimer?.Stop(); } catch { }
            lock (_lock) { _cts?.Cancel(); _cts?.Dispose(); }
        }
    }
}
