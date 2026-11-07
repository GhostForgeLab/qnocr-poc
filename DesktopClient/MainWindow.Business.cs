using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace QNAgent;

internal sealed partial class MainWindow
{
    private UIElement _businessPage = null!, _learningPage = null!;
    private readonly ListBox _shopList = new() { Height = 150, DisplayMemberPath = "DisplayName" };
    private readonly TextBox _shopName = Input(), _shopId = Input();
    private readonly TextBlock _restoreSummary = Text("选择业务备份后先预览，再决定是否恢复。"), _learningSummary = Text("");
    private readonly CheckBox _overwriteBusiness = new() { Content = "用备份覆盖同 ID 的冲突资料（默认保留本机资料）" };
    private BusinessRestorePlan? _restorePlan;
    private readonly CheckBox _collectLearning = new() { Content = new TextBlock { Text = "自动收集人工回复差异，并调用 AI 设置中的模型分析（需人工确认后生效）", TextWrapping = TextWrapping.Wrap } };
    private readonly StackPanel _learningItems = new(), _learnedItems = new();
    private bool _refreshingLearning;
    private Task _manualLearningTask = Task.CompletedTask;
    private UIElement BuildBusiness()
    {
        var panel = Stack(); panel.Children.Add(Text("店铺与业务备份", 28));
        panel.Children.Add(Text("固定 ID 跟随店铺资料迁移，改名不会改变 ID。换电脑先恢复备份，再选择店铺连接千牛，不要重新新建同一个店铺。"));
        panel.Children.Add(_shopList); Field(panel, "店铺显示名称", _shopName);
        _shopId.IsReadOnly = true; Field(panel, "固定店铺 ID（不能修改）", _shopId);
        _shopList.SelectionChanged += (_, _) => { if (_shopList.SelectedItem is ShopIdentity s) { _shopName.Text = s.DisplayName; _shopId.Text = s.Id; } };
        var shopActions = new WrapPanel();
        shopActions.Children.Add(Button("新建店铺", () => { var shop = _store.Business.CreateShop(_shopName.Text); RefreshShops(shop.Id); SetStatus("店铺已创建。请选择“用于连接 / 新增商品”。"); }));
        shopActions.Children.Add(Button("保存显示名称", () => { var shop = SelectedShop(); _store.Business.Rename(shop.Id, _shopName.Text); RefreshShops(shop.Id); SetStatus("名称已修改，固定 ID 和原资料保留。"); }));
        shopActions.Children.Add(Button("复制固定 ID", () => Clipboard.SetText(SelectedShop().Id)));
        shopActions.Children.Add(Button("用于连接 / 新增商品", () =>
        {
            var shop = SelectedShop(); _seller.Text = shop.Id;
            _settings = _store.Settings() with { SellerId = shop.Id }; _store.SaveSettings(_settings);
            SetStatus($"已选择「{shop.DisplayName}」。连接和新增商品将使用这个店铺；现有编辑内容未改动。");
        })); panel.Children.Add(shopActions);
        panel.Children.Add(Text("业务资料备份 / 恢复", 22));
        panel.Children.Add(Text("包含：店铺 ID、商品、店铺优惠、资料补充建议及忽略记录、学习建议和已确认经验。\n不包含：API 密钥、AI 连接设置、窗口绑定、原始聊天记录和日志。学习建议含去标识后的问题与人工回复片段，备份属于你的私有业务资料，请妥善保管。"));
        panel.Children.Add(Button("导出业务备份…", () =>
        {
            var dialog = new SaveFileDialog { Filter = "业务备份 (*.qnbak)|*.qnbak", FileName = "QNAgent-业务备份-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".qnbak" };
            if (dialog.ShowDialog(this) == true) { BusinessBackupService.Export(_store, dialog.FileName); SetStatus("业务备份已导出（不含密钥）：" + dialog.FileName); }
        }));
        panel.Children.Add(Button("选择备份并预览…", async () =>
        {
            EnsureCanRestore(); CancelAdvice(); await _adviceTask; await _manualLearningTask;
            var dialog = new OpenFileDialog { Filter = "业务备份 (*.qnbak)|*.qnbak" };
            if (dialog.ShowDialog(this) != true) return;
            _restorePlan = null;
            var plan = BusinessBackupService.Preview(_store, dialog.FileName); _restorePlan = plan; _overwriteBusiness.IsChecked = false;
            _restoreSummary.Text = $"预览：新增店铺 {plan.NewShops}，新增商品 {plan.NewProducts}，商品冲突 {plan.ConflictingProducts}。\n备份含学习建议 {plan.Incoming.Candidates.Length}、已确认经验 {plan.Incoming.Rules.Length} 条。" +
                (plan.RenamedBindings > 0 ? $"\n{plan.RenamedBindings} 个同名旧标识属于不同固定 ID，将作为独立店铺导入。" : "") +
                "\n恢复前会自动备份本机业务资料；本机密钥和连接设置保持不变。未保存的表单编辑会在恢复后清空，请先保存。";
        }));
        panel.Children.Add(_restoreSummary); panel.Children.Add(_overwriteBusiness);
        panel.Children.Add(Button("按以上预览恢复", () =>
        {
            EnsureCanRestore(); var plan = _restorePlan ?? throw new InvalidOperationException("请先选择备份并预览。");
            var rollback = BusinessBackupService.Restore(_store, plan, _overwriteBusiness.IsChecked == true); _restorePlan = null;
            _editing = null; _productEditor.Visibility = Visibility.Collapsed; _products.SelectedItem = null;
            _loadedShopSeller = null; _shopPromotions.Load(Array.Empty<string[]>()); _shopPromotions.IsEnabled = false;
            RefreshProducts(); RefreshShops(); RefreshLearning();
            _restoreSummary.Text = "恢复完成。请选择店铺，然后在千牛连接页重新绑定本机窗口。新电脑还需填写 AI / JEV 密钥。";
            SetStatus("恢复完成。恢复前备份保存在：" + rollback);
        }));
        panel.Children.Add(Text("重装系统前，请把 .qnbak 复制到另一块磁盘、U 盘或你自己的备份空间。仅把备份留在将被格式化的系统盘，无法在重装后恢复。", 13));
        RefreshShops(); return Scroll(panel);
    }
    private ShopIdentity SelectedShop() => _shopList.SelectedItem as ShopIdentity ?? throw new InvalidOperationException("请先选中店铺。");
    private void RefreshShops(string? selectedId = null)
    {
        selectedId ??= (_shopList.SelectedItem as ShopIdentity)?.Id;
        var shops = _store.Business.Read().Shops; _shopList.ItemsSource = shops;
        _shopList.SelectedItem = shops.FirstOrDefault(s => s.Id == selectedId) ?? shops.FirstOrDefault();
    }
    private void EnsureCanRestore()
    {
        if (_sessions.Count > 0) throw new InvalidOperationException("请先在千牛连接页断开助手连接，再恢复资料，避免会话引用正在恢复的数据。千牛本身不用退出。");
    }
    private UIElement BuildLearning()
    {
        var panel = Stack(); panel.Children.Add(Text("学习建议", 28));
        panel.Children.Add(Text("助手生成建议后，如果读到你采用了不同的文字回复，会收集差异并提炼建议。只处理当前窗口新出现的文字，不回扫历史。姓名、联系方式、长编号会尽量隐藏；请在确认前检查原文、条件及适用范围。"));
        panel.Children.Add(_collectLearning);
        _collectLearning.Click += (_, _) => { if (!_refreshingLearning) _store.Business.Update(s => s with { LearningEnabled = _collectLearning.IsChecked == true }); };
        panel.Children.Add(Text("分析会将去标识的问题、回复片段和对应商品资料发送到 AI 设置中的服务。关闭自动收集后不再新增或分析建议，已经确认的经验仍可逐条停用。", 12));
        panel.Children.Add(Button("刷新学习列表", RefreshLearning)); panel.Children.Add(_learningSummary);
        panel.Children.Add(Button("清理已忽略的学习片段", () =>
        {
            _store.Business.Update(s => s with { Candidates = s.Candidates.Where(c => c.Status != "Ignored").ToArray() }); RefreshLearning();
            SetStatus("已清理忽略片段；已确认经验保留。清理后相同问题的新回复可能再次被收集。");
        }));
        panel.Children.Add(Text("待你确认（尚未用于回复）", 20)); panel.Children.Add(_learningItems);
        panel.Children.Add(Text("已确认经验", 20)); panel.Children.Add(_learnedItems);
        RefreshLearning(); return Scroll(panel);
    }
    private static string LearningKind(string kind) => kind switch { "Knowledge" => "商品知识", "Style" => "表达偏好", "Intent" => "问题理解", "CaseSpecific" => "订单或临时个案", "NoChange" => "无需学习", _ => "待判断" };
    private void RefreshLearning()
    {
        var state = _store.Business.Read(); _refreshingLearning = true; _collectLearning.IsChecked = state.LearningEnabled; _refreshingLearning = false;
        _learningItems.Children.Clear(); _learnedItems.Children.Clear();
        var pending = state.Candidates.Where(c => c.Status is not ("Approved" or "Ignored")).OrderByDescending(c => c.CreatedAt).ToArray();
        _learningSummary.Text = $"待处理 {pending.Length} 条 · 已确认经验 {state.Rules.Length} 条 · 已忽略 {state.Candidates.Count(c => c.Status == "Ignored")} 条。第一版经验仅用于同店同商品。";
        foreach (var c in pending.Take(50))
        {
            var p = state.Products.Single(p => p.ShopId == c.ShopId && p.ProductId == c.ProductId);
            var shop = state.Shops.Single(s => s.Id == c.ShopId);
            var body = new StackPanel { Margin = new Thickness(10) };
            body.Children.Add(Text($"{shop.DisplayName} · {p.Name}\n{LearningKind(c.Kind)} · {(c.Status == "PendingAnalysis" ? "等待分析" : c.Status == "AnalysisFailed" ? "分析未完成" : "待人工核对")}", 15));
            body.Children.Add(Text("买家问题：" + c.Question + "\n原 AI 建议：" + c.AiReply + "\n你的实际回复：" + c.HumanReply, 13));
            body.Children.Add(Text(c.Reason + (c.Evidence.Length > 0 ? "\n原文依据：" + c.Evidence : ""), 13));
            var question = Input(); question.Text = c.Question;
            var content = Input(true); content.MinHeight = 70; content.Text = c.Proposal;
            Field(body, "适用问题 / 条件（可修改）", question); Field(body, "确认后生效的内容（请核对，可修改）", content);
            var actions = new WrapPanel();
            actions.Children.Add(Button("重新分析", async () => { _manualLearningTask = new LearningService(_store).AnalyzeAsync(c.Id, _lifetime.Token); await _manualLearningTask; RefreshLearning(); }));
            var confirm = Button("核对无误，确认生效", () => { new LearningService(_store).Confirm(c.Id, question.Text, content.Text); RefreshLearning(); SetStatus("经验已确认，仅供同店同商品参考。结构化商品资料优先。"); });
            confirm.IsEnabled = c.Status == "Pending" && c.Kind is "Knowledge" or "Style" or "Intent" && c.ProductHash == LearningService.ProductHash(p);
            actions.Children.Add(confirm); actions.Children.Add(Button("忽略 / 不适用", () => { new LearningService(_store).Ignore(c.Id); RefreshLearning(); }));
            body.Children.Add(actions);
            if (c.Kind == "CaseSpecific") body.Children.Add(Text("涉及价格、优惠或售后承诺，请到商品与知识库填写完整条件。此条不能直接生效。", 12));
            if (c.ProductHash != LearningService.ProductHash(p)) body.Children.Add(Text("商品资料已变化，请重新分析后再确认。", 12));
            _learningItems.Children.Add(new Expander { Header = Text($"{LearningKind(c.Kind)} · {p.Name} · {c.Question}", 13), Content = body, Margin = new Thickness(0, 4, 0, 8) });
        }
        if (pending.Length > 50) _learningItems.Children.Add(Text("每次显示最近 50 条，处理后刷新可继续查看。"));
        foreach (var r in state.Rules.OrderByDescending(r => r.ConfirmedAt))
        {
            var p = state.Products.Single(p => p.ShopId == r.ShopId && p.ProductId == r.ProductId);
            var stale = r.ProductHash != LearningService.ProductHash(p);
            var body = new StackPanel { Margin = new Thickness(10) };
            body.Children.Add(Text($"{state.Shops.Single(s => s.Id == r.ShopId).DisplayName} · {p.Name}\n适用：{r.Question}\n{r.Content}"));
            body.Children.Add(Button(r.Active && !stale ? "停用这条经验" : "已重新核对当前商品，启用经验", () =>
            { new LearningService(_store).SetActive(r.Id, !(r.Active && !stale)); RefreshLearning(); }));
            _learnedItems.Children.Add(new Expander { Header = Text($"{(stale ? "资料变化，暂停" : r.Active ? "已生效" : "已停用")} · {LearningKind(r.Kind)} · {r.Question}", 13), Content = body, Margin = new Thickness(0, 4, 0, 8) });
        }
    }
}
