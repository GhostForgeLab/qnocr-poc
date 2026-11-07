using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QNAgent;

internal static class KnowledgeEditorChecks
{
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var root = Path.GetFullPath(Path.Combine("Output", "KnowledgeEditorChecks", Guid.NewGuid().ToString("N")));
        var store = new ClientStore(root); var window = new MainWindow(store); var count = 0;
        void Check(bool pass, string label) { if (!pass) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        var product = new CatalogProduct
        {
            SellerId = "演示店铺", ProductId = "12345678901", Name = "烟盒（界面测试示例）", Source = "界面测试数据，并非真实商品信息", Approved = true,
            Capacity = "原有规格说明", CurrentPrice = "原有价格说明", Notes = "原有文档继续保留。",
            Variants = new[] { new ProductVariant { Name = "粗支烟盒 - 款式 A", Price = 298, Details = "适用粗支；具体容量待核实" }, new ProductVariant { Name = "细支烟盒 - 款式 B", Price = 328.50m, Details = "适用细支；具体容量待核实" } },
            Faqs = new[] { new ProductFaq { Question = "不同规格有什么区别？", Answer = "款式 A 适用粗支，款式 B 适用细支。其他尺寸以核对后的资料为准。" }, new ProductFaq { Question = "适合什么场景使用？", Answer = "待根据实际商品说明补充。" } },
            Promotions = new[] { new Promotion { Title = "示例单品优惠", AppliesTo = "仅粗支款式 A", Rule = "满 200 元减 10 元（测试示例）", Validity = "2026-10-01 至 2026-10-31", Stacking = "不可与店铺券同用" } }
        };
        try
        {
            Call("EditProduct", product);
            var variants = Field<KnowledgeRowsEditor>("_variants"); var faqs = Field<KnowledgeRowsEditor>("_faqs");
            var approved = Field<CheckBox>("_approved");
            Check(approved.IsChecked == true && variants.Values().Length == 2 && faqs.Values().Length == 2, "editing restores structured rows and approval");
            variants.Add("新增待核实款式", "", "");
            Check(approved.IsChecked == false, "adding a variant revokes product approval");
            var rows = (StackPanel)variants.Children[0]; var last = (Border)rows.Children[rows.Children.Count - 1];
            var delete = ((Grid)last.Child).Children.OfType<Button>().Single(); delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(variants.Values().Length == 2, "deleting a variant removes its persisted row");
            approved.IsChecked = true; Call("SaveProduct");
            var saved = store.Products().Single();
            Check(saved.Variants[1].Price == 328.50m && saved.Faqs.Length == 2 && saved.Approved && saved.Capacity == "原有规格说明" && saved.Notes == product.Notes, "UI save persists structured content and legacy fields together");
            Check(Field<StackPanel>("_productEditor").Visibility == Visibility.Collapsed && variants.Values().Length == 0 && faqs.Values().Length == 0 &&
                Field<System.Collections.Generic.Dictionary<string, TextBox>>("_fields").Values.All(f => f.Text == ""), "save clears and collapses every product input");
            var list = Field<ListBox>("_products"); list.SelectedItem = null; list.SelectedIndex = 0;
            Check(Field<StackPanel>("_productEditor").Visibility == Visibility.Collapsed, "selecting a saved product does not reopen the editor implicitly");
            Field<Button>("_editProductButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Field<StackPanel>("_productEditor").Visibility == Visibility.Visible && faqs.Values().Length == 2 && variants.Values()[1][1] == "328.50", "explicit edit button restores selected persisted product");
            var first = (Border)((StackPanel)faqs.Children[0]).Children[0];
            var answer = ((Grid)first.Child).Children.OfType<StackPanel>().Last().Children.OfType<TextBox>().Single();
            answer.Text = "已修改的答案";
            Check(approved.IsChecked == false, "editing an answer revokes product approval");
            Call("SaveProduct"); Check(!store.Products().Single().Approved && store.Products().Single().Faqs[0].Answer == "已修改的答案", "FAQ edit saves as unapproved draft");
            Call("EditProduct", product);
            var scroll = Field<ScrollViewer>("_knowledgeScroll");
            Field<ContentControl>("_page").Content = Field<UIElement>("_knowledgePage");
            scroll.Background = window.Background;
            var panel = (StackPanel)scroll.Content;
            var shopSeller = Field<TextBox>("_shopSeller"); shopSeller.Text = "演示店铺";
            panel.Children.OfType<Button>().Single(b => Equals(b.Content, "载入该店铺优惠")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var shopRows = Field<KnowledgeRowsEditor>("_shopPromotions");
            shopRows.Add("示例店铺券", "全店（定制款除外）", "满 500 减 30", "2026-10-01 至 2026-10-31", "不可与商品券同用");
            Field<CheckBox>("_shopApproved").IsChecked = true;
            panel.Children.OfType<Button>().Single(b => Equals(b.Content, "保存该店铺优惠")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(store.Shop("演示店铺")?.Approved == true && store.Shop("演示店铺")!.Promotions.Single().Rule == "满 500 减 30", "shop editor saves independently with explicit approval");
            shopSeller.Text = "其他店铺";
            Check(!shopRows.IsEnabled && Field<CheckBox>("_shopApproved").IsChecked == false, "changing shop identity disables stale promotion editor");
            shopSeller.Text = "演示店铺";
            scroll.Measure(new Size(1100, 900)); scroll.Arrange(new Rect(0, 0, 1100, 900)); scroll.UpdateLayout();
            void Render(string file, FrameworkElement? target = null)
            {
                scroll.ScrollToVerticalOffset(target is null ? 0 : target.TranslatePoint(new Point(0, 0), panel).Y - 65); scroll.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1100, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(scroll);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(root, file)); encoder.Save(output);
            }
            Render("01-variants.png", variants); Render("02-product-promotions.png", Field<KnowledgeRowsEditor>("_productPromotions"));
            Render("03-faq.png", faqs); Render("04-shop-promotions.png", shopRows);
            Check(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "knowledge editor fits viewport without horizontal scrolling");
            scroll.Measure(new Size(740, 700)); scroll.Arrange(new Rect(0, 0, 740, 700)); scroll.UpdateLayout();
            Check(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "variant table also fits the minimum window width");
            Call("SaveProduct");
            scroll.Measure(new Size(1100, 900)); scroll.Arrange(new Rect(0, 0, 1100, 900)); scroll.UpdateLayout();
            Render("00-after-save.png");
            panel.Children.OfType<WrapPanel>().First().Children.OfType<Button>().Single(b => Equals(b.Content, "新增商品")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Field<StackPanel>("_productEditor").Visibility == Visibility.Visible && variants.Values().Length == 0 &&
                Field<System.Collections.Generic.Dictionary<string, TextBox>>("_fields")["ProductId"].Text == "", "new-product button opens a clean form after saving");
            try { Call("SaveProduct"); throw new Exception("Expected incomplete-product rejection"); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidDataException)
            { Check(Field<StackPanel>("_productEditor").Visibility == Visibility.Visible, "failed save keeps the form available for correction"); }
            Console.WriteLine($"Passed {count} knowledge editor checks. Previews: {root}");
            return 0;
        }
        finally { window.Close(); app.Shutdown(); }
    }
}
