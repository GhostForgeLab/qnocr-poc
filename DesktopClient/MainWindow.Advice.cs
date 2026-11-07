using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QNAgent;

internal sealed partial class MainWindow
{
    private readonly TextBlock _adviceProductLabel = Text("保存商品后会自动检查，也可选中已保存商品重新检查。", 13);
    private readonly TextBlock _adviceStatus = Text("建议只用于完善资料，不会自动加入客服知识。", 12);
    private readonly StackPanel _adviceItems = new(), _adviceIgnored = new();
    private readonly Expander _ignoredAdviceExpander = new() { Header = "已忽略 / 不适用", Margin = new Thickness(0, 10, 0, 0) };
    private Button _adviceRefresh = null!;
    private CatalogProduct? _adviceProduct;
    private ScrollViewer? _knowledgeScroll;
    private CancellationTokenSource? _adviceCancellation;
    private Task _adviceTask = Task.CompletedTask;
    private string? _adviceRequestKey;
    private bool _refreshingProducts;

    private UIElement BuildAdviceLayout(StackPanel productPanel)
    {
        _knowledgeScroll = Scroll(productPanel);
        var advice = new StackPanel { Margin = new Thickness(16) };
        advice.Children.Add(Text("AI 资料补充建议", 21));
        advice.Children.Add(Text("只提出问题与补充方向，答案由你填写。保存商品后检查一次，不影响保存。", 12));
        advice.Children.Add(_adviceProductLabel); advice.Children.Add(_adviceStatus);
        _adviceRefresh = Button("重新检查已保存资料", () => _adviceProduct is { } p ? BeginAdviceAsync(p, true) : Task.CompletedTask);
        _adviceRefresh.IsEnabled = false; advice.Children.Add(_adviceRefresh); advice.Children.Add(_adviceItems);
        _ignoredAdviceExpander.Content = _adviceIgnored; advice.Children.Add(_ignoredAdviceExpander);
        var grid = new Grid { Background = Background };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        grid.Children.Add(_knowledgeScroll);
        var sidebar = new Border { Background = Brushes.White, BorderBrush = Brush("#DCE3ED"), BorderThickness = new Thickness(1, 0, 0, 0), Child = Scroll(advice) };
        Grid.SetColumn(sidebar, 1); grid.Children.Add(sidebar);
        grid.SizeChanged += (_, _) => grid.ColumnDefinitions[1].Width = new GridLength(grid.ActualWidth < 900 ? 290 : 340);
        return grid;
    }
    private void CancelAdvice()
    {
        var source = _adviceCancellation; _adviceCancellation = null; _adviceRequestKey = null;
        try { source?.Cancel(); } catch (ObjectDisposedException) { }
    }
    private void SelectAdviceProduct(CatalogProduct? product)
    {
        var same = product is not null && _adviceProduct is not null && KnowledgeAdviceService.ProductKey(product) == KnowledgeAdviceService.ProductKey(_adviceProduct);
        if (!same) CancelAdvice();
        _adviceProduct = product; _adviceItems.Children.Clear(); _adviceIgnored.Children.Clear();
        _adviceRefresh.IsEnabled = product is not null;
        if (product is null)
        {
            _adviceProductLabel.Text = "保存商品后会自动检查，也可选中已保存商品重新检查。";
            _adviceStatus.Text = "尚未选中商品。"; _ignoredAdviceExpander.Header = "已忽略 / 不适用"; return;
        }
        _adviceProductLabel.Text = $"{product.Name}\n{product.SellerId} · {product.ProductId}";
        try
        {
            RenderAdvice(product, _adviceService.Load(product));
            if (_adviceRequestKey == RequestKey(product)) { _adviceStatus.Text = "正在检查已保存资料…"; _adviceRefresh.IsEnabled = false; }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { _adviceStatus.Text = "建议暂未载入：" + Friendly(error); }
    }
    private string RequestKey(CatalogProduct product) => KnowledgeAdviceService.ProductKey(product) + ":" + _adviceService.Fingerprint(product, _store.Settings());
    private Task BeginAdviceAsync(CatalogProduct product, bool force = false)
    {
        if (_closing) return Task.CompletedTask;
        try { return BeginAdviceCore(product, force); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { _store.Diagnostics.Write(DiagnosticLogLevel.Error, DiagnosticEvent.AdviceFailed, error: error); _adviceStatus.Text = "商品已保存，建议检查未启动：" + Friendly(error); return Task.CompletedTask; }
    }
    private Task BeginAdviceCore(CatalogProduct product, bool force)
    {
        // Read the saved version, never the partially edited form.
        var saved = _store.Products().SingleOrDefault(p => p.SellerId == product.SellerId && p.ProductId == product.ProductId);
        if (saved is null) return Task.CompletedTask;
        var key = RequestKey(saved);
        if (_adviceRequestKey == key && !_adviceTask.IsCompleted) return _adviceTask;
        CancelAdvice(); _adviceProduct = saved; _adviceProductLabel.Text = $"{saved.Name}\n{saved.SellerId} · {saved.ProductId}";
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _adviceCancellation = source; _adviceRequestKey = key;
        _adviceStatus.Text = "正在检查已保存资料，商品已保存成功…"; _adviceItems.Children.Clear(); _adviceRefresh.IsEnabled = false;
        return _adviceTask = CheckAdviceAsync(saved, key, source, force);
    }
    private async Task CheckAdviceAsync(CatalogProduct product, string key, CancellationTokenSource source, bool force)
    {
        try
        {
            var state = await _adviceService.CheckAsync(product, _store.Settings(), force, source.Token);
            if (!_closing && !source.IsCancellationRequested && _adviceRequestKey == key && _adviceProduct is not null &&
                KnowledgeAdviceService.ProductKey(product) == KnowledgeAdviceService.ProductKey(_adviceProduct)) RenderAdvice(product, state);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _store.Diagnostics.Write(DiagnosticLogLevel.Error, DiagnosticEvent.AdviceFailed, error: error);
            if (!_closing && _adviceRequestKey == key) _adviceStatus.Text = "商品已保存，建议检查未完成：" + Friendly(error) + " 可点击重新检查。";
        }
        finally
        {
            if (ReferenceEquals(_adviceCancellation, source))
            { _adviceCancellation = null; _adviceRequestKey = null; if (!_closing) _adviceRefresh.IsEnabled = _adviceProduct is not null; }
            source.Dispose();
        }
    }
    private void RenderAdvice(CatalogProduct product, KnowledgeAdviceState state)
    {
        _adviceItems.Children.Clear(); _adviceIgnored.Children.Clear();
        var stale = state.ContentHash != _adviceService.Fingerprint(product, _store.Settings());
        var visible = state.Suggestions.Where(s => state.Ignored.All(i => i.Id != s.Id &&
            KnowledgeAdviceService.NormalizeQuestion(i.Question) != KnowledgeAdviceService.NormalizeQuestion(s.Question))).ToArray();
        _adviceStatus.Text = state.GeneratedAt == default ? "尚未检查这个商品，保存后会自动生成建议。"
            : stale ? "商品、店铺优惠或模型设置已更新，请重新检查后使用建议。"
            : visible.Length == 0 ? "本次没有待显示的补充建议。仍请按实际商品情况核对。"
            : $"{state.GeneratedAt:MM-dd HH:mm} · {visible.Length} 条待补充建议\n针对已保存版本；修改后请保存再检查。";
        foreach (var suggestion in visible)
        {
            var card = new StackPanel();
            card.Children.Add(Text($"{suggestion.Category} · {suggestion.Priority} · {suggestion.Status}", 12));
            card.Children.Add(Text(suggestion.Question, 15));
            card.Children.Add(Text("缺口：" + suggestion.Reason, 12));
            card.Children.Add(Text("建议：" + suggestion.SuggestedAction, 12));
            card.Children.Add(Text("填写位置：" + KnowledgeAdviceService.Targets[suggestion.Target], 12));
            var buttons = new WrapPanel();
            var jump = AdviceButton("去填写", () => ApplyAdvice(product, state.ContentHash, suggestion, false));
            jump.ToolTip = "跳到“" + KnowledgeAdviceService.Targets[suggestion.Target] + "”，不自动填写内容";
            var faq = AdviceButton("加入问答", () => ApplyAdvice(product, state.ContentHash, suggestion, true));
            faq.ToolTip = "只加入问题，答案留空，由你填写";
            jump.IsEnabled = faq.IsEnabled = !stale; buttons.Children.Add(jump); buttons.Children.Add(faq);
            var ignore = AdviceButton("忽略", () => RenderAdvice(product, _adviceService.Ignore(product, suggestion)));
            ignore.ToolTip = "忽略或标记为不适用，以后不再提醒；可以在下方恢复"; buttons.Children.Add(ignore);
            card.Children.Add(buttons);
            _adviceItems.Children.Add(new Border { Child = card, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 10),
                Background = Brush("#F3F6FA"), BorderBrush = Brush("#DCE3ED"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5) });
        }
        foreach (var ignored in state.Ignored)
        {
            _adviceIgnored.Children.Add(Text(ignored.Question, 12));
            _adviceIgnored.Children.Add(Button("恢复此项", () => RenderAdvice(product, _adviceService.Restore(product, ignored.Id))));
        }
        _ignoredAdviceExpander.Header = $"已忽略 / 不适用（{state.Ignored.Length}）";
    }
    private Button AdviceButton(string label, Action action)
    {
        var button = Button(label, action); button.Padding = new Thickness(8, 5, 8, 5);
        button.Margin = new Thickness(0, 4, 5, 0); button.FontSize = 12; return button;
    }
    private bool HasProductEdits()
    {
        if (_productEditor.Visibility != Visibility.Visible || _editing is null) return false;
        if (_fields.Any(f => f.Value.Text.Trim() != (typeof(CatalogProduct).GetProperty(f.Key)!.GetValue(_editing)?.ToString() ?? "").Trim()) ||
            (_approved.IsChecked == true) != _editing.Approved) return true;
        bool Same(object a, object b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
        return !Same(_variants.Values(), _editing.Variants.Select(v => new[] { v.Name, v.Price?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "", v.Details }).ToArray()) ||
            !Same(_faqs.Values(), _editing.Faqs.Select(f => new[] { f.Question, f.Answer }).ToArray()) ||
            !Same(_productPromotions.Values(), _editing.Promotions.Select(PromotionValues).ToArray());
    }
    private void ApplyAdvice(CatalogProduct product, string hash, KnowledgeSuggestion suggestion, bool addFaq)
    {
        if (_adviceProduct is null || KnowledgeAdviceService.ProductKey(product) != KnowledgeAdviceService.ProductKey(_adviceProduct))
            throw new InvalidOperationException("已切换商品，请使用当前商品的建议。");
        var current = _store.Products().Single(p => p.SellerId == product.SellerId && p.ProductId == product.ProductId);
        if (_adviceService.Fingerprint(current, _store.Settings()) != hash) throw new InvalidOperationException("资料已更新，请重新检查后使用建议。");
        if (_editing is not null && (_editing.SellerId != current.SellerId || _editing.ProductId != current.ProductId) && HasProductEdits())
            throw new InvalidOperationException("另一个商品有未保存的编辑，请先保存后再使用这条建议。");
        if (_editing?.SellerId != current.SellerId || _editing.ProductId != current.ProductId || _productEditor.Visibility != Visibility.Visible) EditProduct(current);
        FrameworkElement target;
        if (addFaq)
        {
            if (_faqs.Values().Any(f => KnowledgeAdviceService.NormalizeQuestion(f[0]) == KnowledgeAdviceService.NormalizeQuestion(suggestion.Question)))
            { target = _faqs; _adviceStatus.Text = "编辑区已有这个问题，请核对已有问答，未重复添加。"; }
            else
            {
                var inputs = _faqs.Add(suggestion.Question, ""); target = inputs[1];
                _adviceStatus.Text = "问题已加入编辑区，答案留空，请你填写后核对并保存。";
            }
        }
        else
        {
            target = suggestion.Target switch
            {
                "Variants" => _variants, "Promotions" => _productPromotions, "Faqs" => _faqs,
                "ShopPromotions" => _shopSeller, _ => _fields[suggestion.Target]
            };
            _adviceStatus.Text = "已定位到“" + KnowledgeAdviceService.Targets[suggestion.Target] + "”。请按实际情况补充，AI 未填写任何答案。";
            if (suggestion.Target == "ShopPromotions") _adviceStatus.Text += "请核对店铺标识后载入对应店铺优惠。";
        }
        _ = Dispatcher.BeginInvoke(() => { target.BringIntoView(); target.Focus(); });
    }
}
