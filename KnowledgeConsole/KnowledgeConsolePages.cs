using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using QnocrPoc.Models;

internal static class KnowledgeConsolePages
{
    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string Url(string token) => "?token=" + Uri.EscapeDataString(token);
    private static string Actor() => "<label>操作员 <input name='actor' required maxlength='128' autocomplete='off'></label>";
    private static string Page(string title, string body) => "<!doctype html><html lang='zh-CN'><head><meta charset='utf-8'>" +
        "<meta name='viewport' content='width=device-width,initial-scale=1'><title>" + H(title) + "</title>" +
        "<style>body{font:15px/1.55 system-ui,sans-serif;max-width:1120px;margin:28px auto;padding:0 18px;color:#1d2939;background:#f5f7fa}" +
        "h1,h2{line-height:1.25}section,article{background:white;border:1px solid #d9e0e8;border-radius:10px;padding:18px;margin:18px 0}" +
        "table{border-collapse:collapse;width:100%}th,td{padding:8px;border-bottom:1px solid #e7ebf0;text-align:left;vertical-align:top}" +
        "input,textarea{padding:7px;border:1px solid #b8c2ce;border-radius:5px;max-width:100%;box-sizing:border-box}" +
        "input[type=text],textarea{width:100%}textarea{min-height:66px}label{display:block;margin:8px 0}" +
        "button{background:#1769aa;color:white;border:0;border-radius:5px;padding:8px 12px;cursor:pointer;margin:4px 4px 4px 0}" +
        "button.warn{background:#a3431a}button.safe{background:#276c49}.muted{color:#596778}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(230px,1fr));gap:12px}" +
        "code{overflow-wrap:anywhere}form.inline{display:inline-block;margin-right:10px}details{margin:8px 0}small{display:block;color:#596778}</style></head><body>" +
        body + "</body></html>";

    public static string Error(string message, string token) => Page("操作未完成",
        "<section><h1>操作未完成</h1><p>" + H(message) + "</p><a href='/" + Url(token) + "'>返回知识控制台</a></section>");

    public static string Home(IReadOnlyList<KnowledgeItem> items, IReadOnlyList<KnowledgeDraft> drafts,
        IReadOnlyList<KnowledgeRevision> revisions, string token)
    {
        var url = Url(token);
        var html = new StringBuilder();
        html.Append("<h1>商品知识控制台</h1><p class='muted'>仅限本机测试商品。知识需人工审核及发布；此页面不会调用 AI 或发送买家消息。</p>");
        html.Append("<section><h2>商品列表</h2><table><thead><tr><th>SellerId</th><th>ProductId</th><th>ProductName</th><th>KnowledgeStatus</th><th>Version</th><th>UpdatedAt</th></tr></thead><tbody>");
        var products = items.GroupBy(item => (item.SellerId, item.ProductId))
            .OrderBy(group => group.Key.SellerId).ThenBy(group => group.Key.ProductId).ToArray();
        foreach (var product in products)
        {
            var active = product.Where(item => item.Status == KnowledgeStatus.Published && item.AiUseAllowed).ToArray();
            var latest = drafts.Where(draft => draft.SellerId == product.Key.SellerId && draft.ProductId == product.Key.ProductId)
                .OrderByDescending(draft => draft.CreatedAt).FirstOrDefault();
            var status = active.Length > 0 ? "Published" : latest?.Status.ToString() ?? "Draft";
            var version = active.Length > 0 ? string.Join(", ", active.Select(item => item.Version.Version).Distinct()) :
                latest?.Content.Metadata?.Version ?? "";
            html.Append("<tr><td>").Append(H(product.Key.SellerId)).Append("</td><td><code>").Append(H(product.Key.ProductId))
                .Append("</code></td><td>").Append(H(latest?.Content.Identity.ProductName ?? "未录入"))
                .Append("</td><td>").Append(H(status)).Append("</td><td>").Append(H(version))
                .Append("</td><td>").Append(H(product.Max(item => item.UpdatedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm"))).Append("</td></tr>");
        }
        if (products.Length == 0) html.Append("<tr><td colspan='6'>尚无商品知识</td></tr>");
        html.Append("</tbody></table></section>");
        html.Append("<section><h2>创建商品知识 Draft</h2><p class='muted'>一次创建一个测试商品；请仅填写有来源依据的字段。Features 与 FAQ 每行用“名称|说明”“问题|答案”。</p>");
        html.Append("<form method='post' action='/draft").Append(url).Append("'><div class='grid'>")
            .Append("<label>SellerId<input name='sellerId' required maxlength='64'></label>")
            .Append("<label>ProductId（数字）<input name='productId' required pattern='[0-9]+' maxlength='32'></label>")
            .Append("<label>ProductName<input name='productName' required maxlength='100'></label>")
            .Append("<label>Version<input name='version' required maxlength='64' placeholder='v1'></label>")
            .Append("<label>资料来源引用<input name='sourceReference' required maxlength='128' placeholder='商品页或资料文件'></label>")
            .Append("<label>Capacity（逗号分隔）<input name='capacity' placeholder='100ml,200ml'></label>")
            .Append("<label>Size<input name='size'></label><label>Weight<input name='weight'></label>")
            .Append("<label>Material<input name='material'></label></div>")
            .Append("<label>Features<textarea name='features' placeholder='卖点名称|经核对的描述'></textarea></label>")
            .Append("<label>FAQ<textarea name='faq' placeholder='问题|经核对的答案'></textarea></label>")
            .Append("<label>Restrictions<textarea name='restrictions' placeholder='每行一条限制'></textarea></label>")
            .Append(Actor()).Append("<button type='submit'>只创建 Draft</button></form></section>");
        var revisionCandidates = revisions.Where(revision => revision.CandidateKnowledgeId is not null)
            .Select(revision => revision.CandidateKnowledgeId!).ToHashSet(StringComparer.Ordinal);
        html.Append("<section><h2>知识项与审核</h2><p class='muted'>Draft 可编辑；Reviewing 需人工批准或拒绝；Approved 需单独确认发布。</p>");
        foreach (var item in items.OrderByDescending(item => item.UpdatedAt))
        {
            html.Append("<article><strong>").Append(H(item.SellerId)).Append(" / ").Append(H(item.ProductId))
                .Append(" / ").Append(H(item.Key)).Append("</strong> · ").Append(H(item.Status.ToString()))
                .Append(" · ").Append(H(item.Version.Version)).Append("<p>当前值：").Append(H(item.Value))
                .Append("</p><small>来源：").Append(H(item.Source.SourceType.ToString())).Append(" / ")
                .Append(H(item.Source.Reference)).Append(" · AI可用：").Append(item.AiUseAllowed ? "是" : "否").Append("</small>");
            var prefix = "/item/" + item.KnowledgeId;
            if (item.Status == KnowledgeStatus.Draft)
            {
                html.Append("<form method='post' action='").Append(prefix).Append("/edit").Append(url)
                    .Append("'><label>编辑 Draft 值<input name='value' required maxlength='200' value='").Append(H(item.Value))
                    .Append("'></label>").Append(Actor()).Append("<button>保存 Draft</button></form>");
                html.Append("<form method='post' action='").Append(prefix).Append("/submit").Append(url)
                    .Append("'>").Append(Actor()).Append("<button>提交审核</button></form>");
            }
            if (item.Status == KnowledgeStatus.Reviewing)
            {
                html.Append("<form class='inline' method='post' action='").Append(prefix).Append("/approve").Append(url)
                    .Append("'>").Append(Actor()).Append("<button class='safe'>批准</button></form>");
                html.Append("<form class='inline' method='post' action='").Append(prefix).Append("/reject").Append(url)
                    .Append("'>").Append(Actor()).Append("<label>拒绝原因<input name='reason' required></label><button class='warn'>拒绝</button></form>");
            }
            if (item.Status == KnowledgeStatus.Approved && !revisionCandidates.Contains(item.KnowledgeId))
                html.Append("<form method='post' action='").Append(prefix).Append("/publish").Append(url)
                    .Append("'>").Append(Actor()).Append("<label><input type='checkbox' name='confirm' value='publish' required> 确认发布并允许 AI 使用</label>")
                    .Append("<button class='warn'>发布知识</button></form>");
            if (item.Status == KnowledgeStatus.Approved && revisionCandidates.Contains(item.KnowledgeId))
                html.Append("<p class='muted'>这是修订候选项；请使用下方修订发布入口。</p>");
            if (item.Status == KnowledgeStatus.Published && item.AiUseAllowed)
                html.Append("<details><summary>创建此字段的 Revision Draft</summary><form method='post' action='/revision").Append(url)
                    .Append("'><input type='hidden' name='sellerId' value='").Append(H(item.SellerId))
                    .Append("'><input type='hidden' name='productId' value='").Append(H(item.ProductId))
                    .Append("'><input type='hidden' name='key' value='").Append(H(item.Key))
                    .Append("'><label>新值<input name='newValue' required maxlength='200'></label>")
                    .Append("<label>新版本号<input name='version' required maxlength='64'></label>")
                    .Append("<label>修改原因<input name='reason' required maxlength='200'></label>")
                    .Append("<label>资料来源引用<input name='sourceReference' required maxlength='128'></label>")
                    .Append(Actor()).Append("<button>创建 Revision Draft</button></form></details>");
            html.Append("</article>");
        }
        if (items.Count == 0) html.Append("<p>尚无知识项。</p>");
        html.Append("</section><section><h2>Draft 与修订记录</h2>");
        foreach (var draft in drafts)
            html.Append("<article>Draft <code>").Append(H(draft.DraftId)).Append("</code> · ")
                .Append(H(draft.Content.Identity.ProductName)).Append(" · ").Append(H(draft.Status.ToString()))
                .Append(" · ").Append(draft.KnowledgeItemIds.Count).Append(" 个知识项")
                .Append(draft.ConflictingFields.Count > 0 ? "<p>冲突字段：" + H(string.Join(", ", draft.ConflictingFields)) + "</p>" : "")
                .Append("</article>");
        foreach (var revision in revisions)
        {
            var change = revision.ChangedFields.FirstOrDefault();
            html.Append("<article>Revision <code>").Append(H(revision.RevisionId)).Append("</code> · ")
                .Append(H(revision.Status.ToString())).Append(" · ").Append(H(revision.ProductId))
                .Append(" / ").Append(H(change?.Key)).Append("：").Append(H(change?.OldValue))
                .Append(" → ").Append(H(change?.NewValue));
            if (revision.Status == KnowledgeRevisionStatus.Approved)
                html.Append("<form method='post' action='/revision/").Append(revision.RevisionId).Append("/publish")
                    .Append(url).Append("'>").Append(Actor())
                    .Append("<label><input type='checkbox' name='confirm' value='publish' required> 确认替换当前版本；旧版本保留</label>")
                    .Append("<button class='warn'>发布修订</button></form>");
            html.Append("</article>");
        }
        html.Append("</section>");
        return Page("商品知识控制台", html.ToString());
    }

    public static ProductKnowledgeAuthoringInput ParseDraft(IFormCollection form)
    {
        static string? Optional(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var capacity = form["capacity"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var features = Lines(form["features"].ToString()).Select(line =>
        {
            var pair = line.Split('|', 2, StringSplitOptions.TrimEntries);
            return new ProductFeature(pair[0], pair.Length == 2 ? pair[1] : null);
        }).ToArray();
        var faq = Lines(form["faq"].ToString()).Select(line =>
        {
            var pair = line.Split('|', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2) throw new ArgumentException("FAQ 每行必须是：问题|答案。");
            return new ProductFAQ(pair[0], pair[1]);
        }).ToArray();
        var restrictions = Lines(form["restrictions"].ToString())
            .Select(line => new ProductRestriction(line)).ToArray();
        return new ProductKnowledgeAuthoringInput(form["sellerId"].ToString(), form["productId"].ToString(),
            form["productName"].ToString(), form["version"].ToString(),
            new KnowledgeSource(KnowledgeSourceType.ManualInput, "KnowledgeConsole",
                form["sourceReference"].ToString(), DateTimeOffset.UtcNow), true,
            capacity.Length == 0 ? null : capacity, Optional(form["material"].ToString()),
            features.Length == 0 ? null : features, Optional(form["size"].ToString()),
            Optional(form["weight"].ToString()), faq.Length == 0 ? null : faq,
            restrictions.Length == 0 ? null : restrictions);
    }
}
