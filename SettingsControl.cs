using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Flow.Launcher.Plugin;

namespace Flow.Launcher.Plugin.StreamingAI
{
    /// <summary>
    /// 设置面板（纯代码构建的 WPF UserControl，避免 XAML 编译依赖）。
    /// </summary>
    public class SettingsControl : UserControl
    {
        private readonly PluginInitContext _context;
        private readonly Settings _settings;

        private ComboBox _provider, _authMode, _modelCombo;
        private TextBox _endpoint, _ollamaBase, _system, _maxTokens, _headerName, _timeout;
        private PasswordBox _pw;
        private Slider _temp;
        private TextBlock _tempVal;
        private StackPanel _openaiGroup, _ollamaGroup;

        public SettingsControl(Main plugin, PluginInitContext context, Settings settings)
        {
            _context = context;
            _settings = settings;
            BuildUI();
        }

        private void BuildUI()
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var panel = new StackPanel { Margin = new Thickness(16) };
            scroll.Content = panel;

            panel.Children.Add(Header("流式 AI 对话 · 设置"));

            panel.Children.Add(Label("后端供应商"));
            _provider = new ComboBox();
            _provider.Items.Add("OpenAI 兼容（可对接多数第三方 endpoint）");
            _provider.Items.Add("Ollama 原生（本地 /api/chat，支持模型自动发现）");
            _provider.SelectedIndex = (int)_settings.Provider;
            _provider.SelectionChanged += (s, e) => ApplyProviderVisibility();
            panel.Children.Add(_provider);

            // ---------- OpenAI 兼容组 ----------
            _openaiGroup = new StackPanel();
            _openaiGroup.Children.Add(Label("API Endpoint（OpenAI 兼容的 chat/completions 完整地址）"));
            _endpoint = Tb(_settings.Endpoint);
            _openaiGroup.Children.Add(_endpoint);

            _openaiGroup.Children.Add(Label("鉴权方式"));
            _authMode = new ComboBox();
            _authMode.Items.Add("Bearer（Authorization: Bearer，OpenAI / DeepSeek / OpenRouter 等）");
            _authMode.Items.Add("ApiKey（自定义请求头，如 Azure 的 api-key）");
            _authMode.Items.Add("None（本地无需密钥，如 Ollama / LM Studio）");
            _authMode.SelectedIndex = (int)_settings.AuthMode;
            _openaiGroup.Children.Add(_authMode);

            _openaiGroup.Children.Add(Label("API Key"));
            _pw = new PasswordBox { Password = _settings.ApiKey };
            _openaiGroup.Children.Add(_pw);

            _openaiGroup.Children.Add(Label("API Key 请求头名称（仅 ApiKey 模式，如 api-key）"));
            _headerName = Tb(_settings.ApiKeyHeaderName);
            _openaiGroup.Children.Add(_headerName);
            panel.Children.Add(_openaiGroup);

            // ---------- Ollama 组 ----------
            _ollamaGroup = new StackPanel { Visibility = Visibility.Collapsed };
            _ollamaGroup.Children.Add(Label("Ollama 基址（含端口，如 http://localhost:11434）"));
            _ollamaBase = Tb(_settings.OllamaBaseUrl);
            _ollamaGroup.Children.Add(_ollamaBase);

            var fetchRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 4) };
            var fetch = new Button { Content = "从 Ollama 拉取模型列表", Width = 180 };
            fetch.Click += FetchOllamaModels_Click;
            fetchRow.Children.Add(fetch);
            _ollamaGroup.Children.Add(fetchRow);
            _ollamaGroup.Children.Add(Label("提示：Ollama 原生接口无需密钥；下方选好模型即可。"));
            panel.Children.Add(_ollamaGroup);

            // ---------- 模型（两种模式共用，可下拉也可手填）----------
            panel.Children.Add(Label("模型名称（可下拉选择 / 也可手填，如 gpt-4o-mini / llama3.2 / qwen2.5）"));
            _modelCombo = new ComboBox
            {
                IsEditable = true,
                IsTextSearchEnabled = false,
                StaysOpenOnEdit = true,
                Text = _settings.Model
            };
            if (!string.IsNullOrWhiteSpace(_settings.Model))
                _modelCombo.Items.Add(_settings.Model);
            panel.Children.Add(_modelCombo);

            panel.Children.Add(Label("系统提示词（System Prompt）"));
            _system = Tb(_settings.SystemPrompt);
            _system.Height = 70;
            _system.TextWrapping = TextWrapping.Wrap;
            _system.AcceptsReturn = true;
            panel.Children.Add(_system);

            var tempRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            _temp = new Slider { Minimum = 0, Maximum = 2, Width = 240, Value = _settings.Temperature, TickFrequency = 0.1 };
            _tempVal = new TextBlock { Text = _settings.Temperature.ToString("F2"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            _temp.ValueChanged += (s, e) => _tempVal.Text = _temp.Value.ToString("F2");
            tempRow.Children.Add(Label("温度 Temperature:"));
            tempRow.Children.Add(_temp);
            tempRow.Children.Add(_tempVal);
            panel.Children.Add(tempRow);

            panel.Children.Add(Label("最大 Token 数（Max Tokens）"));
            _maxTokens = Tb(_settings.MaxTokens.ToString());
            panel.Children.Add(_maxTokens);

            panel.Children.Add(Label("超时时间（秒，同时控制「首 token 超时」与建连超时）"));
            _timeout = Tb(_settings.TimeoutSeconds.ToString());
            panel.Children.Add(_timeout);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            var save = new Button { Content = "保存", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
            save.Click += Save_Click;
            var test = new Button { Content = "测试连接", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
            test.Click += Test_Click;
            var reset = new Button { Content = "恢复默认", Width = 90 };
            reset.Click += Reset_Click;
            btnRow.Children.Add(save);
            btnRow.Children.Add(test);
            btnRow.Children.Add(reset);
            panel.Children.Add(btnRow);

            ApplyProviderVisibility();
            this.Content = scroll;
        }

        private void ApplyProviderVisibility()
        {
            bool ollama = _provider.SelectedIndex == 1;
            _ollamaGroup.Visibility = ollama ? Visibility.Visible : Visibility.Collapsed;
            _openaiGroup.Visibility = ollama ? Visibility.Collapsed : Visibility.Visible;
            if (ollama)
            {
                _authMode.SelectedIndex = (int)AuthMode.None;
                _authMode.IsEnabled = false;
            }
            else
            {
                _authMode.IsEnabled = true;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _settings.Provider = (Provider)_provider.SelectedIndex;
            _settings.OllamaBaseUrl = _ollamaBase.Text.Trim();
            _settings.Endpoint = _endpoint.Text.Trim();
            _settings.ApiKey = _pw.Password;
            _settings.ApiKeyHeaderName = _headerName.Text.Trim();
            _settings.Model = (_modelCombo.Text ?? "").Trim();
            _settings.SystemPrompt = _system.Text;
            _settings.AuthMode = (AuthMode)_authMode.SelectedIndex;
            double.TryParse(_tempVal.Text, out var t);
            _settings.Temperature = t;
            int.TryParse(_maxTokens.Text, out var mt);
            _settings.MaxTokens = mt;
            int.TryParse(_timeout.Text, out var to);
            _settings.TimeoutSeconds = to;

            _context.API.SaveSettingJsonStorage<Settings>();
            _context.API.ShowMsg("已保存", "流式 AI 对话设置已更新");
        }

        private async void FetchOllamaModels_Click(object sender, RoutedEventArgs e)
        {
            var baseUrl = (_ollamaBase.Text ?? "http://localhost:11434").Trim();
            var probe = new Settings { OllamaBaseUrl = baseUrl };
            try
            {
                var list = await Task.Run(() => new ChatService().ListOllamaModelsAsync(probe)).ConfigureAwait(false);
                Dispatcher.Invoke(() =>
                {
                    _modelCombo.Items.Clear();
                    foreach (var m in list) _modelCombo.Items.Add(m);
                    if (list.Count > 0)
                    {
                        _modelCombo.Text = list[0];
                        _context.API.ShowMsg("已拉取模型", $"共 {list.Count} 个本地模型，已填入模型框");
                    }
                    else
                    {
                        _context.API.ShowMsg("未发现模型", "Ollama 本地尚无模型，请先执行 `ollama pull <模型名>`");
                    }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => _context.API.ShowMsgError("拉取失败", ex.Message));
            }
        }

        private async void Test_Click(object sender, RoutedEventArgs e)
        {
            Save_Click(sender, e); // 先持久化最新值
            var cfg = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(_settings));
            cfg.MaxTokens = 8;
            var svc = new ChatService();
            string first = "";
            try
            {
                await Task.Run(async () =>
                {
                    await svc.StreamAsync(new List<ChatMessage>(), "ping", d =>
                    {
                        if (first == "") first = d;
                    }, default, cfg);
                }).ConfigureAwait(false);
                var msg = first.Length > 50 ? first.Substring(0, 50) + "…" : first;
                Dispatcher.Invoke(() => _context.API.ShowMsg("连接成功", "已收到模型回复：" + msg));
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => _context.API.ShowMsgError("连接失败", ex.Message));
            }
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            var d = new Settings();
            _provider.SelectedIndex = (int)d.Provider;
            _ollamaBase.Text = d.OllamaBaseUrl;
            _endpoint.Text = d.Endpoint;
            _pw.Password = d.ApiKey;
            _headerName.Text = d.ApiKeyHeaderName;
            _modelCombo.Items.Clear();
            _modelCombo.Text = d.Model;
            _system.Text = d.SystemPrompt;
            _authMode.SelectedIndex = (int)d.AuthMode;
            _temp.Value = d.Temperature;
            _tempVal.Text = d.Temperature.ToString("F2");
            _maxTokens.Text = d.MaxTokens.ToString();
            _timeout.Text = d.TimeoutSeconds.ToString();
            ApplyProviderVisibility();
        }

        // ----- 小工具 -----
        private static TextBlock Header(string t) =>
            new TextBlock { Text = t, FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) };
        private static TextBlock Label(string t) =>
            new TextBlock { Text = t, Margin = new Thickness(0, 10, 0, 4) };
        private static TextBox Tb(string text) =>
            new TextBox { Text = text ?? "", Margin = new Thickness(0, 0, 0, 4) };
    }
}
