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

internal sealed partial class ReceptionSession : System.ComponentModel.INotifyPropertyChanged
{
    private readonly ClientStore _store, _shared;
    private readonly QianniuConnection _connection;
    private readonly ReplyService _replies;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly ObservableCollection<InboxMessage> _messages = new();
    private long _messageSequence;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _operations = new();
    private readonly Button _generate;
    private ConversationSwitchSignal? _switchSignal;
    private Task? _captureTask, _stopTask;
    private bool _stopping;
    private InboxMessage? _selected;
    private AssistedReplyView? _view;
    private byte[]? _latestScreenshot;
    public QianniuWindow Target { get; }
    public WindowBinding Binding { get; }
    public string ScopeId => Binding.ScopeId;
    public string SellerId => Binding.SellerId;
    public string Status { get; private set; } = "等待连接";
    public string Display => $"{SellerId} · {Target.WindowTitle}\n{Status}";
    public byte[]? LastScreenshot => Volatile.Read(ref _latestScreenshot);
    public ConversationWindow Panel => _conversationWindow;
    public bool IsStopped => _stopping;
    public string StateRoot => _store.Root;
    private ClientSettings Settings => _shared.Settings() with { SellerId = SellerId };
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public event Action<ReceptionSession, byte[]>? ScreenshotChanged;
    public event Action<ReceptionSession>? StatusChanged;
    private readonly ListBox _inbox = new();
    private readonly TextBlock _question = new() { TextWrapping = TextWrapping.Wrap, FontSize = 18 };
    private readonly System.Windows.Controls.Image _messageImage = new() { MaxHeight = 150, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private readonly TextBlock _visualNote = Text("", 12);
    private readonly Dictionary<string, byte[]> _messagePreviews = new(StringComparer.Ordinal);
    private readonly TextBlock _replyState = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _reply = Input(multiline: true);
    private readonly ComboBox _replyProduct = new();
    private readonly ConversationFocus _focus = new();
    private readonly Dictionary<string, string> _sessionProducts = new(StringComparer.Ordinal);
    private readonly TextBlock _buyerTitle = Text("等待当前买家", 20);
    private readonly TextBlock _conversationDetail = Text("连接千牛后，在这里显示当前会话", 12);
    private readonly TextBlock _floatingStatus = Text("", 12);
    private readonly StackPanel _replyPanel = new();
    private ConversationWindow _conversationWindow = null!;

    public ReceptionSession(ClientStore shared, ClientStore state, WindowBinding binding, QianniuWindow target,
        ResourceDictionary resources, Action<ReceptionSession> settings, Action<ReceptionSession> disconnect, IJevGateway? jev = null, bool autoGenerate = true, TimeProvider? contextTimeProvider = null)
    {
        if (!Guid.TryParseExact(binding.ScopeId, "N", out _) ||
            !string.Equals(state.Root, Path.GetFullPath(Path.Combine(shared.Root, "windows", binding.ScopeId)), StringComparison.OrdinalIgnoreCase) ||
            binding.WindowTitle != target.WindowTitle)
            throw new InvalidDataException("窗口与独立数据目录的绑定不一致。");
        _shared = shared; _store = state; Binding = binding; Target = target;
        _connection = new QianniuConnection(state, ScopeId, contextTimeProvider); _replies = new ReplyService(state, ScopeId, shared, jev);
        _automatic.IsChecked = autoGenerate;
        _generate = Button("生成回复建议", GenerateAsync);
        _conversationWindow = new ConversationWindow(BuildWorkspace(), () => settings(this), () => disconnect(this), "断开");
        _conversationWindow.Title = "QN Agent · " + SellerId + " · 会话助手";
        _conversationWindow.Resources.MergedDictionaries.Add(resources);
        foreach (var message in state.Messages().Where(m => m.WindowScopeId == ScopeId && m.SellerId == SellerId).OrderByDescending(m => m.Timestamp)) _messages.Add(message);
        _messageSequence = _messages.Select(m => m.Sequence).DefaultIfEmpty().Max();
        _inbox.SelectionChanged += (_, _) =>
        {
            if (_refreshingSelection) return;
            if (_inbox.SelectedItem is InboxMessage { Role: "buyer" }) SelectMessage();
            else { _refreshingSelection = true; _inbox.SelectedItem = _selected; _refreshingSelection = false; }
        };
        _replyProduct.SelectionChanged += (_, _) => ProductSelectionChanged();
        _automatic.Checked += (_, _) => QueueAutoGeneration();
        _automatic.Unchecked += (_, _) => { CancelAutoDelay(); };
        _connection.Status += text => Post(() => SetStatus(text));
        _connection.Conversation += observation => Post(() =>
        { if (_connection.IsCurrent(observation)) ObserveConversation(observation); });
        _connection.MessagePreview += (id, png) => Post(() =>
        {
            _messagePreviews[id] = png;
            while (_messagePreviews.Count > 40 || _messagePreviews.Sum(p => p.Value.Length) > 8 * 1024 * 1024)
                _messagePreviews.Remove(_messagePreviews.Keys.First());
            if (_selected?.MessageId == id) ShowMessagePreview();
        });
        _connection.Screenshot += png =>
        {
            Interlocked.Exchange(ref _latestScreenshot, png);
            ScreenshotChanged?.Invoke(this, png);
        };
        _connection.Message += message => Post(() => AcceptMessage(message));

    }
    private void Post(Action action) => _dispatcher.BeginInvoke(() => { if (!_stopping) action(); });
    internal bool AcceptMessage(InboxMessage message)
    {
        _dispatcher.VerifyAccess();
        if (_stopping || message.WindowScopeId != ScopeId || message.SellerId != SellerId || message.Role is not ("buyer" or "seller")) return false;
        var existing = _messages.FirstOrDefault(item => item.MessageId == message.MessageId);
        if (existing is not null)
        {
            if (existing.Buyer != message.Buyer || existing.SellerId != message.SellerId || existing.WindowScopeId != message.WindowScopeId || existing.Role != message.Role) return false;
            if (existing.ContentKind == message.ContentKind && existing.SessionId == message.SessionId) return true;
            if (existing.SessionId != message.SessionId)
            {
                if (_sessionProducts.TryGetValue(existing.SessionId, out var productId)) _sessionProducts[message.SessionId] = productId;
                // Same visible question may move to a fresh session; keep any displayed draft.
            }
            _messages[_messages.IndexOf(existing)] = message;
        }
        else
        {
            if (message.Sequence == 0) message = message with { Sequence = ++_messageSequence };
            else _messageSequence = Math.Max(_messageSequence, message.Sequence);
            _messages.Insert(0, message);
            if (message.Role == "buyer" && !message.Baseline) _autoEligible.Add(message.MessageId);
            if (message.Role == "seller" && _focus.Contains(message))
            {
                CollectLearning(message);
                var afterCurrent = _selected is not null && (message.Timestamp > _selected.Timestamp ||
                    message.Timestamp == _selected.Timestamp && message.Sequence > _selected.Sequence);
                if (!message.Baseline || afterCurrent)
                {
                    _dialogueRevision++; CancelGeneration();
                    if (_selected is not null && afterCurrent) _autoEligible.Remove(_selected.MessageId);
                }
                SetStatus("已记录卖家回复，仅更新上下文，保留当前草稿。");
            }
        }
        while (_messages.Count > 2000) { _autoEligible.Remove(_messages[^1].MessageId); _messages.RemoveAt(_messages.Count - 1); }
        _store.SaveMessages(_messages.Reverse()); return true;
    }
    public void Start(Action<ReceptionSession> targetLost)
    {
        _dispatcher.VerifyAccess();
        if (_captureTask is not null || _stopping) throw new InvalidOperationException("这个窗口连接已经启动或停止。");
        ObserveConversation(new(SellerId, null, "正在确认当前买家…"));
        _conversationWindow.Attach(Target, () => targetLost(this));
        if (_stopping) return;
        _switchSignal = new ConversationSwitchSignal(Target.Hwnd, () => _connection.BuyerListBounds, _connection.NotifyConversationClick);
        var settings = Settings; var token = _lifetime.Token;
        _captureTask = Task.Run(async () =>
        {
            try { await _connection.RunAsync(Target, settings, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            { _shared.Diagnostics.Write(DiagnosticLogLevel.Error, DiagnosticEvent.CaptureFailed, new() { WindowId = ScopeId }, error); Post(() => SetStatus("连接中断：" + error.Message)); }
            finally { Post(() => { _switchSignal?.Dispose(); _switchSignal = null; ObserveConversation(new(SellerId, null, "采集已停止，请断开此窗口后重新添加")); SetStatus("采集已停止"); }); }
        });
        SetStatus("已连接，等待当前买家识别");
        _shared.Diagnostics.Write(DiagnosticLogLevel.Information, DiagnosticEvent.WindowConnected, new() { WindowId = ScopeId, Success = true });
    }
    public void Reveal() { if (!_stopping) _conversationWindow.Reveal(); }
    public Task StopAsync() { _dispatcher.VerifyAccess(); return _stopTask ??= StopCoreAsync(); }
    private async Task StopCoreAsync()
    {
        SetStatus("正在断开…");
        _stopping = true; _lifetime.Cancel(); _switchSignal?.Dispose(); _switchSignal = null;
        _shared.Diagnostics.Write(DiagnosticLogLevel.Information, DiagnosticEvent.WindowDisconnected, new() { WindowId = ScopeId });
        _conversationWindow.Detach();
        if (_captureTask is not null) { try { await _captureTask; } catch (OperationCanceledException) { } }
        try { await Task.WhenAll(_operations.ToArray()); } catch (Exception error) when (error is not OutOfMemoryException) { }
        _conversationWindow.Exit(); _connection.Dispose(); _lifetime.Dispose();
        _messagePreviews.Clear(); _latestScreenshot = null;
    }
    private void SetStatus(string text)
    {
        if (_stopping) return;
        Status = text; _floatingStatus.Text = text;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Display)));
        StatusChanged?.Invoke(this);
    }
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static BitmapImage DecodePreview(byte[] png, int width = 0)
    {
        using var stream = new MemoryStream(png, writable: false);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        if (width > 0) image.DecodePixelWidth = width;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
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
            if (_stopping) return;
            button.IsEnabled = false;
            Task? operation = null;
            try { operation = action(); _operations.Add(operation); await operation; }
            catch (OperationCanceledException) { SetStatus("操作已取消。"); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                _shared.Diagnostics.Write(DiagnosticLogLevel.Error, DiagnosticEvent.UiOperationFailed,
                    new() { WindowId = ScopeId, SessionId = _selected?.SessionId, MessageId = _selected?.MessageId }, error);
                SetStatus("操作未完成：" + Friendly(error));
            }
            finally { if (operation is not null) _operations.Remove(operation); if (!_stopping) button.IsEnabled = true; }
        };
        return button;
    }
    private Button Button(string text, Action action) => Button(text, () => { action(); return Task.CompletedTask; });
    private static string Friendly(Exception error) => error is System.Net.Http.HttpRequestException ? "网络连接失败，请检查服务地址与网络。" : error is JsonException ? "返回内容不是支持的 JSON 格式，请检查接口或导入文件。" : error.Message;
    internal void ObserveConversation(ObservedConversation observation)
    {
        if (_stopping || observation.SellerId != SellerId) return;
        if (observation.Buyer is null && observation.PreserveDisplay && _selected is not null)
        {
            _conversationPending = true; CancelAutoDelay(); _replyPanel.IsEnabled = false;
            _conversationDetail.Text = SellerId + " · 正在更新对话上下文…";
            return;
        }
        _conversationPending = false;
        _focus.Remember(_selected, _reply.Text);
        _focus.Observe(observation);
        _buyerTitle.Text = observation.Buyer ?? "等待当前买家";
        _conversationDetail.Text = SellerId + " · " + observation.Detail;
        var messages = _focus.Messages(_messages);
        var next = messages.FirstOrDefault(m => m.Role == "buyer") ?? (_selected is not null && _focus.Contains(_selected) ? _selected : null);
        var changed = _selected?.MessageId != next?.MessageId || _selected?.SessionId != next?.SessionId || _selected?.ContentKind != next?.ContentKind;
        _refreshingSelection = true;
        try { _inbox.ItemsSource = messages.Reverse().ToArray(); _inbox.SelectedItem = next; }
        finally { _refreshingSelection = false; }
        if (changed || next is null) SelectMessage();
        QueueAutoGeneration();
        _replyPanel.IsEnabled = _selected is not null && !_conversationPending;
    }

    private UIElement BuildWorkspace()
    {
        var panel = new DockPanel { Margin = new Thickness(14) };
        var heading = new StackPanel(); heading.Children.Add(_buyerTitle); heading.Children.Add(_conversationDetail);
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        _replyPanel.IsEnabled = false;
        _question.FontSize = 15;
        _question.Text = "在千牛打开聊天会话后，这里显示当前买家的问题。";
        _replyPanel.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12), Child = _question, Margin = new Thickness(0, 0, 0, 8) });
        _replyPanel.Children.Add(_messageImage); _replyPanel.Children.Add(_visualNote);
        _replyPanel.Children.Add(BuildIntentCard());
        _inbox.BorderBrush = Brush("#DCE3ED"); _inbox.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var template = new DataTemplate(); var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DisplayText"));
        factory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); factory.SetValue(FrameworkElement.MarginProperty, new Thickness(8));
        template.VisualTree = factory; _inbox.ItemTemplate = template; _inbox.MaxHeight = 150;
        _replyPanel.Children.Add(new Expander { Header = "最近 10 条对话（买家 / 卖家）", Content = _inbox, Margin = new Thickness(0, 4, 0, 8) });
        Field(_replyPanel, "对应商品 · 请核对", _replyProduct);
        _replyPanel.Children.Add(Button("识别当前商品", () => RecognizeProductWithRetryAsync()));
        _replyPanel.Children.Add(_automatic);
        _generate.HorizontalAlignment = HorizontalAlignment.Stretch; _generate.Margin = new Thickness(0, 12, 0, 8);
        _replyPanel.Children.Add(_generate);
        _replyState.FontSize = 12; _replyPanel.Children.Add(_replyState);
        _replyPanel.Children.Add(_callStatus);
        _reply.MinHeight = 145; _reply.MaxHeight = 260; _reply.FontSize = 15;
        Field(_replyPanel, "回复建议 · 可直接修改", _reply);
        var actions = new WrapPanel();
        actions.Children.Add(Button("保存草稿", () =>
        {
            RequireView(); var saved = _replies.SaveDraft(_selected!, _view!.ReplyPreviewId, _reply.Text);
            ShowReply(saved);
        }));
        actions.Children.Add(Button("确认并复制", () =>
        {
            RequireView();
            try
            {
                var copied = _replies.ConfirmAndCopy(_selected!, _view!.ReplyPreviewId, _reply.Text, new WindowsAssistantClipboard());
                ShowReply(copied);
            }
            catch
            {
                // Approval (or a new edited revision) can persist even when the clipboard is busy.
                if (_replies.Latest(_selected!) is { } latest) ShowReply(latest);
                throw;
            }
            SetStatus("已复制，请在千牛核对买家后粘贴发送。");
        }));
        actions.SetBinding(UIElement.IsEnabledProperty, new System.Windows.Data.Binding("IsEnabled") { Source = _replyPanel });
        var footer = new StackPanel(); footer.Children.Add(actions);
        footer.Children.Add(Text("确认后复制到千牛，由你发送。", 12));
        _floatingStatus.Foreground = Brush("#65758A"); _floatingStatus.MaxHeight = 38; footer.Children.Add(_floatingStatus);
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        panel.Children.Add(Scroll(_replyPanel));
        return panel;
    }
    private void SelectMessage()
    {
        _focus.Remember(_selected, _reply.Text);
        if (_selected is not null && _replyProduct.SelectedItem is CatalogProduct oldProduct)
            _sessionProducts[_selected.SessionId] = oldProduct.ProductId;
        CancelGeneration();
        _selected = _inbox.SelectedItem as InboxMessage; _view = null; _reply.Clear(); _callStatus.Text = "";
        ResetIntentCard();
        _messageImage.Source = null; _messageImage.Visibility = Visibility.Collapsed; _visualNote.Text = "";
        _refreshingSelection = true;
        _replyProduct.ItemsSource = null;
        if (_selected is null || !_focus.Contains(_selected))
        { _selected = null; _question.Text = "等待当前买家的消息…"; _replyState.Text = ""; _replyPanel.IsEnabled = false; _refreshingSelection = false; return; }
        _replyPanel.IsEnabled = true;
        _question.Text = _selected.Text;
        ShowMessagePreview();
        _replyState.Text = _selected.Baseline ? "当前可见消息 · 点击生成建议" : "新消息 · 点击生成建议";
        var products = _shared.Products().Where(product => product.SellerId == _selected.SellerId).ToArray();
        var productId = _selected.ProductId.Length > 0 ? _selected.ProductId
            : _sessionProducts.TryGetValue(_selected.SessionId, out var manual) ? manual : "";
        _replyProduct.ItemsSource = products; _replyProduct.SelectedItem = products.SingleOrDefault(product => product.ProductId == productId);
        _refreshingSelection = false;
        var latest = _replies.Latest(_selected);
        if (latest is not null && latest.CurrentProductId == (_replyProduct.SelectedItem as CatalogProduct)?.ProductId)
            ShowReply(latest);
        if (_focus.Draft(_selected) is { } draft) _reply.Text = draft;
        QueueAutoGeneration();
    }
    private void ShowMessagePreview()
    {
        if (_selected is null) return;
        if (_messagePreviews.TryGetValue(_selected.MessageId, out var png))
        { _messageImage.Source = DecodePreview(png); _messageImage.Visibility = Visibility.Visible; }
        _visualNote.Text = _selected.ContentKind == "Unrecognized" ? "消息图像预览 · 内容尚未识别，请人工核对"
            : _selected.ContentKind == "Mixed" ? "文字中包含图像/表情，请结合预览核对" : "";
        _generate.Content = _selected.ContentKind == "Unrecognized" ? "生成核实回复" : "重新生成回复建议";
    }
    private Task GenerateAsync() => GenerateCurrentAsync();
    private void RequireView()
    {
        if (_selected is null || !_focus.Contains(_selected)) throw new InvalidOperationException("当前买家未确认，请等待识别完成。");
        if (_conversationPending) throw new InvalidOperationException("正在确认对话变化，请稍候再操作。");
        if (_view is null) throw new InvalidOperationException("请先生成回复建议。");
        if (string.IsNullOrWhiteSpace(_reply.Text)) throw new InvalidOperationException("尚未生成有效建议，请重试或填写人工回复。");
    }
    private void ShowReply(AssistedReplyView view)
    {
        _view = view;
        ShowIntentForReply(view);
        _reply.Text = IsServiceFailure(view) && view.EditedReply is null ? "" : ReplyTextPolicy.Normalize(view.EffectiveReply);
        ShowCallStatus(view);
        var assessment = view.KnowledgeCategory switch
        {
            "JEV_UNAVAILABLE" => "JEV 未完成 · 请检查 AI 设置或重试",
            "REPLY_UNAVAILABLE" => "回复模型请求失败 · 请检查服务或重试",
            "REVIEW_DRAFT" => "回复草稿 · 请人工核对细节后复制",
            "SOCIAL_REPLY" => "当前为确认或寒暄 · 简短回应即可",
            "VERIFY_UNAVAILABLE" => "回复核验未完成 · 请人工核实",
            "UNSUPPORTED_REPLY" or "STYLE_REJECTED" => "原建议未通过检查 · 已改为核实回复",
            "CLARIFY_BUYER" => "JEV：需要补充条件 · 已生成追问",
            "PARTIAL" => "JEV：部分资料足够 · 其余需追问或核实",
            "IDENTITY_CLARIFICATION" => "JEV：买家可能误认商品 · 已澄清售卖内容",
            "HUMAN_CHECK" => "已结合现有资料回复 · 不确定部分需核实",
            "ANSWER" => "JEV 意图与资料判定完成 · 回复依据已复核",
            "ANSWER_OPTIONS" => "已按款式整理资料 · 请核对后复制",
            "EVIDENCE_FIRST" => "已结合现有资料生成 · 回复依据已复核",
            "DIRECT_FACTS" => "已引用商品资料原文 · 请核对后复制",
            _ => ""
        };
        _replyState.Text = view.Status == "Copied" ? "已复制回复 · 可再次点击复制"
            : view.Status == "Approved" ? "已确认，尚未复制成功 · 请再次点击复制"
            : assessment.Length > 0 ? assessment : view.KnowledgeGuardApplied ? "缺少必要商品资料 · 请人工核实"
            : "建议已生成 · 请核对后复制";
        if (IsServiceFailure(view) && view.EditedReply is null && _replies.ReadAudit(view)?.Calls is { } calls)
            _replyState.Text = (calls.ReplyFailureMessage ?? assessment) + " 尚未生成建议，可点击重试。";
    }
}
