using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WoWTranslateControl.Core;
using WoWTranslateControl.Models;

namespace WoWTranslateControl;

/// <summary>流量表格行视图模型。</summary>
public sealed class TrafficRow
{
    public string TimeText { get; init; } = "";
    public string KindName { get; init; } = "";
    public string RuleId { get; init; } = "";
    public string RuleName { get; init; } = "";
    public string Content { get; init; } = "";
    public int ElapsedMs { get; init; }
    /// <summary>聊天频道名（GUILD/SAY…）；null = 无频道标签的系统播报。</summary>
    public string? Channel { get; init; }
}

public partial class MainWindow : Window
{
    private AppConfig _cfg = new();
    private ProxyServer? _proxy;
    private LlamaServerManager? _llama;
    private Core.Glossary.GlossaryStore? _glossary;
    private System.Windows.Forms.NotifyIcon? _tray;

    private readonly ObservableCollection<TrafficRow> _allRows = new();
    private readonly ObservableCollection<TrafficRow> _view = new();

    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _clockTimer;

    // 代理事件在后台线程触发，先入队，再由 UI 定时器批量消费，
    // 避免战斗日志突发（每秒数十条）时逐条刷新把界面卡死。
    private readonly Queue<TrafficEntry> _pending = new();
    private readonly object _pendingSync = new();

    private readonly Queue<string> _pendingLog = new();
    private readonly object _logSync = new();
    private readonly Queue<string> _pendingOutput = new();
    private readonly object _outputSync = new();

    private string _filterKind = "全部";

    public MainWindow()
    {
        InitializeComponent();

        // 标题版本号取自程序集版本（随 csproj <Version> 变化），避免 XAML 硬编码漂移
        Title = $"WoWTranslate 控制台 v{System.Reflection.Assembly.GetEntryAssembly()!.GetName().Version!.ToString(3)}" +
                " — Direct DLL 翻译 · ijnokmsc";

        // 窗口/任务栏图标：从 exe 旁的 assets\app.ico 加载（XAML 相对 URI 在编译后的
        // BAML 里不指向 exe 目录，会抛异常，故用代码加载）
        try
        {
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri(Path.Combine(AppContext.BaseDirectory, "assets", "app.ico")));
        }
        catch { /* 图标缺失不影响功能 */ }

        _cfg = AppConfig.Load();
        TrafficGrid.ItemsSource = _view;

        LoadConfigToUi();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += (_, _) => FlushPending();
        _uiTimer.Start();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        InitTray();
        RefreshDllStatus();
        // 初始定位到左栏顶部（初始化期间控件变更可能触发 BringIntoView 滚动）
        LeftScroll.ScrollToTop();

        var orphans = LlamaServerManager.KillOrphans();
        if (orphans > 0)
            Log($"已清理上次会话残留的 llama-server 孤儿进程 × {orphans}");
        _llama = new LlamaServerManager(_cfg);
        _llama.OnStateChanged += OnLlamaStateChanged;
        _llama.OnLog += Log;
        _llama.OnOutput += line =>
        {
            lock (_outputSync)
            {
                _pendingOutput.Enqueue(line);
                while (_pendingOutput.Count > 200) _pendingOutput.Dequeue();
            }
        };

        Log($"已加载配置：llama 目录 {_cfg.LlamaDir}，模型 {_cfg.ModelFile}，" +
            $"监听 {_cfg.ListenPort} → 上游 {_cfg.UpstreamPort}");
        Log(App.DebugMode
            ? "调试模式：已由 -Debug 启动参数开启全部日志（DLL 日志 / proxy_traffic.log / llama_mem.csv）"
            : "调试模式：未开启（日志一概不写；需要排错时加 -Debug 启动参数）");

        // 便携化：运行目录下生成 llama.cpp 目录（新用户把下载/解压的文件放这里即可）
        try { Directory.CreateDirectory(_cfg.LlamaDir); } catch { }

        // DLL 版本自愈：游戏目录 DLL 与本地资产 MD5 不一致时自动重新部署
        foreach (var l in Core.DllSwitcher.EnsureUpToDate(
                     TxtGameDir.Text.Trim(),
                     System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "direct-dll")))
            Log(l);
        if (ValidateGameDir(true)) AutoDeployTrackB();

        if (_cfg.AutoStartLlama || _cfg.ProxyAutoStart)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // 首次使用（还没装 llama.cpp）不自动启动 llama-server——
                // 空跑只会报"未找到可执行文件"，应引导走向导
                if (_cfg.AutoStartLlama)
                {
                    if (File.Exists(Path.Combine(_cfg.LlamaDir, "llama-server.exe")))
                        StartLlama();
                    else
                        Log("未检测到 llama.cpp（缺 llama-server.exe），已跳过 llama-server 自动启动。" +
                            "请打开「环境检测与下载向导」完成下载，或把文件放入 llama 目录后手动启动。");
                }
                if (_cfg.ProxyAutoStart) StartProxy();
                // 启动过程禁用/启用按钮会移动键盘焦点，WPF 顺链 BringIntoView
                // 把左栏滚到底部（用户误以为"过滤规则盖住了其他卡片"）——
                // 全部服务就绪后滚回顶部；仅未设置游戏目录时才聚焦该输入框引导填写
                LeftScroll.ScrollToTop();
                if (string.IsNullOrWhiteSpace(TxtGameDir.Text))
                    TxtGameDir.Focus();
            }), DispatcherPriority.Background);
    }

    private bool _forceExit;
    private int _memSampleTick;

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 关闭 → 缩托盘（托盘菜单的「退出」置 _forceExit 后才真正关闭）
        if (!_forceExit && _cfg.MinimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _tray?.Dispose();
        _tray = null;
        _cfg.Save();
        _uiTimer.Stop();
        _clockTimer.Stop();

        // 关掉本程序时一并回收子进程与监听端口，避免残留占用 8080/8081
        try { _proxy?.Dispose(); } catch { }
        try { _llama?.Dispose(); } catch { }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        // 最小化 → 缩托盘
        if (WindowState == WindowState.Minimized && _cfg.MinimizeToTray)
            HideToTray();
    }

    // ==================== 配置读写 ====================

    private void LoadConfigToUi()
    {
        TxtLlamaDir.Text = _cfg.LlamaDir;
        TxtModel.Text = _cfg.ModelFile;
        TxtThreads.Text = _cfg.Threads.ToString();
        TxtContext.Text = _cfg.ContextSize.ToString();
        TxtUpstreamPort.Text = _cfg.UpstreamPort.ToString();
        TxtListenPort.Text = _cfg.ListenPort.ToString();
        TxtMaxTokens.Text = _cfg.MaxTokensCeil.ToString();

        ChkAutoStart.IsChecked = _cfg.AutoStartLlama && _cfg.ProxyAutoStart;
        ChkOnlyChannel.IsChecked = _cfg.TrafficOnlyChannel;
        ChkCache.IsChecked = _cfg.CacheEnabled;

        // 频道过滤
        ChkChanEnabled.IsChecked = _cfg.ChannelFilterEnabled;
        ChkChanSay.IsChecked = _cfg.ChannelSay;
        ChkChanYell.IsChecked = _cfg.ChannelYell;
        ChkChanWhisper.IsChecked = _cfg.ChannelWhisper;
        ChkChanParty.IsChecked = _cfg.ChannelParty;
        ChkChanGuild.IsChecked = _cfg.ChannelGuild;
        ChkChanRaid.IsChecked = _cfg.ChannelRaid;
        ChkChanBg.IsChecked = _cfg.ChannelBattleground;
        ChkChanWorld.IsChecked = _cfg.ChannelWorld;
        ChkChanUntagged.IsChecked = _cfg.ChannelUntagged;

        // 翻译服务
        foreach (System.Windows.Controls.ComboBoxItem item in CmbProvider.Items)
            if ((string)item.Tag == _cfg.ProviderMode) { CmbProvider.SelectedItem = item; break; }
        TxtOpenAiEndpoint.Text = _cfg.OpenAiEndpoint;
        TxtOpenAiKey.Text = _cfg.OpenAiApiKey;
        TxtOpenAiModel.Text = _cfg.OpenAiModel;
        TxtGoogleSl.Text = _cfg.GoogleSl;
        TxtGoogleTl.Text = _cfg.GoogleTl;
        TxtSystemPrompt.Text = _cfg.SystemPrompt;
        UpdateProviderFields();

        // 插件配置
        TxtGameDir.Text = _cfg.GameDir;

        // 托盘与 DLL 轨道
        ChkTray.IsChecked = _cfg.MinimizeToTray;
        // 日志开关由 -Debug 启动参数统一控制，复选框只作状态指示（不可勾选）
        ChkFileLog.IsChecked = App.DebugMode;
        ChkDllLog.IsChecked = App.DebugMode;
        ChkFileLog.IsEnabled = false;
        ChkDllLog.IsEnabled = false;
        // Track B 显示模式（v16 全自治驱动）
        foreach (System.Windows.Controls.ComboBoxItem item in CmbDirectDisplay.Items)
            if ((string)item.Tag == _cfg.DirectDisplayMode) { CmbDirectDisplay.SelectedItem = item; break; }
        TxtDirectPrefix.Text = _cfg.DirectDisplayPrefix;
        foreach (System.Windows.Controls.ComboBoxItem item in CmbDirectOutgoing.Items)
            if ((string)item.Tag == _cfg.DirectOutgoingMode) { CmbDirectOutgoing.SelectedItem = item; break; }

        // llama 自动重启（v41）
        TxtLlamaMemLimit.Text = _cfg.LlamaMemLimitMB.ToString();
        ChkLlamaAutoRestart.IsChecked = _cfg.LlamaAutoRestart;

        // v41 发送消息过滤器：规则列表 + 外发频道开关（勾选 = 翻译）
        TxtOutgoingFilter.Text = _cfg.OutgoingFilterRules ?? "";
        var outOff = new HashSet<string>((_cfg.DirectOutgoingOffChannels ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
        foreach (var (ch, box) in OutChannels())
            box.IsChecked = !outOff.Contains(ch);

        _suppressSlider = true;

        ProxyPortText.Text = $" :{_cfg.ListenPort} → :{_cfg.UpstreamPort}";
    }

    private bool ReadConfigFromUi()
    {
        bool ok = true;

        _cfg.LlamaDir = TxtLlamaDir.Text.Trim();
        _cfg.ModelFile = TxtModel.Text.Trim();

        if (!TryParseInt(TxtThreads.Text, 1, 128, v => _cfg.Threads = v, "线程数")) ok = false;
        if (!TryParseInt(TxtContext.Text, 256, 131072, v => _cfg.ContextSize = v, "上下文")) ok = false;
        if (!TryParseInt(TxtUpstreamPort.Text, 1, 65535, v => _cfg.UpstreamPort = v, "上游端口")) ok = false;
        if (!TryParseInt(TxtListenPort.Text, 1, 65535, v => _cfg.ListenPort = v, "监听端口")) ok = false;
        if (!TryParseInt(TxtMaxTokens.Text, 16, 8192, v => _cfg.MaxTokensCeil = v, "token 上限")) ok = false;

        return ok;
    }

    private bool TryParseInt(string text, int min, int max, Action<int> apply, string label)
    {
        if (int.TryParse(text.Trim(), out var v) && v >= min && v <= max)
        {
            apply(v);
            return true;
        }
        Log($"参数「{label}」无效：{text}（有效范围 {min}～{max}），已保留原值");
        return false;
    }

    private void BtnSaveCfg_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadConfigFromUi()) return;
        _cfg.Save();
        ProxyPortText.Text = $" :{_cfg.ListenPort} → :{_cfg.UpstreamPort}";
        Log("配置已保存到 settings.json（端口与参数改动需重启对应服务生效）");
        MessageBox.Show(this, "配置已保存。\n\n端口、线程数等改动需重启 llama-server / 过滤代理后生效。",
            "已保存", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnResetPrompt_Click(object sender, RoutedEventArgs e)
    {
        TxtSystemPrompt.Text = AppConfig.DefaultSystemPrompt;
        Log("已恢复默认系统提示词，点「保存配置」后生效。");
    }

    // ==================== 服务控制 ====================

    private void BtnStartLlama_Click(object sender, RoutedEventArgs e) => StartLlama();
    private void BtnStopLlama_Click(object sender, RoutedEventArgs e) => StopLlama();
    private void BtnStartProxy_Click(object sender, RoutedEventArgs e) => StartProxy();
    private void BtnStopProxy_Click(object sender, RoutedEventArgs e) => StopProxy();

    private void BtnStartAll_Click(object sender, RoutedEventArgs e)
    {
        StartLlama();
        // 代理不依赖模型就绪（未命中过滤时会等上游），可立即启动
        StartProxy();
    }

    private void StartLlama()
    {
        ReadConfigFromUi();
        if (_llama == null) return;
        if (_llama.Start())
        {
            BtnStartLlama.IsEnabled = false;
            BtnStopLlama.IsEnabled = true;
            KeepFocusStable();
        }
    }

    private void StopLlama()
    {
        _llama?.Stop();
        BtnStartLlama.IsEnabled = true;
        BtnStopLlama.IsEnabled = false;
        KeepFocusStable();
    }

    private void StartProxy()
    {
        ReadConfigFromUi();

        // 端口占用自动回退：从配置端口向上探测（最多 20 个，避开 llama 上游端口）。
        // 新端口写回 settings 并同步游戏目录 WoWTranslateDirect.json——DLL 端点跟着变。
        var originalPort = _cfg.ListenPort;
        if (LlamaServerManager.IsPortListening(originalPort))
        {
            var port = originalPort;
            for (var i = 0; i < 20 && LlamaServerManager.IsPortListening(port); i++)
            {
                port++;
                if (port == _cfg.UpstreamPort) port++;   // 避开 llama-server
            }
            if (LlamaServerManager.IsPortListening(port))
            {
                var msg = "端口 " + originalPort + " 起连续 20 个端口均被占用，无法启动过滤代理。" +
                          "请检查是否有其他程序占用，或手动在设置里指定端口。";
                Log(msg);
                MessageBox.Show(this, msg, "端口冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _cfg.ListenPort = port;
            _cfg.Save();
            TxtListenPort.Text = port.ToString();
            Log("端口 " + originalPort + " 已被占用（可能是其他代理/旧实例），已自动切换到 " + port + " 并保存。");
        }

        _glossary ??= Core.Glossary.GlossaryStore.Load(
            System.IO.Path.Combine(AppContext.BaseDirectory, "glossary.json"));
        _proxy?.Dispose();
        _proxy = new ProxyServer(_cfg, _glossary);
        _proxy.OnTraffic += entry =>
        {
            lock (_pendingSync)
            {
                _pending.Enqueue(entry);
                // 极端突发时丢弃最旧的，保证界面不堆积
                while (_pending.Count > 3000) _pending.Dequeue();
            }
        };
        _proxy.OnLog += Log;

        try
        {
            _proxy.Start();
            BtnStartProxy.IsEnabled = false;
            BtnStopProxy.IsEnabled = true;
            KeepFocusStable();
            UpdateProxyDot(true);
            ProxyPortText.Text = $" :{_cfg.ListenPort} → :{_cfg.UpstreamPort}";
            // 端口回退后同步游戏目录 WoWTranslateDirect.json（DLL 端点端口 = 新监听端口）
            if (originalPort != _cfg.ListenPort)
            {
                try
                {
                    var gd = TxtGameDir.Text.Trim();
                    if (_lastDllStatus?.Current == Core.DllSwitcher.TrackDirect &&
                        Directory.Exists(gd) && File.Exists(Path.Combine(gd, "Wow.exe")))
                    {
                        Core.DllSwitcher.WriteDirectConfig(gd, _cfg.ListenPort,
                            _cfg.DirectDisplayMode, _cfg.DirectDisplayPrefix, _cfg.DirectOutgoingMode,
                            dllLog: App.DebugMode,
                            outFilter: _cfg.OutgoingFilterRules,
                            outOff: _cfg.DirectOutgoingOffChannels);
                        Log("已同步游戏目录 WoWTranslateDirect.json——游戏内 /reload 或重启游戏生效");
                    }
                }
                catch (Exception ex) { Log("同步游戏目录配置失败：" + ex.Message); }
            }
        }
        catch (Exception ex)
        {
            Log($"代理启动失败：{ex.Message}");
            MessageBox.Show(this, $"代理启动失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopProxy()
    {
        try { _proxy?.Stop(); } catch { }
        BtnStartProxy.IsEnabled = true;
        BtnStopProxy.IsEnabled = false;
        KeepFocusStable();
        UpdateProxyDot(false);
    }

    private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var on = ChkAutoStart.IsChecked == true;
        _cfg.AutoStartLlama = on;
        _cfg.ProxyAutoStart = on;
        _cfg.Save();
    }

    // ==================== 规则开关 ====================


    private void Channel_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _cfg.ChannelFilterEnabled = ChkChanEnabled.IsChecked == true;
        _cfg.ChannelSay = ChkChanSay.IsChecked == true;
        _cfg.ChannelYell = ChkChanYell.IsChecked == true;
        _cfg.ChannelWhisper = ChkChanWhisper.IsChecked == true;
        _cfg.ChannelParty = ChkChanParty.IsChecked == true;
        _cfg.ChannelGuild = ChkChanGuild.IsChecked == true;
        _cfg.ChannelRaid = ChkChanRaid.IsChecked == true;
        _cfg.ChannelBattleground = ChkChanBg.IsChecked == true;
        _cfg.ChannelWorld = ChkChanWorld.IsChecked == true;
        _cfg.ChannelUntagged = ChkChanUntagged.IsChecked == true;
        _cfg.Save();
    }

    // ==================== 翻译服务（Provider） ====================

    private void CmbProvider_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        SaveProviderFromUi();
        UpdateProviderFields();
        _cfg.Save();
    }

    private void SaveProviderFromUi()
    {
        _cfg.ProviderMode = CmbProvider.SelectedItem is System.Windows.Controls.ComboBoxItem item
            ? (string)item.Tag : "local";
        _cfg.OpenAiEndpoint = TxtOpenAiEndpoint.Text.Trim();
        _cfg.OpenAiApiKey = TxtOpenAiKey.Text.Trim();
        _cfg.OpenAiModel = TxtOpenAiModel.Text.Trim();
        _cfg.GoogleSl = string.IsNullOrWhiteSpace(TxtGoogleSl.Text) ? "auto" : TxtGoogleSl.Text.Trim();
        _cfg.GoogleTl = string.IsNullOrWhiteSpace(TxtGoogleTl.Text) ? "zh-CN" : TxtGoogleTl.Text.Trim();
        // 系统提示词：保留用户输入的换行与缩进，仅去掉首尾空白；空则回退默认
        var prompt = TxtSystemPrompt.Text.Trim();
        _cfg.SystemPrompt = prompt.Length > 0 ? prompt : AppConfig.DefaultSystemPrompt;
    }

    private void UpdateProviderFields()
    {
        var showOpenAi = _cfg.ProviderMode is "openai" or "auto";
        var showGoogle = _cfg.ProviderMode is "google" or "auto";
        LblOpenAi.Visibility = showOpenAi ? Visibility.Visible : Visibility.Collapsed;
        TxtOpenAiEndpoint.Visibility = showOpenAi ? Visibility.Visible : Visibility.Collapsed;
        TxtOpenAiKey.Visibility = showOpenAi ? Visibility.Visible : Visibility.Collapsed;
        TxtOpenAiModel.Visibility = showOpenAi ? Visibility.Visible : Visibility.Collapsed;
        LblGoogle.Visibility = showGoogle ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void BtnTestProvider_Click(object sender, RoutedEventArgs e)
    {
        SaveProviderFromUi();
        _cfg.Save();
        BtnTestProvider.IsEnabled = false;
        Log("正在测试翻译服务…");
        try
        {
            var manager = new Core.Providers.ProviderManager(_cfg);
            var ctx = new Core.Pipeline.TranslationContext("Hello, world!", Array.Empty<byte>(), _cfg);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var reply = await manager.TranslateAsync(ctx, CancellationToken.None);
            sw.Stop();
            Log(reply.Ok
                ? $"✅ 翻译服务测试通过（{manager.PrimaryId}，{sw.ElapsedMilliseconds}ms）：{reply.Translated}"
                : $"❌ 翻译服务测试失败：{reply.Error}");
            Log($"翻译服务测试：{(reply.Ok ? "成功" : "失败")} {reply.Error}");
        }
        finally
        {
            BtnTestProvider.IsEnabled = true;
        }
    }

    // ==================== 插件一键配置（需求 8） ====================

    private void BtnBrowseGameDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择游戏客户端根目录（含 Wow.exe）",
        };
        if (dlg.ShowDialog(this) == true)
            TxtGameDir.Text = dlg.FolderName;
    }

    private void BtnWizard_Click(object sender, RoutedEventArgs e)
    {
        ReadConfigFromUi();
        new SetupWizardWindow(_cfg) { Owner = this }.ShowDialog();
        // 向导可能改了 llama 目录/模型配置，回填 UI
        LoadConfigToUi();
    }

    private void Cache_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _cfg.CacheEnabled = ChkCache.IsChecked == true;
        _cfg.Save();
        if (!ChkCache.IsChecked == true) _proxy?.ClearCache();
    }

    private bool _suppressSlider;
    private void BtnClearCache_Click(object sender, RoutedEventArgs e)
    {
        _proxy?.ClearCache();
        Log("已手动清空响应缓存");
    }

    private void BtnGlossary_Click(object sender, RoutedEventArgs e)
    {
        // 术语表在主窗口启动时加载一次，代理与 UI 共用同一实例，避免双实例互写
        _glossary ??= Core.Glossary.GlossaryStore.Load(
            System.IO.Path.Combine(AppContext.BaseDirectory, "glossary.json"));
        new GlossaryWindow(_glossary) { Owner = this }.ShowDialog();
    }

    private void BtnResetStats_Click(object sender, RoutedEventArgs e)
    {
        if (_proxy != null)
        {
            Interlocked.Exchange(ref _proxy.TotalRequests, 0);
            Interlocked.Exchange(ref _proxy.FilteredCount, 0);
            Interlocked.Exchange(ref _proxy.CacheHitCount, 0);
            Interlocked.Exchange(ref _proxy.ModelCount, 0);
            Interlocked.Exchange(ref _proxy.ErrorCount, 0);
        }
        _allRows.Clear();
        _view.Clear();
        UpdateStats();
        Log("统计计数已重置");
    }

    // ==================== 流量列表 ====================

    private void CmbFilter_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CmbFilter.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
        _filterKind = item.Content?.ToString() ?? "全部";
        RebuildView();
    }

    private void BtnClearTraffic_Click(object sender, RoutedEventArgs e)
    {
        _allRows.Clear();
        _view.Clear();
    }

    private static string KindToName(TrafficKind k) => k switch
    {
        TrafficKind.Filtered => "已过滤",
        TrafficKind.CacheHit => "缓存",
        TrafficKind.Model => "模型",
        TrafficKind.UpstreamError => "错误",
        TrafficKind.BadRequest => "请求异常",
        _ => k.ToString()
    };

    private bool MatchesFilter(string kindName) => _filterKind switch
    {
        "全部" => true,
        "已过滤" => kindName == "已过滤",
        "缓存命中" => kindName == "缓存",
        "模型翻译" => kindName == "模型",
        "错误" => kindName is "错误" or "请求异常",
        _ => true
    };

    /// <summary>「只看频道聊天」：无频道标签的行（战斗日志/拾取/伤亡等系统播报）一律隐藏。</summary>
    private bool MatchesRow(TrafficRow r)
        => MatchesFilter(r.KindName) && (ChkOnlyChannel.IsChecked != true || r.Channel != null);

    private void ChkOnlyChannel_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _cfg.TrafficOnlyChannel = ChkOnlyChannel.IsChecked == true;
        _cfg.Save();
        RebuildView();
    }

    private void RebuildView()
    {
        _view.Clear();
        foreach (var r in _allRows)
            if (MatchesRow(r)) _view.Add(r);
    }

    // ==================== 定时刷新 ====================

    private void FlushPending()
    {
        // ---- 流量 ----
        List<TrafficEntry> batch;
        lock (_pendingSync)
        {
            if (_pending.Count == 0) { batch = new List<TrafficEntry>(); }
            else
            {
                batch = new List<TrafficEntry>(_pending);
                _pending.Clear();
            }
        }

        if (batch.Count > 0)
        {
            var autoScroll = ChkAutoScroll.IsChecked == true;
            var firstIdx = _view.Count == 0 ? 0 : -1;

            foreach (var en in batch)
            {
                var kind = KindToName(en.Kind);
                var row = new TrafficRow
                {
                    TimeText = en.Time.ToString("HH:mm:ss"),
                    KindName = kind,
                    RuleId = en.RuleId,
                    RuleName = en.RuleName,
                    Content = en.Content,
                    ElapsedMs = en.ElapsedMs,
                    Channel = en.Channel
                };

                _allRows.Add(row);
                while (_allRows.Count > _cfg.LogMaxRows) _allRows.RemoveAt(0);

                if (MatchesRow(row))
                {
                    _view.Add(row);
                    while (_view.Count > _cfg.LogMaxRows) _view.RemoveAt(0);
                }

                if (en.Kind == TrafficKind.Filtered)
                {
                }
            }

            if (autoScroll && _view.Count > 0)
                TrafficGrid.ScrollIntoView(_view[^1]);

            UpdateStats();
        }

        // ---- 事件日志 ----
        List<string> logs;
        lock (_logSync)
        {
            logs = new List<string>(_pendingLog);
            _pendingLog.Clear();
        }
        if (logs.Count > 0)
        {
            TxtLog.AppendText(string.Join("\n", logs) + "\n");
            TxtLog.ScrollToEnd();
        }

        // ---- llama 输出 ----
        List<string> outs;
        lock (_outputSync)
        {
            outs = new List<string>(_pendingOutput);
            _pendingOutput.Clear();
        }
        if (outs.Count > 0)
        {
            TxtOutput.AppendText(string.Join("\n", outs) + "\n");
            if (ChkFollowOutput.IsChecked == true) TxtOutput.ScrollToEnd();
        }
    }

    private void UpdateStats()
    {
        if (_proxy == null) return;

        var total = Interlocked.Read(ref _proxy.TotalRequests);
        var filtered = Interlocked.Read(ref _proxy.FilteredCount);
        var cache = Interlocked.Read(ref _proxy.CacheHitCount);
        var model = Interlocked.Read(ref _proxy.ModelCount);
        var error = Interlocked.Read(ref _proxy.ErrorCount);

        BarTotal.Text = total.ToString();
        BarFiltered.Text = filtered.ToString();
        BarCache.Text = cache.ToString();
        BarModel.Text = model.ToString();
        BarError.Text = error.ToString();

        StatTotal.Text = total.ToString();
        StatFiltered.Text = filtered.ToString();
        StatCache.Text = cache.ToString();
        StatModel.Text = model.ToString();
        StatError.Text = error.ToString();

        var notModel = filtered + cache;
        if (total > 0)
        {
            var pct = 100.0 * notModel / total;
            BarSaved.Text = $"模型负载削减 {pct:F1}%";
            StatSaved.Text = $"{pct:F1}%";
            StatSavedDetail.Text =
                $"{notModel} / {total} 条请求未进入模型（过滤 {filtered} + 缓存 {cache}）。" +
                "日志基线：680 条请求中 609 条(88.6%)为系统噪音。";
        }
        else
        {
            BarSaved.Text = "模型负载削减 —";
            StatSaved.Text = "—";
        }

        StatCacheInfo.Text = $"缓存条目：{_proxy.CacheCount}（热层 {_proxy.CacheHotCount}）";

    }

    private void UpdateClock()
    {
        if (_llama == null) return;
        UptimeText.Text = _llama.Uptime.ToString(@"hh\:mm\:ss");

        // llama 内存占用监控（每秒采样显示；CSV 仅 -Debug 调试模式落盘）
        var mem = _llama.WorkingSetMB;
        LlamaPidText.Text = _llama.Pid is int p && _llama.State is LlamaState.Running or LlamaState.Starting
            ? (mem.HasValue ? $"PID {p} · {mem.Value:N0} MB" : $"PID {p}")
            : "";
        if (App.DebugMode && ++_memSampleTick >= 60)
        {
            _memSampleTick = 0;
            if (mem.HasValue && _llama.Pid is int mp)
            {
                try
                {
                    var csv = Path.Combine(AppContext.BaseDirectory, "llama_mem.csv");
                    if (!File.Exists(csv))
                        File.AppendAllText(csv, "time,pid,working_set_mb" + Environment.NewLine);
                    File.AppendAllText(csv,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{mp},{mem.Value:F1}" + Environment.NewLine);
                }
                catch { /* 日志写失败不影响服务 */ }
            }
        }

        // v41 内存超限自动重启：Running 且连续 60 秒超阈值 → 重启（重启后 10 分钟冷却）
        _llama.CheckMemoryRestart();
    }

    /// <summary>
    /// 按钮 IsEnabled 变化会迁移键盘焦点（WPF 顺链 BringIntoView 把左栏滚到底），
    /// 需要把焦点收回固定位置；但游戏目录已设置时不落入该输入框（用户指定：
    /// 仅未设置时才自动跳转），改为交给窗口本身承接焦点。
    /// </summary>
    private void KeepFocusStable()
    {
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Control c &&
            c.IsEnabled && c is not System.Windows.Controls.Button)
            return;   // 焦点在正常启用的非按钮控件上，不动
        if (string.IsNullOrWhiteSpace(TxtGameDir.Text))
            TxtGameDir.Focus();      // 未配置游戏目录：跳转到该输入框引导填写
        else
            System.Windows.Input.Keyboard.Focus(null);   // 已配置：清到窗口，不抢控件
    }

    private void OnLlamaStateChanged(LlamaState s)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var (text, brush) = s switch
            {
                LlamaState.Running => ("运行中", OkBrush()),
                LlamaState.Starting => ("启动中…", WarnBrush()),
                LlamaState.Crashed => ("异常退出", ErrBrush()),
                _ => ("已停止", ErrBrush())
            };
            LlamaStateText.Text = " " + text;
            LlamaDot.Fill = brush;
            LlamaPidText.Text = _llama?.Pid is int p && s != LlamaState.Stopped ? $"PID {p}" : "";

            BtnStartLlama.IsEnabled = s is LlamaState.Stopped or LlamaState.Crashed;
            BtnStopLlama.IsEnabled = s is LlamaState.Running or LlamaState.Starting;
            BtnRestartLlama.IsEnabled = s is LlamaState.Running or LlamaState.Starting;
            KeepFocusStable();
        }));
    }

    private void UpdateProxyDot(bool on)
    {
        ProxyStateText.Text = on ? " 运行中" : " 已停止";
        ProxyDot.Fill = on ? OkBrush() : ErrBrush();
    }

    private static System.Windows.Media.Brush OkBrush() =>
        new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0xBB, 0x6A));
    private static System.Windows.Media.Brush WarnBrush() =>
        new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xB7, 0x4D));
    private static System.Windows.Media.Brush ErrBrush() =>
        new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x53, 0x50));

    private void Log(string msg)
    {
        lock (_logSync)
        {
            _pendingLog.Enqueue($"[{DateTime.Now:HH:mm:ss}] {msg}");
            while (_pendingLog.Count > 500) _pendingLog.Dequeue();
        }
    }

    private void BtnClearOutput_Click(object sender, RoutedEventArgs e)
    {
        TxtOutput.Clear();
        _llama?.ClearOutput();
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e) => TxtLog.Clear();

    // ==================== 系统托盘 ====================

    private void Tray_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _cfg.MinimizeToTray = ChkTray.IsChecked == true;
        _cfg.Save();
        if (_tray != null) _tray.Visible = _cfg.MinimizeToTray;
    }

    private void InitTray()
    {
        if (_tray != null) return;
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "WoWTranslate 控制台",
            Icon = CreateTrayIcon(),
            Visible = _cfg.MinimizeToTray,
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("启动全部服务（llama + 代理）", null,
            (_, _) => Dispatcher.BeginInvoke(new Action(() => { StartLlama(); StartProxy(); })));
        menu.Items.Add("停止全部服务", null,
            (_, _) => Dispatcher.BeginInvoke(new Action(() => { StopLlama(); StopProxy(); })));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("术语表管理", null,
            (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                new Action(() => BtnGlossary_Click(this, new RoutedEventArgs()))));
        menu.Items.Add("环境检测与下载向导", null,
            (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                new Action(() => BtnWizard_Click(this, new RoutedEventArgs()))));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitFromTray());
        _tray.ContextMenuStrip = menu;
    }

    private void HideToTray()
    {
        InitTray();
        if (_tray != null) _tray.Visible = _cfg.MinimizeToTray;
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _forceExit = true;
        Close();
    }

    private static System.Drawing.Icon CreateTrayIcon()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x2D, 0x6C, 0xDF));
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(0, 0, 12, 12, 180, 90);
            path.AddArc(20, 0, 12, 12, 270, 90);
            path.AddArc(20, 20, 12, 12, 0, 90);
            path.AddArc(0, 20, 12, 12, 90, 90);
            path.CloseFigure();
            g.FillPath(bg, path);
            using var font = new System.Drawing.Font("Microsoft YaHei UI", 15f, System.Drawing.FontStyle.Bold);
            using var fg = System.Drawing.Brushes.White;
            var sz = g.MeasureString("译", font);
            g.DrawString("译", font, fg, (32 - sz.Width) / 2f, (32 - sz.Height) / 2f);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    // ==================== Direct DLL 部署（3.0 单轨） ====================

    private Core.DllSwitcher.SwitchStatus? _lastDllStatus;

    private void BtnRefreshDll_Click(object sender, RoutedEventArgs e) => RefreshDllStatus();

    private void RefreshDllStatus()
    {
        if (TxtDllStatus == null) return; // XAML 加载期事件防护
        var gameDir = TxtGameDir.Text.Trim();
        var assets = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "direct-dll");
        try
        {
            var status = Core.DllSwitcher.Probe(gameDir, assets);
            _lastDllStatus = status;
            TxtDllStatus.Text = $"当前：{status.CurrentText}\n" + string.Join("\n", status.Details);

            UpdateDirectConfigHint();
        }
        catch (Exception ex)
        {
            TxtDllStatus.Text = $"检测失败：{ex.Message}";
        }
    }

    private void BtnTrackDirect_Click(object sender, RoutedEventArgs e)
    {
        // 手动重装 = 解除卸载锁（SkipAutoDeploy），允许此后恢复自动部署
        if (_cfg.SkipAutoDeploy)
        {
            _cfg.SkipAutoDeploy = false;
            _cfg.Save();
            Log("已解除卸载标记，恢复自动部署。");
        }
        AutoDeployTrackB();
        RefreshDllStatus();
    }

    // ==================== Track B 显示模式（v16 全自治驱动） ====================

    /// <summary>当前实际轨道为 direct 时，把显示设置同步到游戏目录 WoWTranslateDirect.json。</summary>
    private void SyncDirectConfigToGameDir()
    {
        if (!IsLoaded) return;
        _cfg.Save();
        try
        {
            var gameDir = TxtGameDir.Text.Trim();
            if (_lastDllStatus?.Current == Core.DllSwitcher.TrackDirect &&
                Directory.Exists(gameDir) && File.Exists(Path.Combine(gameDir, "Wow.exe")))
            {
                Core.DllSwitcher.WriteDirectConfig(gameDir, _cfg.ListenPort,
                    _cfg.DirectDisplayMode, _cfg.DirectDisplayPrefix, _cfg.DirectOutgoingMode,
                    dllLog: App.DebugMode,
                    outFilter: _cfg.OutgoingFilterRules,
                    outOff: _cfg.DirectOutgoingOffChannels);
                Log($"已更新游戏目录 WoWTranslateDirect.json（displayMode={_cfg.DirectDisplayMode}，outgoing={_cfg.DirectOutgoingMode}，log={App.DebugMode}，游戏内 /reload 生效）");
            }
        }
        catch (Exception ex)
        {
            Log("同步 Track B 显示配置失败：" + ex.Message);
        }
        UpdateDirectConfigHint();
    }

    /// <summary>
    /// DLL 轨道卡的状态标记：对比界面配置与游戏目录 WoWTranslateDirect.json，
    /// 未同步/未部署/损坏时给出 ⚠ 提示。
    /// </summary>
    private void UpdateDirectConfigHint()
    {
        if (TxtDirectDisplayHint == null) return;
        try
        {
            if (_lastDllStatus?.Current != Core.DllSwitcher.TrackDirect)
            {
                TxtDirectDisplayHint.Text = "Direct DLL 未部署；修改上方设置时会自动写入游戏目录配置。";
                return;
            }
            var jsonPath = Path.Combine(TxtGameDir.Text.Trim(), "WoWTranslateDirect.json");
            if (!File.Exists(jsonPath))
            {
                TxtDirectDisplayHint.Text = "⚠ 游戏目录还没有 WoWTranslateDirect.json，修改任一设置即可自动写入；游戏内 /reload 或重启客户端生效。";
                return;
            }
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath));
            string Get(string key) =>
                doc.RootElement.TryGetProperty(key, out var v) &&
                v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
            var synced = Get("displayMode") == _cfg.DirectDisplayMode &&
                         Get("displayPrefix") == _cfg.DirectDisplayPrefix &&
                         Get("outgoingMode") == _cfg.DirectOutgoingMode &&
                         Get("outFilter") == (_cfg.OutgoingFilterRules ?? "") &&
                         Get("outOff") == (_cfg.DirectOutgoingOffChannels ?? "");
            TxtDirectDisplayHint.Text = synced
                ? "✔ 已与游戏目录配置同步；游戏内 /reload 或重启客户端生效。"
                : "⚠ 界面配置与游戏目录不一致，改动任一设置即可自动写入；游戏内 /reload 或重启客户端生效。";
        }
        catch
        {
            TxtDirectDisplayHint.Text = "⚠ 游戏目录 WoWTranslateDirect.json 读取失败（可能已损坏），改动任一设置即可重写。";
        }
    }

    private void CmbDirectDisplay_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || CmbDirectDisplay.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
        var mode = (string)item.Tag;
        if (mode == _cfg.DirectDisplayMode) return;
        _cfg.DirectDisplayMode = mode;
        SyncDirectConfigToGameDir();
    }

    private void TxtDirectPrefix_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var prefix = string.IsNullOrWhiteSpace(TxtDirectPrefix.Text) ? "[译]" : TxtDirectPrefix.Text;
        TxtDirectPrefix.Text = prefix;
        if (prefix == _cfg.DirectDisplayPrefix) return;
        _cfg.DirectDisplayPrefix = prefix;
        SyncDirectConfigToGameDir();
    }

    // ==================== 游戏目录校验 + 自动部署 ====================

    /// <summary>校验游戏根目录：Wow.exe 与 Data 必需，Interface 缺失仅提示。</summary>
    private bool ValidateGameDir(bool report)
    {
        var dir = TxtGameDir.Text.Trim();
        string status;
        var ok = false;
        if (dir.Length == 0) status = "⚠ 未填写游戏目录——填写后自动部署翻译 DLL";
        else if (!Directory.Exists(dir)) status = "⚠ 目录不存在：" + dir;
        else if (!File.Exists(Path.Combine(dir, "Wow.exe"))) status = "⚠ 未找到 Wow.exe——请填客户端根目录";
        else if (!Directory.Exists(Path.Combine(dir, "Data"))) status = "⚠ 未找到 Data 文件夹——该目录不是 3.3.5 客户端根目录";
        else if (!Directory.Exists(Path.Combine(dir, "Interface")))
        { status = "✔ 游戏目录有效（Interface 尚未生成，进一次游戏即可）"; ok = true; }
        else { status = "✔ 游戏目录有效"; ok = true; }
        if (report) TxtGameDirStatus.Text = status;
        return ok;
    }

    private void TxtGameDir_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _cfg.GameDir = TxtGameDir.Text.Trim();
        _cfg.Save();
        var ok = ValidateGameDir(true);
        RefreshDllStatus();
        if (ok) AutoDeployTrackB();
    }

    /// <summary>游戏目录有效时自动部署主推轨道 B（用户无需选择轨道）。</summary>
    private void AutoDeployTrackB()
    {
        // 用户卸载后不再悄悄重装；点「重新部署 Direct DLL」可恢复（会清掉该标记）
        if (_cfg.SkipAutoDeploy)
        {
            Log("ℹ 已跳过自动部署（此前卸载过 Direct DLL）；需要重装请点「重新部署 Direct DLL」。");
            return;
        }
        try
        {
            var gameDir = TxtGameDir.Text.Trim();
            if (!Directory.Exists(gameDir)) return;
            var assets = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "direct-dll");
            var lines = Core.DllSwitcher.EnsureDeployed(gameDir, assets,
                _cfg.DirectDisplayMode, _cfg.DirectDisplayPrefix, _cfg.DirectOutgoingMode,
                listenPort: _cfg.ListenPort, dllLog: App.DebugMode,
                outFilter: _cfg.OutgoingFilterRules, outOff: _cfg.DirectOutgoingOffChannels);
            foreach (var l in lines) Log(l);
            RefreshDllStatus();
        }
        catch (Exception ex)
        {
            Log("自动部署失败：" + ex.Message);
        }
    }

    private void CmbDirectOutgoing_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || CmbDirectOutgoing.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
        var mode = (string)item.Tag;
        if (mode == _cfg.DirectOutgoingMode) return;
        _cfg.DirectOutgoingMode = mode;
        SyncDirectConfigToGameDir();
    }

    // ==================== 卸载 Direct DLL（v41 智能卸载） ====================

    private void BtnUninstallDirect_Click(object sender, RoutedEventArgs e)
    {
        var gameDir = TxtGameDir.Text.Trim();
        if (MessageBox.Show(this,
                "将从游戏目录卸载 Direct DLL（dinput8.dll / WoWTranslateDirect.dll / dlls.txt / WoWTranslateDirect.json）。\n\n" +
                "部署前的原有文件会自动从 wtc_backup 还原；卸载后控制台不再自动重装，点「重新部署 Direct DLL」可恢复。\n\n继续卸载？",
                "卸载 Direct DLL", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var assets = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "direct-dll");
        foreach (var l in Core.DllSwitcher.UninstallDirect(gameDir, assets))
            Log(l);
        // 卸载生效才置防重装标记（未部署/被拒绝时不动）
        if (Core.DllSwitcher.Probe(gameDir, assets).Current != Core.DllSwitcher.TrackDirect)
        {
            _cfg.SkipAutoDeploy = true;
            _cfg.Save();
        }
        RefreshDllStatus();
    }

    // ==================== llama 重启与内存阈值（v41） ====================

    private void BtnRestartLlama_Click(object sender, RoutedEventArgs e)
    {
        if (_llama == null) return;
        // Stop 最长等 3 秒退出，放后台避免卡 UI；状态经 OnStateChanged 回 UI
        System.Threading.Tasks.Task.Run(() => _llama.Restart("手动重启"));
    }

    private void TxtLlamaMemLimit_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (!int.TryParse(TxtLlamaMemLimit.Text.Trim(), out var mb)) mb = 4096;
        mb = Math.Clamp(mb, 512, 65536);
        TxtLlamaMemLimit.Text = mb.ToString();
        if (mb == _cfg.LlamaMemLimitMB) return;
        _cfg.LlamaMemLimitMB = mb;
        _cfg.Save();
        Log($"llama 内存阈值已设为 {mb} MB（持续超限 60 秒自动重启，重启后 10 分钟冷却）");
    }

    private void ChkLlamaAutoRestart_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _cfg.LlamaAutoRestart = ChkLlamaAutoRestart.IsChecked == true;
        _cfg.Save();
        Log($"llama 自动重启（内存超限/崩溃拉起）已{(_cfg.LlamaAutoRestart ? "开启" : "关闭")}");
    }

    // ==================== 发送消息过滤器（v41） ====================

    /// <summary>外发频道开关（勾选 = 翻译）：频道常量 ↔ 复选框。</summary>
    private (string Channel, System.Windows.Controls.CheckBox Box)[] OutChannels() => new[]
    {
        ("SAY", ChkOutSay), ("YELL", ChkOutYell), ("WHISPER", ChkOutWhisper),
        ("PARTY", ChkOutParty), ("GUILD", ChkOutGuild), ("OFFICER", ChkOutOfficer),
        ("RAID", ChkOutRaid), ("RAID_WARNING", ChkOutRaidWarn),
        ("BATTLEGROUND", ChkOutBg), ("CHANNEL", ChkOutChannel),
    };

    private string BuildOutgoingOffChannels() =>
        string.Join(",", OutChannels().Where(c => c.Box.IsChecked != true).Select(c => c.Channel));

    private void OutChannel_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _cfg.DirectOutgoingOffChannels = BuildOutgoingOffChannels();
        _cfg.Save();
        SyncDirectConfigToGameDir();
    }

    private void TxtOutgoingFilter_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var rules = TxtOutgoingFilter.Text.Replace("\r\n", "\n").TrimEnd('\n');
        TxtOutgoingFilter.Text = rules;
        if (rules == _cfg.OutgoingFilterRules) return;
        _cfg.OutgoingFilterRules = rules;
        _cfg.Save();
        SyncDirectConfigToGameDir();
    }
}
