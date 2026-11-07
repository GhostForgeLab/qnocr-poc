using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QnocrPoc.Models;

namespace QNAgent;

internal sealed partial class ReceptionSession
{
    private readonly TextBlock _intentSummary = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _intentConfidence = new() { FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _intentGuidance = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
    private UIElement BuildIntentCard()
    {
        var content = new StackPanel(); var header = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        DockPanel.SetDock(_intentConfidence, Dock.Right); header.Children.Add(_intentConfidence);
        header.Children.Add(new TextBlock { Text = "JEV 意图分析", FontSize = 12, Foreground = Brush("#176BCE") });
        content.Children.Add(header); content.Children.Add(_intentSummary); content.Children.Add(_intentGuidance);
        var card = new Border { Child = content, Background = Brush("#EAF2FC"), BorderBrush = Brush("#D2E3F8"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8) };
        card.ToolTip = "这是 JEV 对当前消息的判断，并非对买家心理的事实认定。把握表示本次选项概率，不等于正确率。";
        ResetIntentCard(); return card;
    }
    private void ResetIntentCard(string text = "等待读取当前买家意图")
    { _intentSummary.Text = text; _intentConfidence.Text = ""; _intentGuidance.Text = ""; }
    private void ShowIntent(JevAssessment? assessment, IntentResult? legacy = null, bool historical = false)
    {
        var presentation = BuyerIntentDescriptions.Present(assessment?.Purpose, legacy ?? assessment?.Intent);
        _intentSummary.Text = presentation.Summary; _intentConfidence.Text = presentation.Confidence;
        _intentConfidence.Foreground = presentation.Uncertain ? Brush("#936717") : Brush("#176BCE");
        _intentGuidance.Text = (historical ? "历史判断 · " : "回复方向：") + presentation.Guidance;
    }
    private void ShowIntentForReply(AssistedReplyView view)
    {
        var audit = _replies.ReadAudit(view);
        if (view.KnowledgeCategory == "JEV_UNAVAILABLE") { ResetIntentCard("JEV 暂未完成判断，请重试"); return; }
        if (view.KnowledgeCategory == "VISUAL_CLARIFICATION") { ResetIntentCard("图像内容尚未识别，需人工确认"); return; }
        ShowIntent(audit?.Assessment, view.Decision, !_replies.IsCurrentPipeline(view));
    }
}
