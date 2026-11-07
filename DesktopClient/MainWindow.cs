using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

namespace QNAgent;

internal sealed partial class MainWindow : Window
{
    private readonly ClientStore _store;
    private readonly WindowBindingRegistry _bindings;
    private readonly KnowledgeAdviceService _adviceService;
    private readonly ObservableCollection<ReceptionSession> _sessions = new();
    private ClientSettings _settings;
    private readonly ContentControl _page = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _windows = new();
    private readonly ListBox _connected = new() { Height = 170, DisplayMemberPath = "Display" };
    private readonly TextBlock _channelState = new() { TextWrapping = TextWrapping.Wrap };
    private readonly System.Windows.Controls.Image _captureImage = new() { MaxHeight = 360, Stretch = Stretch.Uniform };
    private readonly TextBox _seller = Input(), _poll = Input(), _endpoint = Input(), _model = Input(), _onlineUrl = Input();
    private readonly CheckBox _fastRecognition = new() { Content = "快速识别（多核电脑最多使用 2 个低优先级计算线程）", Margin = new Thickness(0, 10, 0, 8) };
    private readonly PasswordBox _aiKey = new(), _onlineKey = new();
    private readonly TextBox _jevEndpoint = Input(), _jevModel = Input();
    private readonly PasswordBox _jevKey = new();
    private readonly ListBox _products = new();
    private readonly StackPanel _productEditor = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _productEditorState = Text("点击“新增商品”开始录入；修改资料请先选中商品，再点击“编辑选中商品”。");
    private Button _editProductButton = null!;
    private readonly Dictionary<string, TextBox> _fields = new();
    private readonly CheckBox _approved = new() { Content = "我已核对这些商品资料，允许用于当前商品的 AI 回复", Margin = new Thickness(0, 12, 0, 12) };

    private readonly System.Windows.Forms.NotifyIcon _tray = new();
    private readonly System.Drawing.Icon _trayIcon = AppBranding.CreateTrayIcon();
    private readonly CancellationTokenSource _lifetime = new();
    private CatalogProduct? _editing;
    private bool _closing, _exitRequested, _disconnectingAll;
    private Task? _disconnectAllTask;
    private byte[]? _latestScreenshot;
    private ReceptionSession? _previewSession;
    private volatile bool _previewVisible;
    private int _previewPending;
    private long _lastPreviewTick, _previewEpoch;
    private UIElement _workPage = null!, _channelPage = null!, _aiPage = null!, _knowledgePage = null!, _diagnosticsPage = null!;

    public MainWindow(ClientStore store, IKnowledgeAdviceClient? adviceClient = null)
    {
        _store = store; _settings = store.Settings(); _bindings = new WindowBindingRegistry(store);
        _store.Diagnostics.Configure(_settings.LogLevel);
        _adviceService = new KnowledgeAdviceService(store, adviceClient);
        Title = "QN Agent · 千牛客服助手"; Width = 1220; Height = 820; MinWidth = 960; MinHeight = 650;
        Icon = AppBranding.WindowIcon;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#F3F6FA"); Foreground = Brush("#243249"); FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14;
        Resources.Add(typeof(TextBox), new Style(typeof(TextBox)) { Setters = { new Setter(Control.PaddingProperty, new Thickness(9)), new Setter(Control.BorderBrushProperty, Brush("#CBD5E1")), new Setter(Control.BorderThicknessProperty, new Thickness(1)) } });
        Resources.Add(typeof(PasswordBox), new Style(typeof(PasswordBox)) { Setters = { new Setter(Control.PaddingProperty, new Thickness(9)), new Setter(Control.BorderBrushProperty, Brush("#CBD5E1")) } });
        Resources.Add(typeof(ComboBox), new Style(typeof(ComboBox)) { Setters = { new Setter(Control.PaddingProperty, new Thickness(7)), new Setter(FrameworkElement.MinHeightProperty, 36.0) } });
        BuildShell(); LoadSettings();
        _connected.ItemsSource = _sessions; _connected.SelectionChanged += (_, _) => SelectConnection();
        _products.SelectionChanged += (_, _) =>
        {
            _editProductButton.IsEnabled = _products.SelectedItem is CatalogProduct;
            if (!_refreshingProducts) SelectAdviceProduct(_products.SelectedItem as CatalogProduct);
        };
        _products.MouseDoubleClick += (_, _) => EditSelectedProduct();
        RefreshProducts();
        _tray.Text = "QN Agent · 多窗口助手"; _tray.Icon = _trayIcon;
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示全部会话助手", null, (_, _) => ShowConversations());
        menu.Items.Add("设置 / 连接管理", null, (_, _) => ShowSettings());
        menu.Items.Add("退出全部助手", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu; _tray.DoubleClick += (_, _) => ShowSettings(); _tray.Visible = true;
        Closing += async (_, e) =>
        {
            if (_closing) return;
            if (!_exitRequested && _sessions.Count > 0) { e.Cancel = true; Hide(); _previewVisible = false; return; }
            e.Cancel = true; _closing = true; IsEnabled = false; _lifetime.Cancel();
            await Dispatcher.Yield(DispatcherPriority.Background);
            await DisconnectAllAsync(); _tray.Visible = false; _tray.Dispose(); _trayIcon.Dispose(); Close();
        };
        Loaded += async (_, _) =>
        {
            _store.Write("app-status.json", new { State = "WindowOpen", ProcessId = Environment.ProcessId, StartedAt = DateTimeOffset.Now, Title });
            await ScanWindowsAsync();
        };
    }
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static BitmapImage DecodePreview(byte[] png, int width = 0)
    {
        using var stream = new MemoryStream(png, writable: false);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        if (width > 0) image.DecodePixelWidth = width;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }

    private void QueuePreview(bool force = false)
    {
        if (!_previewVisible || _closing || _previewSession is null) return;
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastPreviewTick) < 1000) return;
        if (Interlocked.CompareExchange(ref _previewPending, 1, 0) != 0) return;
        Interlocked.Exchange(ref _lastPreviewTick, now);
        var session = _previewSession; var epoch = _previewEpoch;
        _ = Task.Run(async () =>
        {
            try
            {
                var png = session.LastScreenshot; if (png is null) return;
                var image = DecodePreview(png, 1000);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!_closing && _previewVisible && ReferenceEquals(session, _previewSession) && epoch == _previewEpoch)
                        _captureImage.Source = image;
                }, DispatcherPriority.Background);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { Debug.WriteLine("Preview unavailable: " + error.GetType().Name); }
            finally
            {
                Interlocked.Exchange(ref _previewPending, 0);
                if (!_closing && !Dispatcher.HasShutdownStarted && epoch != Interlocked.Read(ref _previewEpoch))
                    _ = Dispatcher.BeginInvoke(() => QueuePreview(true));
            }
        });
    }
    private static TextBox Input(bool multiline = false) => new() { AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden, MinHeight = multiline ? 120 : 36 };
    private static TextBlock Text(string text, int size = 14) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
    private static StackPanel Stack() => new() { Margin = new Thickness(24) };
    private static ScrollViewer Scroll(UIElement child) => new() { Content = child, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private static void Field(Panel panel, string label, UIElement control)
    { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 12, 0, 5), TextWrapping = TextWrapping.Wrap }); panel.Children.Add(control); }
    private Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(0, 8, 8, 8), Background = Brush("#176BCE"), Foreground = Brushes.White, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); }
            catch (OperationCanceledException) { SetStatus("操作已取消。"); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { _store.Diagnostics.Write(DiagnosticLogLevel.Error, DiagnosticEvent.UiOperationFailed, error: error); SetStatus("操作未完成：" + Friendly(error)); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }
    private Button Button(string text, Action action) => Button(text, () => { action(); return Task.CompletedTask; });
    private static string Friendly(Exception error) => error is System.Net.Http.HttpRequestException ? "网络连接失败，请检查服务地址与网络。" : error is JsonException ? "返回内容不是支持的 JSON 格式，请检查接口或导入文件。" : error.Message;

    private void SetStatus(string text) => _status.Text = text;
    private void ShowSettings()
    {
        if (_closing) return;
        Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate(); _previewVisible = ReferenceEquals(_page.Content, _channelPage); QueuePreview(true);
    }
    private void ExitApp() { _exitRequested = true; Close(); }
    private void ShowConversations()
    {
        if (_sessions.Count == 0) { _page.Content = _channelPage; ShowSettings(); SetStatus("请先添加千牛窗口连接。"); return; }
        foreach (var session in _sessions) session.Reveal();
        Hide(); _previewVisible = false;
    }
    private ReceptionSession SelectedSession() => _connected.SelectedItem as ReceptionSession ?? throw new InvalidOperationException("请在已连接列表中选择窗口。");
    private void SelectConnection()
    {
        _previewSession = _connected.SelectedItem as ReceptionSession; _previewEpoch++;
        _captureImage.Source = null; _latestScreenshot = _previewSession?.LastScreenshot;
        if (_previewSession is { } selected) { _seller.Text = selected.SellerId; _channelState.Text = selected.Display; }
        QueuePreview(true);
    }
    private void BuildShell()
    {
        var root = new DockPanel();
        var footer = new Border { Background = Brushes.White, Padding = new Thickness(20, 12, 20, 12), Child = _status, BorderBrush = Brush("#DCE3ED"), BorderThickness = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var nav = new StackPanel { Width = 190, Background = Brush("#13243D") };
        nav.Children.Add(new TextBlock { Text = "QN Agent", FontSize = 25, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(20, 28, 0, 8) });
        nav.Children.Add(new TextBlock { Text = "千牛客服助手", Foreground = Brush("#A9BED9"), Margin = new Thickness(20, 0, 0, 26) });
        var intro = Stack(); intro.Children.Add(Text("贴窗会话助手", 28));
        intro.Children.Add(Text("会话与回复建议已移到千牛旁的小窗口。每个已连接窗口都有独立小窗，分别显示自己的买家和回复。"));
        intro.Children.Add(Button("显示全部会话小窗", ShowConversations));
        intro.Children.Add(Text("可在千牛连接页逐个添加窗口，每个窗口单独绑定店铺。可在小窗中查看问题、选择商品、生成和编辑回复，再确认复制。点击小窗的「设置」可回到这里。", 14));
        _workPage = intro; _channelPage = BuildChannel(); _aiPage = BuildAi(); _knowledgePage = BuildKnowledge(); _diagnosticsPage = BuildDiagnostics();
        _businessPage = BuildBusiness(); _learningPage = BuildLearning();
        foreach (var (title, page) in new[] { ("会话助手", _workPage), ("千牛连接", _channelPage), ("AI 设置", _aiPage), ("商品与知识库", _knowledgePage), ("店铺与备份", _businessPage), ("学习建议", _learningPage), ("日志与诊断", _diagnosticsPage) })
        {
            var button = Button(title, () =>
            {
                _page.Content = page; _previewVisible = ReferenceEquals(page, _channelPage);
                if (ReferenceEquals(page, _diagnosticsPage)) RefreshDiagnosticStatus();
                if (ReferenceEquals(page, _businessPage)) RefreshShops();
                if (ReferenceEquals(page, _learningPage)) RefreshLearning();
                QueuePreview(force: true);
            });
            button.Margin = new Thickness(12, 4, 12, 4); button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Background = Brush("#243C5B"); nav.Children.Add(button);
        }
        var open = Button("打开数据目录", () => Process.Start(new ProcessStartInfo("explorer.exe", _store.Root) { UseShellExecute = true }));
        open.Margin = new Thickness(12, 30, 12, 8); open.HorizontalAlignment = HorizontalAlignment.Stretch; nav.Children.Add(open);
        var exit = Button("退出助手", ExitApp);
        exit.Margin = new Thickness(12, 4, 12, 4); exit.HorizontalAlignment = HorizontalAlignment.Stretch; nav.Children.Add(exit);
        nav.Children.Add(new TextBlock { Text = "本机保存 · 人工审阅\n不自动发送消息", Foreground = Brush("#A9BED9"), Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap });
        var navScroll = Scroll(nav); DockPanel.SetDock(navScroll, Dock.Left); root.Children.Add(navScroll); root.Children.Add(_page);
        _page.Content = _channelPage; _previewVisible = true; Content = root;
    }
    private UIElement BuildChannel()
    {
        var panel = Stack(); panel.Children.Add(Text("千牛连接", 28));
        panel.Children.Add(Text("读取本机千牛接待窗口。请保持窗口显示；可读取当前可见会话，不会登录账号或代替人工发送。"));
        Field(panel, "待添加窗口的店铺固定 ID（可到“店铺与备份”选择；兼容旧标识）", _seller);
        Field(panel, "画面检查间隔（毫秒，250–2000，默认 500）", _poll);
        panel.Children.Add(_fastRecognition);
        panel.Children.Add(Text("如感觉打字受影响，可关闭快速识别，保存后重新连接，恢复单线程省资源模式。", 12));
        panel.Children.Add(Text("聊天变化并稳定后才识别；空闲时自动放慢至 1–2 秒检查一次，减少打字卡顿。图片仅在内存中处理，预览只在此页低频刷新。修改间隔后重新连接生效。", 12));
        panel.Children.Add(Button("保存连接设置", SaveSettings));
        Field(panel, "检测到的千牛接待窗口", _windows);
        panel.Children.Add(Text("可同时添加多个窗口。每次选择一个接待窗口并填写对应店铺，再点击添加；不同店铺使用不同标识。", 12));
        var actions = new WrapPanel(); actions.Children.Add(Button("刷新窗口", ScanWindowsAsync));
        actions.Children.Add(Button("添加窗口连接", AddConnectionAsync)); actions.Children.Add(Button("收起设置，显示全部助手", ShowConversations)); panel.Children.Add(actions);
        panel.Children.Add(Text("已连接的千牛窗口", 18)); panel.Children.Add(_connected);
        var manage = new WrapPanel();
        manage.Children.Add(Button("显示选中助手", () => SelectedSession().Reveal()));
        manage.Children.Add(Button("断开选中连接", () => DisconnectAsync(SelectedSession())));
        manage.Children.Add(Button("断开全部", DisconnectAllAsync)); panel.Children.Add(manage);
        panel.Children.Add(_channelState);
        panel.Children.Add(Text("选中连接的最近采集画面", 18)); panel.Children.Add(_captureImage);
        panel.Children.Add(Button("查看原始画面", () =>
        {
            if (_latestScreenshot is null) throw new InvalidOperationException("尚未取得有效画面。");
            new Window { Owner = this, Title = "采集画面（仅在内存中）", Width = 1100, Height = 760,
                Content = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new System.Windows.Controls.Image { Source = DecodePreview(_latestScreenshot), Stretch = Stretch.None } }
            }.ShowDialog();
        }));
        panel.Children.Add(Button("导出当前截图…", () =>
        {
            if (_latestScreenshot is null) throw new InvalidOperationException("尚未取得有效画面。");
            var snapshot = _latestScreenshot;
            var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = $"千牛画面_{DateTime.Now:yyyyMMdd_HHmmss}.png" };
            if (dialog.ShowDialog(this) == true) File.WriteAllBytes(dialog.FileName, snapshot);
        }));
        return Scroll(panel);
    }
    private UIElement BuildAi()
    {
        var panel = Stack(); panel.Children.Add(Text("AI 服务设置", 28));
        panel.Children.Add(Text("支持 OpenRouter 及兼容 Chat Completions 的服务，也可连接本机模型。自动生成开启时，仅在新买家消息到达且商品已核对时生成建议，卖家消息只更新上下文；也可手动重新生成。回复只发送当前选中会话和商品资料。保存商品后的资料检查也使用此服务，仅发送当前商品、同店优惠和已忽略问题。"));
        Field(panel, "API 基础地址（例如 https://openrouter.ai/api/v1；不要附加 /chat/completions）", _endpoint);
        Field(panel, "模型名称", _model); Field(panel, "API Key（仅以当前 Windows 用户可解密的形式保存在本机）", _aiKey);
        panel.Children.Add(Button("保存 AI 设置", SaveSettings));
        panel.Children.Add(Button("测试实际连接", async () =>
        {
            SaveSettings(); SetStatus("正在测试 AI 服务…");
            var result = await Connections.ChatAsync(_settings, "这是连接测试，请只回复：连接成功。不含买家或商品信息。", _lifetime.Token);
            SetStatus("AI 实际请求成功：" + result);
        }));
        panel.Children.Add(Text("JEV 意图识别与资料核验", 23));
        panel.Children.Add(Text("先由 JEV 判断意图和资料是否足够，再由上方模型组织回复，最后核验回复依据。JEV 不可用时只提示核实，不用关键词规则代替。"));
        Field(panel, "JEV 接口地址", _jevEndpoint); Field(panel, "JEV 模型", _jevModel);
        Field(panel, "JEV API Key（本机加密；留空时读取 TYPESAFE_API_KEY）", _jevKey);
        panel.Children.Add(Button("保存 JEV 设置", SaveSettings));
        panel.Children.Add(Button("测试 JEV 实际连接", async () =>
        {
            SaveSettings(); SetStatus("正在测试 JEV 意图与资料判定…");
            var result = await new JevGateway().AnalyzeAsync(_settings,
                "{\"BuyerQuestion\":\"这款是什么材质？\",\"Product\":{\"Fields\":{\"Material\":\"纯钛\"}},\"Conversation\":[]}", _lifetime.Token);
            SetStatus($"JEV 实际连接成功：{result.Model}；意图 {result.Intent.IntentName}；资料判定 {result.Mode}。");
        }));
        return Scroll(panel);
    }
    private UIElement BuildKnowledge()
    {
        var panel = Stack(); panel.Children.Add(Text("商品资料与知识库", 28));
        panel.Children.Add(Text("按店铺和商品保存资料。支持手工录入、商品 JSON 导入、TXT / Markdown 备注导入，以及在线 JSON 接口同步。导入与同步后的内容均先作为待核对资料。"));
        var actions = new WrapPanel();
        actions.Children.Add(Button("新增商品", () => { _products.SelectedItem = null; EditProduct(new CatalogProduct { SellerId = _seller.Text.Trim() }); }));
        _editProductButton = Button("编辑选中商品", EditSelectedProduct); _editProductButton.IsEnabled = false;
        actions.Children.Add(_editProductButton);
        actions.Children.Add(Button("导入商品 JSON", ImportJson));
        actions.Children.Add(Button("导出商品 JSON", ExportJson)); panel.Children.Add(actions);
        _products.Height = 145; panel.Children.Add(_products);
        panel.Children.Add(_productEditorState); panel.Children.Add(_productEditor);
        var labels = new[] { ("SellerId", "店铺标识"), ("ProductId", "商品 ID"), ("Name", "商品名称"), ("SalesScope", "售卖内容 / 不包含项目（用于澄清买家误解）"), ("Material", "材质"), ("Capacity", "通用规格说明（可选，旧版规格资料保留在此）"), ("CurrentPrice", "通用价格说明（可选；各款价格请填在规格列表）"), ("ShippingTime", "发货说明"), ("ReturnPolicy", "售后政策"), ("CustomizationPolicy", "定制说明"), ("Source", "资料来源"), ("Notes", "其他文档 / 原有补充知识（可选）") };
        foreach (var (key, label) in labels)
        {
            var input = Input(key == "Notes");
            input.TextChanged += (_, _) => _approved.IsChecked = false;
            _fields[key] = input; Field(_productEditor, label, input);
            if (key == "Name") BuildVariantEditor(_productEditor);
            if (key == "CurrentPrice") BuildProductPromotions(_productEditor);
            if (key == "Source") BuildFaqEditor(_productEditor);
        }
        _productEditor.Children.Add(Button("导入 TXT / Markdown 到当前商品备注", ImportNotes));
        _productEditor.Children.Add(_approved); _productEditor.Children.Add(Button("保存当前商品资料", SaveProduct));
        BuildShopPromotions(panel);
        panel.Children.Add(Text("在线知识源", 23));
        panel.Children.Add(Text("填写返回同格式商品 JSON 数组的 HTTPS 接口或导出链接。可带 Bearer Token；只读取并同步到本地，不修改在线知识库。", 12));
        Field(panel, "在线知识库 JSON 地址", _onlineUrl); Field(panel, "访问 Token（可选，本机加密）", _onlineKey);
        panel.Children.Add(Button("保存在线知识源", SaveSettings));
        panel.Children.Add(Button("立即同步为待核对资料", async () =>
        {
            SaveSettings(); SetStatus("正在读取在线知识库…");
            var count = await Connections.SyncKnowledgeAsync(_store, _settings, _lifetime.Token);
            RefreshProducts(); SetStatus($"已同步 {count} 条商品资料，请核对后允许 AI 使用。");
        }));
        return BuildAdviceLayout(panel);
    }
    private void LoadSettings()
    {
        _seller.Text = _settings.SellerId; _poll.Text = _settings.CaptureIntervalMilliseconds.ToString();
        _fastRecognition.IsChecked = _settings.FastRecognition;
        _endpoint.Text = _settings.AiEndpoint; _model.Text = _settings.AiModel;
        _aiKey.Password = SecretVault.Unprotect(_settings.AiSecret);
        _jevEndpoint.Text = _settings.JevEndpoint; _jevModel.Text = _settings.JevModel;
        _jevKey.Password = SecretVault.Unprotect(_settings.JevSecret);
        _onlineUrl.Text = _settings.OnlineKnowledgeUrl; _onlineKey.Password = SecretVault.Unprotect(_settings.OnlineKnowledgeSecret);
        LoadDiagnosticLevel();
    }
    private void SaveSettings()
    {
        Connections.Endpoint(_endpoint.Text.Trim());
        Connections.Endpoint(_jevEndpoint.Text.Trim());
        if (string.IsNullOrWhiteSpace(_jevModel.Text) || _jevModel.Text.Trim().Length > 80) throw new InvalidDataException("请填写有效的 JEV 模型名称。");
        if (_onlineUrl.Text.Trim().Length > 0) Connections.Endpoint(_onlineUrl.Text.Trim());
        if (!int.TryParse(_poll.Text, out var milliseconds) || milliseconds is < 250 or > 2000) throw new InvalidDataException("画面检查间隔须为 250–2000 毫秒。");
        if (_seller.Text.Trim().Length > 128 || _seller.Text.Any(char.IsControl)) throw new InvalidDataException("店铺标识过长或含无效字符。");
        _settings = _settings with { SellerId = _seller.Text.Trim(), CaptureIntervalMilliseconds = milliseconds, FastRecognition = _fastRecognition.IsChecked == true,
            AiEndpoint = _endpoint.Text.Trim().TrimEnd('/'), AiModel = _model.Text.Trim(), AiSecret = SecretVault.Protect(_aiKey.Password.Trim()),
            JevEndpoint = _jevEndpoint.Text.Trim(), JevModel = _jevModel.Text.Trim(),
            JevSecret = SecretVault.Protect(string.IsNullOrWhiteSpace(_jevKey.Password) ? Environment.GetEnvironmentVariable("TYPESAFE_API_KEY") ?? "" : _jevKey.Password.Trim()),
            OnlineKnowledgeUrl = _onlineUrl.Text.Trim(), OnlineKnowledgeSecret = SecretVault.Protect(_onlineKey.Password.Trim()) };
        _store.SaveSettings(_settings); SetStatus("设置已保存到本机。");
    }

    private sealed record WindowOption(QianniuWindow Window)
    {
        public override string ToString() => $"{Window.WindowTitle} · {(Window.IsMinimized ? "已最小化" : Window.IsVisible ? "可见" : "不可见")} · 0x{Window.Hwnd:X}";
    }
    private async Task ScanWindowsAsync()
    {
        var selected = (_windows.SelectedItem as WindowOption)?.Window;
        var windows = await Task.Run(QianniuConnection.Windows);
        var options = windows.Select(w => new WindowOption(w)).ToArray(); _windows.ItemsSource = options;
        _windows.SelectedItem = options.FirstOrDefault(o => o.Window.Hwnd == selected?.Hwnd && o.Window.Pid == selected?.Pid);
        if (_windows.SelectedItem is null && options.Length == 1) _windows.SelectedIndex = 0;
        _channelState.Text = $"发现 {windows.Count} 个接待窗口，已添加 {_sessions.Count} 个连接。"; SetStatus(_channelState.Text);
    }
    private Task AddConnectionAsync()
    {
        if (_closing || _disconnectingAll) throw new InvalidOperationException("正在关闭连接，请稍候再添加窗口。");
        if (_windows.SelectedItem is not WindowOption option) throw new InvalidOperationException("请选择待添加的千牛接待窗口。");
        var duplicate = _sessions.FirstOrDefault(s => s.Target.Hwnd == option.Window.Hwnd && s.Target.Pid == option.Window.Pid);
        if (duplicate is not null) { _connected.SelectedItem = duplicate; duplicate.Reveal(); throw new InvalidOperationException("该窗口已连接到店铺「" + duplicate.SellerId + "」。要修改绑定，请先断开这个连接。"); }
        SaveSettings();
        var binding = _bindings.GetOrCreate(option.Window, _seller.Text);
        var session = new ReceptionSession(_store, _bindings.OpenStore(binding), binding, option.Window, Resources,
            s => { _connected.SelectedItem = s; ShowSettings(); }, s => _ = DisconnectAsync(s));
        session.StatusChanged += s => { if (ReferenceEquals(s, _previewSession)) _channelState.Text = s.Display; };
        session.ScreenshotChanged += (s, png) => Dispatcher.BeginInvoke(() =>
        { if (ReferenceEquals(s, _previewSession) && !s.IsStopped) { _latestScreenshot = png; QueuePreview(); } });
        _sessions.Add(session); _connected.SelectedItem = session;
        try { session.Start(s => _ = DisconnectAsync(s)); if (session.IsStopped) throw new InvalidOperationException("目标窗口已关闭，请刷新窗口列表。"); }
        catch { _sessions.Remove(session); _ = session.StopAsync(); throw; }
        SetStatus($"已添加店铺「{binding.SellerId}」，共 {_sessions.Count} 个连接。可继续添加窗口，或收起设置显示全部助手。");
        return Task.CompletedTask;
    }
    private async Task DisconnectAsync(ReceptionSession session)
    {
        if (!_sessions.Contains(session)) return;
        await session.StopAsync();
        _sessions.Remove(session); if (ReferenceEquals(_previewSession, session)) { _connected.SelectedItem = _sessions.FirstOrDefault(); SelectConnection(); }
        if (!_closing) { SetStatus($"已断开店铺「{session.SellerId}」的窗口；其余 {_sessions.Count} 个连接继续运行。"); if (_sessions.Count == 0) ShowSettings(); }
    }
    private Task DisconnectAllAsync() => _disconnectAllTask is { IsCompleted: false } ? _disconnectAllTask : _disconnectAllTask = DisconnectAllCoreAsync();
    private async Task DisconnectAllCoreAsync()
    {
        _disconnectingAll = true;
        try
        {
            var sessions = _sessions.ToArray();
            await Task.WhenAll(sessions.Select(s => s.StopAsync()));
            foreach (var session in sessions) _sessions.Remove(session);
            SelectConnection();
        }
        finally { _disconnectingAll = false; }
        if (!_closing) SetStatus("全部窗口已断开，原有消息与回复已保留。");
    }
    private void RefreshProducts()
    {
        var selected = _products.SelectedItem as CatalogProduct;
        _refreshingProducts = true;
        try
        {
            var items = _store.Products(); _products.ItemsSource = items;
            _products.SelectedItem = items.SingleOrDefault(p => p.SellerId == selected?.SellerId && p.ProductId == selected?.ProductId);
        }
        finally { _refreshingProducts = false; }
        SelectAdviceProduct(_products.SelectedItem as CatalogProduct);
    }
    private void EditSelectedProduct()
    {
        if (_products.SelectedItem is CatalogProduct product) EditProduct(product);
    }
    private void EditProduct(CatalogProduct product)
    {
        _editing = product;
        _productEditor.Visibility = Visibility.Visible;
        _productEditorState.Text = product.ProductId.Length == 0 ? "正在新增商品" : $"正在编辑：{product.Name} · 修改后请重新核对并保存";
        foreach (var (name, field) in _fields) field.Text = typeof(CatalogProduct).GetProperty(name)!.GetValue(product)?.ToString() ?? "";
        _variants.Load(product.Variants.Select(v => new[] { v.Name, v.Price?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "", v.Details }));
        _productPromotions.Load(product.Promotions.Select(PromotionValues));
        _faqs.Load(product.Faqs.Select(f => new[] { f.Question, f.Answer }));
        _approved.IsChecked = product.Approved;
        _knowledgeScroll?.ScrollToTop();
    }
    private void SaveProduct()
    {
        string F(string name) => _fields[name].Text.Trim();
        var identity = _store.Business.Resolve(F("SellerId"));
        var product = new CatalogProduct { ShopId = identity.Id, SellerId = identity.BindingKey, ProductId = F("ProductId"), Name = F("Name"), SalesScope = F("SalesScope"), Material = F("Material"),
            Capacity = F("Capacity"), CurrentPrice = F("CurrentPrice"), ShippingTime = F("ShippingTime"), ReturnPolicy = F("ReturnPolicy"),
            CustomizationPolicy = F("CustomizationPolicy"), Notes = F("Notes"), Source = F("Source"), Approved = _approved.IsChecked == true,
            Variants = ReadVariants(), Promotions = ReadPromotions(_productPromotions),
            Faqs = _faqs.Values().Select(f => new ProductFaq { Question = f[0], Answer = f[1] }).ToArray() };
        ClientStore.Validate(product); var items = _store.Products();
        if (string.IsNullOrEmpty(_editing?.ProductId) && items.Any(item => item.SellerId == product.SellerId && item.ProductId == product.ProductId))
            throw new InvalidOperationException("该商品已存在，请在列表选中后点击“编辑选中商品”，避免覆盖已有资料。");
        if (_editing is not null && (_editing.SellerId != product.SellerId || _editing.ProductId != product.ProductId) && items.Any(item => item.SellerId == _editing.SellerId && item.ProductId == _editing.ProductId))
            throw new InvalidOperationException("已有商品的店铺和 ID 不可直接改写；请新增一条商品。");
        items.RemoveAll(item => item.SellerId == product.SellerId && item.ProductId == product.ProductId); items.Add(product);
        _store.SaveProducts(items); RefreshProducts();
        _products.SelectedItem = _products.Items.Cast<CatalogProduct>().Single(p => p.SellerId == product.SellerId && p.ProductId == product.ProductId);
        foreach (var field in _fields.Values) field.Clear();
        _variants.Load(Array.Empty<string[]>()); _faqs.Load(Array.Empty<string[]>()); _productPromotions.Load(Array.Empty<string[]>());
        _approved.IsChecked = false; _editing = null; _productEditor.Visibility = Visibility.Collapsed;
        _productEditorState.Text = $"已保存“{product.Name}”。继续录入请点击“新增商品”；修改请点击“编辑选中商品”。";
        _knowledgeScroll?.ScrollToTop();
        SetStatus(product.Approved ? "商品资料已保存并标记为已核对，录入区已收起。" : "商品草稿已保存，尚不用于 AI 回复，录入区已收起。");
        _store.Diagnostics.Write(DiagnosticLogLevel.Information, DiagnosticEvent.ProductSaved, new() { Count = 1, Success = true });
        _ = BeginAdviceAsync(product);
    }
    private void ImportJson()
    {
        var dialog = new OpenFileDialog { Filter = "商品 JSON|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        if (new FileInfo(dialog.FileName).Length > 2 * 1024 * 1024) throw new InvalidDataException("文件超过 2 MB。");
        var count = _store.Import(File.ReadAllText(dialog.FileName), "本地导入：" + Path.GetFileName(dialog.FileName));
        _store.Diagnostics.Write(DiagnosticLogLevel.Information, DiagnosticEvent.ProductsImported, new() { Count = count, Success = true });
        RefreshProducts(); SetStatus($"已导入 {count} 条商品，均为待核对状态。");
    }
    private void ImportNotes()
    {
        var dialog = new OpenFileDialog { Filter = "文本知识|*.txt;*.md" };
        if (dialog.ShowDialog(this) != true) return;
        if (new FileInfo(dialog.FileName).Length > 100000) throw new InvalidDataException("文档过长，请按商品拆分。");
        var text = File.ReadAllText(dialog.FileName); if (text.Length > 12000) throw new InvalidDataException("当前商品备注最多 12000 字。");
        _fields["Notes"].Text = text; _fields["Source"].Text = "本地文档：" + Path.GetFileName(dialog.FileName); _approved.IsChecked = false;
        SetStatus("文档已载入当前商品编辑区，请填写店铺、商品 ID，核对后保存。");
    }
    private void ExportJson()
    {
        var dialog = new SaveFileDialog { Filter = "商品 JSON|*.json", FileName = "qnagent-products.json" };
        if (dialog.ShowDialog(this) == true) { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_store.Products(), ClientStore.Json)); SetStatus("商品资料已导出。"); }
    }
}
