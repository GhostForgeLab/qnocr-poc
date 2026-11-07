using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QnocrPoc.Models;

namespace QNAgent;

internal sealed class ConversationWindow : Window
{
    private WindowAttachment? _attachment;
    private readonly FrameworkElement _body;
    private readonly Button _fold;
    private bool _collapsed;
    public ConversationWindow(UIElement content, Action settings, Action exit, string exitLabel = "退出")
    {
        Title = "QN Agent · 贴窗会话助手";
        Icon = AppBranding.WindowIcon;
        Width = 390; Height = 700; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowActivated = false; ShowInTaskbar = false; Background = new SolidColorBrush(Color.FromRgb(243, 246, 250));
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        var layout = new DockPanel();
        var header = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(23, 107, 206)), Height = 52, LastChildFill = true };
        Button ActionButton(string name, Action action)
        {
            var button = new Button { Content = name, Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(2, 10, 2, 10),
                Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            button.Click += (_, _) => action(); return button;
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(ActionButton("设置", settings));
        _fold = ActionButton("收起", ToggleCollapsed);
        actions.Children.Add(_fold);
        actions.Children.Add(ActionButton(exitLabel, exit));
        DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
        header.Children.Add(new TextBlock { Text = "会话助手", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
            FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        _body = new Border { Child = content };
        layout.Children.Add(_body);
        Content = new Border { Background = Background, BorderBrush = new SolidColorBrush(Color.FromRgb(204, 215, 229)), BorderThickness = new Thickness(1), Child = layout };
        Closing += (_, e) => { if (!_allowClose) { e.Cancel = true; settings(); } };
        Closed += (_, _) => _attachment?.Dispose();
    }
    private bool _allowClose;
    private void ToggleCollapsed()
    {
        _collapsed = !_collapsed; _body.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        _fold.Content = _collapsed ? "展开" : "收起"; _attachment?.SetCollapsed(_collapsed);
    }
    public void Attach(QianniuWindow target, Action lost)
    {
        _attachment?.Dispose();
        _attachment = new WindowAttachment(this, target, lost);
        _attachment.SetCollapsed(_collapsed);
    }
    public void Reveal() => _attachment?.Refresh();
    public void Detach() { _attachment?.Dispose(); _attachment = null; Hide(); }
    public void Exit() { _allowClose = true; _attachment?.Dispose(); Close(); }
}
