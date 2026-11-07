using System.Net;
using System.Text;
using QnocrPoc.Models;

internal static class AgentConsolePages
{
    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string Query(string token) => "?token=" + Uri.EscapeDataString(token);
    public static string SessionUrl(string sessionId, string token) =>
        "/session/" + Uri.EscapeDataString(sessionId) + Query(token);
    private static string Actor() => "<label>操作员 <input name='operator' required maxlength='128' autocomplete='off'></label>";
    private static string Page(string title, string body) =>
        "<!doctype html><html lang='zh-CN'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>" +
        "<title>" + H(title) + "</title><style>body{font:15px/1.55 system-ui,sans-serif;max-width:1180px;margin:28px auto;padding:0 18px;background:#f5f7fa;color:#243244}" +
        "h1,h2,h3{line-height:1.25}section,article{background:#fff;border:1px solid #d9e1ea;border-radius:10px;padding:18px;margin:16px 0}" +
        "table{border-collapse:collapse;width:100%}th,td{padding:8px;border-bottom:1px solid #e7ebf0;text-align:left;vertical-align:top}" +
        "input,textarea,select{padding:7px;border:1px solid #b8c2ce;border-radius:5px;max-width:100%;box-sizing:border-box}" +
        "input[type=text],textarea{width:100%}textarea{min-height:72px}label{display:block;margin:8px 0}" +
        "button{background:#1769aa;color:#fff;border:0;border-radius:5px;padding:8px 12px;cursor:pointer;margin:4px 4px 4px 0}" +
        "button.warn{background:#a3431a}button.safe{background:#276c49}.muted,small{color:#596778}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(230px,1fr));gap:12px}" +
        "code{overflow-wrap:anywhere}.badge{display:inline-block;padding:2px 7px;border-radius:12px;background:#e8eef7;margin:3px}" +
        "form.inline{display:inline-block;margin-right:12px}blockquote{border-left:3px solid #c6d5e4;padding:6px 12px;margin:8px 0;background:#fafbfd}</style></head><body>" +
        body + "</body></html>";

    public static string Error(string message, string token) => Page("操作未完成",
        "<section><h1>操作未完成</h1><p>" + H(message) + "</p><a href='/" + Query(token) + "'>返回工作台</a></section>");

    public static string Home(IReadOnlyList<AgentConsoleSessionRow> sessions, string token, bool demo = false)
    {
        var html = new StringBuilder("<h1>Agent Console · 客服辅助工作台</h1>");
        if (demo) html.Append("<section><strong>本地演示</strong><p>以下为合成买家消息。已执行实际上下文、规则意图、知识检查和人工预览流程；缺少商品资料时显示人工核实建议。本演示未连接千牛或外部 AI。</p></section>");
        html.Append("<p class='muted'>仅显示所选Shadow运行批次。AI只生成建议；复制后由客服在千牛人工粘贴发送。系统发送数始终为0。</p>")
            .Append("<section><h2>会话列表</h2><table><thead><tr><th>SessionId</th><th>Buyer</th><th>LastMessage</th><th>UpdatedAt</th><th>预览数</th></tr></thead><tbody>");
        foreach (var session in sessions)
            html.Append("<tr><td><a href='").Append(SessionUrl(session.SessionId, token)).Append("'><code>")
                .Append(H(session.SessionId)).Append("</code></a></td><td>").Append(H(session.Buyer))
                .Append("</td><td>").Append(H(session.LastMessage))
                .Append("</td><td>").Append(H(session.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")))
                .Append("</td><td>").Append(session.PreviewCount).Append("</td></tr>");
        if (sessions.Count == 0) html.Append("<tr><td colspan='5'>当前运行批次尚无Shadow回复预览。</td></tr>");
        html.Append("</tbody></table></section>");
        return Page("Agent Console", html.ToString());
    }

    public static string Conversation(AgentConsoleConversation conversation, string token)
    {
        var session = conversation.Session;
        var html = new StringBuilder();
        html.Append("<a href='/").Append(Query(token)).Append("'>← 会话列表</a><h1>会话 · ")
            .Append(H(session.BuyerDisplayName ?? session.ObservedBuyer)).Append("</h1><p><code>")
            .Append(H(session.SessionId)).Append("</code></p>")
            .Append("<p class='muted'>聊天时间线仅包含当前Shadow运行采集到的新消息；原千牛历史消息不在此重放。</p>");
        var product = session.CurrentProductContext;
        html.Append("<section><h2>当前商品上下文</h2>");
        if (product is null)
            html.Append("<p>状态：").Append(H(session.ProductContextBindingStatus?.ToString() ?? "Unknown"))
                .Append("。当前无可用商品绑定，知识查询不得借用历史商品。</p>");
        else
        {
            html.Append("<div class='grid'><p>ProductId：<code>").Append(H(product.ProductId))
                .Append("</code></p><p>名称：").Append(H(product.ProductName.Length == 0 ? "未确认" : product.ProductName))
                .Append("</p><p>来源：").Append(H(product.Source.ToString()))
                .Append("</p><p>Confidence：").Append(H(product.Confidence?.ToString("0.00") ?? "未校准/人工绑定"))
                .Append("</p><p>绑定人/依据：").Append(H(product.BoundBy))
                .Append("</p><p>时间：").Append(H(product.CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")))
                .Append("</p></div>");
            if (product.Source == ProductContextSource.Manual && conversation.CanClearProduct)
                html.Append("<form method='post' action='/session/").Append(H(session.SessionId))
                    .Append("/clear-product").Append(Query(token)).Append("'>").Append(Actor())
                    .Append("<button class='warn'>清除当前人工商品绑定</button></form>");
            else html.Append("<small>商品信息为采集时快照，由后续采集更新。</small>");
        }
        html.Append("</section><section><h2>买家消息时间线</h2><table><thead><tr><th>时间</th><th>角色</th><th>内容</th><th>来源</th></tr></thead><tbody>");
        foreach (var message in conversation.Timeline)
            html.Append("<tr><td>").Append(H(message.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")))
                .Append("</td><td>").Append(H(message.Role)).Append("</td><td>")
                .Append(H(message.Text)).Append("</td><td>").Append(H(message.Source)).Append("</td></tr>");
        if (conversation.Timeline.Count == 0) html.Append("<tr><td colspan='4'>暂无已派发消息。</td></tr>");
        html.Append("</tbody></table></section><section><h2>AI助手与回复建议</h2>");
        foreach (var notice in conversation.Notices ?? [])
            html.Append("<p class='muted'>").Append(H(notice)).Append("</p>");
        foreach (var preview in conversation.Previews)
        {
            var view = preview.View;
            var id = H(view.ReplyPreviewId);
            var url = Query(token);
            html.Append("<article><h3>").Append(H(view.OriginalMessage)).Append("</h3><div>")
                .Append("<span class='badge'>Intent: ").Append(H(view.DecisionIntent ?? "未知"))
                .Append("</span><span class='badge'>Provider: ").Append(H(preview.IntentProvider))
                .Append(preview.FallbackUsed ? "（降级）" : "").Append("</span>")
                .Append("<span class='badge'>Confidence: ").Append(H(view.Decision?.Confidence?.ToString("0.00") ?? "未提供"))
                .Append("</span><span class='badge'>Knowledge: ").Append(H(view.KnowledgeStatus ?? "未记录"))
                .Append("</span></div><p>ProductContext：<code>")
                .Append(H(preview.ProductContext?.ProductId ?? "无当前绑定"))
                .Append("</code> · KnowledgeSource：").Append(H(view.KnowledgeSource ?? "None"))
                .Append(" · KnowledgeUsedByAI：").Append(view.KnowledgeUsedByAi ? "是" : "否")
                .Append(" · Intent耗时：").Append(H(preview.IntentLatencyMilliseconds?.ToString("0.0") ?? "未记录"))
                .Append(" ms</p><small>风险：").Append(H(view.RiskLevel)).Append(" · ")
                .Append(H(view.RiskReason)).Append(" · 状态：").Append(H(view.Status))
                .Append(" · 反馈：").Append(H(preview.FeedbackType ?? "未评价"))
                .Append(" · SendStatus：").Append(H(view.SendStatus)).Append("</small>")
                .Append("<p>AI建议：</p><blockquote>").Append(H(view.AiReply)).Append("</blockquote>");
            if (view.EditedReply is not null)
                html.Append("<p>人工修改：</p><blockquote>").Append(H(view.EditedReply)).Append("</blockquote>");
            if (view.Status is AssistedReplyStatuses.AiGenerated or AssistedReplyStatuses.HumanEdited)
            {
                html.Append("<form method='post' action='/preview/").Append(id).Append("/edit").Append(url)
                    .Append("'><label>修改后回复<textarea name='reply' required maxlength='4000'>")
                    .Append(H(view.EffectiveReply)).Append("</textarea></label>").Append(Actor())
                    .Append("<button>保存修改</button></form>");
                html.Append("<form method='post' action='/preview/").Append(id).Append("/approve").Append(url)
                    .Append("'><input type='hidden' name='replyHash' value='").Append(H(view.ReplySha256))
                    .Append("'>").Append(Actor()).Append("<button class='safe'>人工批准当前文本</button></form>");
            }
            if (view.Status == AssistedReplyStatuses.Approved)
                html.Append("<form method='post' action='/preview/").Append(id).Append("/copy").Append(url)
                    .Append("'><input type='hidden' name='replyHash' value='").Append(H(view.ReplySha256))
                    .Append("'>").Append(Actor()).Append("<button class='safe'>复制已批准回复</button></form>");
            if (preview.FeedbackType is null)
            {
                html.Append("<form method='post' action='/preview/").Append(id).Append("/feedback").Append(url)
                    .Append("'><label>人工评价<select name='type' required>")
                    .Append("<option value='Rejected'>Rejected（不采用）</option>");
                if (view.Status == AssistedReplyStatuses.Copied)
                    html.Append(view.EditedReply is null
                        ? "<option value='Accepted'>Accepted（直接采用）</option>"
                        : "<option value='Edited'>Edited（修改后采用）</option>");
                html.Append("</select></label><label>原因<select name='reason'><option value=''>未填写</option>")
                    .Append("<option>信息不准确</option><option>语气问题</option><option>缺少商品信息</option><option>其他</option></select></label>")
                    .Append("<label>拒绝时的最终人工回复（可选）<textarea name='finalText' maxlength='4000'></textarea></label>")
                    .Append(Actor()).Append("<button>记录人工反馈</button></form>");
            }
            html.Append("</article>");
        }
        if (conversation.Previews.Count == 0) html.Append("<p>本运行批次暂无AI回复预览。</p>");
        html.Append("</section><p class='muted'>本工作台仅复制到剪贴板。客服需自行切换千牛并人工粘贴发送；程序发送数为0。</p>");
        return Page("Agent Console · 会话", html.ToString());
    }
}
