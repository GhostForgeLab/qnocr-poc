namespace QnocrPoc.Extension.AgentContext;

/// <summary>Versioned persistence contract shared by SQLite writes and manager startup validation.</summary>
internal static class AgentSessionPersistenceModel
{
    internal const int SchemaVersion = 1;

    internal static AgentSession Snapshot(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.SessionId) || session.Identity is null ||
            string.IsNullOrWhiteSpace(session.Identity.Source) ||
            string.IsNullOrWhiteSpace(session.Identity.SellerId) ||
            string.IsNullOrWhiteSpace(session.Identity.BuyerId) ||
            session.Identity.IdentityLevel != IdentityLevel.Observed ||
            !Enum.IsDefined(session.Status) || session.CreatedTime == default ||
            session.LastActiveTime < session.CreatedTime || session.Messages is null || session.StateHistory is null)
            throw new InvalidDataException("Invalid persisted session metadata.");

        var messages = session.Messages.ToArray();
        var history = session.StateHistory.ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (messages.Length == 0 || messages.Any(message => message is null ||
            string.IsNullOrWhiteSpace(message.MessageId) || !ids.Add(message.MessageId) ||
            message.SessionId != session.SessionId || message.Source != session.Identity.Source ||
            message.Timestamp == default || string.IsNullOrWhiteSpace(message.Role) ||
            string.IsNullOrWhiteSpace(message.Content)))
            throw new InvalidDataException("Invalid persisted session messages.");
        if (history.Length != (int)session.Status + 1)
            throw new InvalidDataException("Invalid persisted session lifecycle length.");
        for (var index = 0; index < history.Length; index++)
        {
            var change = history[index];
            if (change is null || change.To != (AgentSessionStatus)index ||
                change.From != (index == 0 ? null : (AgentSessionStatus?)(index - 1)) ||
                string.IsNullOrWhiteSpace(change.Reason) ||
                (index == 0 ? change.ChangedTime != session.CreatedTime :
                    change.ChangedTime < history[index - 1].ChangedTime || change.ChangedTime < session.LastActiveTime))
                throw new InvalidDataException("Invalid persisted session lifecycle history.");
        }
        return session with { Messages = Array.AsReadOnly(messages), StateHistory = Array.AsReadOnly(history) };
    }
}
