namespace QnocrPoc.RuntimeObserver;

/// <summary>Polls external runtime state without referencing qnbot objects.</summary>
public interface IRuntimeObserver
{
    IReadOnlyList<RuntimeObservedEvent> Poll();
    RuntimeObservedEvent Stop();
}
