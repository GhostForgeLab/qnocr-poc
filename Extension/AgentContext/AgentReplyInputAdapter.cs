using System.Text.Json;
using System.Text.Json.Nodes;
using QnocrPoc.Knowledge;
using QnocrPoc.Models;

namespace QnocrPoc.Extension.AgentContext;

/// <summary>Current Agent facts to the existing context/Prompt wire. No identity mapping or historical lookup.</summary>
public static class AgentReplyInputAdapter
{
    public static SessionContextView Build(AgentSession session, AgentKnowledgeMessageResult result, string requestId)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(result);
        if (session.Status != AgentSessionStatus.Active || result.SessionId != session.SessionId ||
            result.Intent.SessionId != session.SessionId || result.MessageId != result.Intent.MessageId ||
            result.SellerId != session.Identity.SellerId || result.Intent.Intent is null)
            throw new InvalidDataException("Reply associations differ from the accepted active Agent session.");
        var context = AgentContextIntentAdapter.Build(session, result.MessageId, result.Timestamp);
        if (context.Messages[^1].Text.Length > 1000 || context.Messages.Sum(message => message.Text.Length) > 3000)
            throw new InvalidDataException("Reply dialogue exceeds the existing Context window budget.");
        var bound = result.ProductContext.Current;
        if (bound is not null && (bound.SessionId != session.SessionId || bound.SellerId != result.SellerId ||
            bound.ProductId != result.ProductId))
            throw new InvalidDataException("Reply product differs from its Knowledge association.");
        return context with
        {
            AiRequestId = requestId, CaptureId = result.Request?.CaptureId ?? "",
            Messages = context.Messages.Select(message => message with { Role = message.Role.ToLowerInvariant() }).ToArray(),
            ProductId = result.ProductId ?? "", CurrentProductContext = bound,
            ProductContextBindingStatus = result.ProductContext.Status,
            ProductReferences = result.Request?.ProductReference is { } reference ? [reference] : [],
            Identity = new BuyerIdentity(session.Identity.BuyerId, session.Identity.Source, null,
                result.Timestamp, session.SessionId, BuyerIdentityStatuses.Observed)
        };
    }

    public static void Write(string path, SessionContextView context, AgentKnowledgeMessageResult result)
    {
        JsonObject node;
        if (result.QueryStatus == KnowledgeStatuses.Available)
        {
            var request = result.Request ?? throw new InvalidDataException("Published Knowledge request is missing.");
            var knowledge = result.Knowledge ?? throw new InvalidDataException("Published Knowledge result is missing.");
            if (request.SessionId != context.SessionId || request.MessageId != context.MessageId ||
                request.SellerId != result.SellerId || request.ProductReference?.ProductId != context.ProductId ||
                result.ProductContext.Status != ProductContextBindingStatus.Bound || result.Error is not null)
                throw new InvalidDataException("Reply Knowledge does not match the current product.");
            KnowledgeModelContextWriter.Write(path, context, request, knowledge, result.QueryStatus, publishedPreview: true);
            node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            node["KnowledgeId"] = knowledge.KnowledgeId;
            node["KnowledgeReference"] = knowledge.ItemSource?.Reference;
        }
        else if (result.QueryStatus == KnowledgeStatuses.NotRequired && result.Intent.Intent?.IntentName == "OTHER")
            node = JsonSerializer.SerializeToNode(context)!.AsObject();
        else throw new InvalidDataException("Only Published facts or non-product OTHER intent may reach AI.");
        node["SellerId"] = result.SellerId;
        node["SessionStatus"] = AgentSessionStatus.Active.ToString();
        node["IntentName"] = result.Intent.Intent!.IntentName;
        node["IntentConfidence"] = result.Intent.Intent.Confidence;
        node["IntentProvider"] = result.Intent.Provider;
        QnocrPoc.Shadow.AtomicJson.Write(path, node);
    }
}
