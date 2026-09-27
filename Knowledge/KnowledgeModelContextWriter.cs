using System.Text.Json;
using System.Text.Json.Nodes;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

namespace QnocrPoc.Knowledge;

/// <summary>Writes a separate model context; the durable original context and MessageEnvelope stay unchanged.</summary>
public static class KnowledgeModelContextWriter
{
    private static readonly HashSet<string> AllowedFacts = new(StringComparer.Ordinal)
    { "Capacity", "Size", "Weight", "Material", "CustomizationPolicy" };

    public static KnowledgeContextView Write(string path, SessionContextView context,
        KnowledgeRequest request, KnowledgeResult result, string status)
    {
        var missingContext = status == KnowledgeStatuses.MissingProductContext &&
            request.MissingProductContext && request.ProductReference is null;
        if (request.SessionId != context.SessionId || request.MessageId != context.MessageId ||
            request.CaptureId != context.CaptureId || !missingContext && request.ProductReference?.ProductId !=
                (string.IsNullOrWhiteSpace(context.ProductId) ? null : context.ProductId))
            throw new InvalidDataException("Knowledge request differs from the current message context.");
        if (context.ProductContextBindingStatus == ProductContextBindingStatus.Bound &&
            (context.CurrentProductContext is null ||
             context.CurrentProductContext.ProductId != context.ProductId ||
             context.CurrentProductContext.SellerId != request.SellerId))
            throw new InvalidDataException("Bound product sidecar differs from model context identity.");
        var available = status == KnowledgeStatuses.Available && result.Success &&
            !string.IsNullOrWhiteSpace(request.SellerId) && result.SellerId == request.SellerId &&
            !string.IsNullOrWhiteSpace(result.ProductVersion) && !result.MissingProduct &&
            result.Source == "LocalTestProductConfig" && result.ReturnedFields is { Count: > 0 } &&
            !string.IsNullOrWhiteSpace(context.ProductId) && result.MissingFields.Count == 0 &&
            result.ReturnedFields.Keys.All(AllowedFacts.Contains) &&
            request.NeedFields.All(result.ReturnedFields.ContainsKey) &&
            result.ReturnedFields.Values.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 200 &&
                !value.Any(ch => char.IsControl(ch) || ch is '<' or '>' or '{' or '}'));
        if (status == KnowledgeStatuses.Available && !available)
            throw new InvalidDataException("Only complete, bounded local test facts can enter AI context.");
        if (status is not (KnowledgeStatuses.Available or KnowledgeStatuses.Missing or KnowledgeStatuses.MissingProductContext) ||
            status == KnowledgeStatuses.MissingProductContext && !missingContext)
            throw new InvalidDataException("Only required knowledge may produce a model context.");
        var facts = available ? result.ReturnedFields! : new Dictionary<string, string>(StringComparer.Ordinal);
        var view = new KnowledgeContextView(status,
            missingContext ? "" : context.ProductId, available ? result.Source : "None", facts,
            available ? Array.Empty<string>() : result.MissingFields, available, available ? result.UpdatedAt : null,
            request.SellerId, available ? result.ProductVersion : null, result.MissingProduct || missingContext,
            result.KnowledgeCategory);
        var node = JsonSerializer.SerializeToNode(context)?.AsObject()
            ?? throw new InvalidDataException("Session context cannot be serialized.");
        node["Knowledge"] = JsonSerializer.SerializeToNode(view);
        AtomicJson.Write(path, node);
        return view;
    }
}
