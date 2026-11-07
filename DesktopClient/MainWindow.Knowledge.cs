using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace QNAgent;

internal sealed partial class MainWindow
{
    private KnowledgeRowsEditor _variants = null!, _faqs = null!, _productPromotions = null!, _shopPromotions = null!;
    private readonly TextBox _shopSeller = Input();
    private readonly TextBlock _shopLabel = Text("尚未载入店铺优惠", 14);
    private readonly CheckBox _shopApproved = new() { Content = "我已核对店铺优惠，允许用于该店铺的 AI 回复", Margin = new Thickness(0, 12, 0, 12) };
    private string? _loadedShopSeller;
    private static readonly string[] PromotionLabels = { "优惠名称", "适用范围（商品 / 规格 / 排除项）", "优惠规则（门槛、减免金额、领取条件）", "有效期（起止时间；未知填待核实）", "叠加规则（可与哪些优惠同用；未知填待核实）" };
    private void BuildVariantEditor(Panel panel)
    {
        panel.Children.Add(Text("商品规格与价格", 20));
        panel.Children.Add(Text("一个商品链接下的不同款式逐条添加。价格单位为元；留空表示待核实。买家未说明款式时，AI 应先确认规格。", 12));
        _variants = new KnowledgeRowsEditor("＋ 增加规格", new[] { "规格 / 款式名称", "价格（元）", "规格差异 / 容量 / 适用说明" }, true, () => _approved.IsChecked = false);
        panel.Children.Add(_variants);
    }
    private void BuildProductPromotions(Panel panel)
    {
        panel.Children.Add(Text("商品单品优惠", 20));
        panel.Children.Add(Text("仅属于当前商品。若只适用部分规格，请写明规格名称；没有优惠可不添加。", 12));
        _productPromotions = new KnowledgeRowsEditor("＋ 增加商品优惠", PromotionLabels, false, () => _approved.IsChecked = false);
        panel.Children.Add(_productPromotions);
    }
    private void BuildFaqEditor(Panel panel)
    {
        panel.Children.Add(Text("补充知识 / 常见问答", 20));
        _faqs = new KnowledgeRowsEditor("＋ 新增问答", new[] { "问题", "答案" }, false, () => _approved.IsChecked = false);
        panel.Children.Add(_faqs);
        var shortcuts = new WrapPanel();
        foreach (var question in new[] { "不同规格有什么区别？", "适合什么场景使用？", "如何使用和保养？" })
            shortcuts.Children.Add(Button("＋ " + question, () => _faqs.Add(question, "")));
        panel.Children.Add(shortcuts);
    }
    private void BuildShopPromotions(Panel panel)
    {
        panel.Children.Add(Text("店铺优惠（独立保存，同店商品共用）", 23));
        panel.Children.Add(Text("填写与千牛连接一致的店铺标识，然后载入。修改后须单独核对并保存；商品 JSON 导入 / 导出不包含此处的店铺优惠。", 12));
        Field(panel, "优惠所属店铺标识", _shopSeller);
        var load = Button("载入该店铺优惠", () =>
        {
            var seller = _shopSeller.Text.Trim();
            CatalogValidation.ValidateShop(new ShopKnowledge { SellerId = seller });
            var shop = _store.Shop(seller) ?? new ShopKnowledge { SellerId = seller };
            _loadedShopSeller = seller; _shopPromotions.Load(shop.Promotions.Select(PromotionValues));
            _shopApproved.IsChecked = shop.Approved;
            _shopLabel.Text = $"正在编辑：{seller} · {(shop.Approved ? "已核对" : "待核对")}（重新载入会还原未保存编辑）";
            _shopPromotions.IsEnabled = true;
        });
        panel.Children.Add(load); panel.Children.Add(_shopLabel);
        _shopPromotions = new KnowledgeRowsEditor("＋ 增加店铺优惠", PromotionLabels, false, () => _shopApproved.IsChecked = false) { IsEnabled = false };
        panel.Children.Add(_shopPromotions); panel.Children.Add(_shopApproved);
        _shopSeller.TextChanged += (_, _) =>
        {
            _shopPromotions.IsEnabled = _loadedShopSeller == _shopSeller.Text.Trim();
            _shopApproved.IsChecked = false;
        };
        panel.Children.Add(Button("保存该店铺优惠", () =>
        {
            if (_loadedShopSeller is null || _loadedShopSeller != _shopSeller.Text.Trim()) throw new InvalidDataException("请先载入需要编辑的店铺优惠。");
            var shop = new ShopKnowledge { SellerId = _loadedShopSeller, Promotions = ReadPromotions(_shopPromotions), Approved = _shopApproved.IsChecked == true };
            _store.SaveShop(shop);
            _shopLabel.Text = $"正在编辑：{shop.SellerId} · {(shop.Approved ? "已核对" : "待核对")}";
            SetStatus(shop.Approved ? "店铺优惠已保存，供该店铺所有商品按适用范围使用。" : "店铺优惠草稿已保存，尚不用于 AI 回复。");
        }));
    }
    private ProductVariant[] ReadVariants() => _variants.Values().Select(v =>
    {
        decimal? price = null;
        if (v[1].Length > 0)
        {
            if (!decimal.TryParse(v[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
                throw new InvalidDataException($"规格“{v[0]}”的价格请填写数字，例如 298.00；不知道价格可留空。");
            price = amount;
        }
        return new ProductVariant { Name = v[0], Price = price, Details = v[2] };
    }).ToArray();
    private static string[] PromotionValues(Promotion p) => new[] { p.Title, p.AppliesTo, p.Rule, p.Validity, p.Stacking };
    private static Promotion[] ReadPromotions(KnowledgeRowsEditor editor) => editor.Values().Select(p => new Promotion
        { Title = p[0], AppliesTo = p[1], Rule = p[2], Validity = p[3], Stacking = p[4] }).ToArray();
}
