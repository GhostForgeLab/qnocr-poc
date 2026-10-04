using QnocrPoc.Decision;
using QnocrPoc.Models;

namespace QnocrPoc.Extension.AgentContext;

/// <summary>Maps an accepted Agent snapshot to the existing classifier contract; never borrows other sessions.</summary>
public static class AgentContextIntentAdapter
{
    public static SessionContextView Build(AgentSession session, string messageId, DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        if (session.Status != AgentSessionStatus.Active || session.Messages.Any(message =>
            message.SessionId != session.SessionId || message.Source != session.Identity.Source))
            throw new InvalidDataException("Intent requires a current, isolated Agent session.");
        var index = Enumerable.Range(0, session.Messages.Count)
            .Single(i => session.Messages[i].MessageId == messageId);
        var current = session.Messages[index];
        if (!current.Role.Equals("buyer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Intent requires an accepted buyer message.");

        // Append order is authoritative. A batch's later messages must not leak into an earlier request.
        var recent = session.Messages.Take(index).Where(message =>
                message.Role.Equals("buyer", StringComparison.OrdinalIgnoreCase) ||
                message.Role.Equals("seller", StringComparison.OrdinalIgnoreCase))
            .TakeLast(JEVIntentClassifier.MaxRecentMessages).Reverse().ToArray();
        var remaining = JEVIntentClassifier.MaxContextCharacters - JEVIntentClassifier.MaxBuyerQuestionCharacters;
        var bounded = new List<EnvelopeMessage>();
        foreach (var prior in recent)
        {
            var length = Math.Min(prior.Content.Length, Math.Min(remaining, JEVIntentClassifier.MaxBuyerQuestionCharacters));
            if (length == 0) break;
            // Do not cut a UTF-16 surrogate pair at the boundary.
            if (length < prior.Content.Length && char.IsHighSurrogate(prior.Content[length - 1])) length--;
            if (length == 0) break;
            bounded.Add(new EnvelopeMessage(prior.Role, prior.Content[..length], prior.Timestamp));
            remaining -= length;
        }
        bounded.Reverse();
        var history = Array.AsReadOnly(bounded.ToArray());
        var messages = Array.AsReadOnly(bounded.Append(
            new EnvelopeMessage(current.Role, current.Content, current.Timestamp)).ToArray());
        // The current question is never truncated. The existing provider rejects >1000 chars and falls back.
        return new SessionContextView(session.SessionId, session.Identity.BuyerId, "", messages,
            capturedAt, "", current.MessageId, "", messages.Count,
            CreatedAt: session.CreatedTime, LastActivityAt: session.LastActiveTime)
        { IntentRecentMessages = history };
    }
}
