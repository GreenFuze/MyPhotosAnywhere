namespace Mpa.Processor.Protocol;

public enum WorkerEndpoint { Core, Worker }
public enum WorkerPhase { Created, AwaitingCapabilities, Ready, Preparing, Prepared, Running, Canceling, Exiting, Closed, Faulted }
public enum WorkerMessageDisposition { Accepted, FencedAfterCancellation }

/// <summary>Serial role/state validation after envelope validation. Canceled results never
/// become publishable; the exact cancellation acknowledgement is still required.</summary>
public sealed class WorkerConversation
{
    public WorkerPhase Phase { get; private set; }
    private WorkerAttempt? _active;
    private WorkerAttempt? _lastCompleted;

    public WorkerMessageDisposition Observe(WorkerEndpoint sender, WorkerMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (Phase is WorkerPhase.Faulted or WorkerPhase.Closed) throw new InvalidOperationException("Worker conversation has ended.");
        try { return Apply(sender, message); }
        catch { Phase = WorkerPhase.Faulted; throw; }
    }

    private WorkerMessageDisposition Apply(WorkerEndpoint sender, WorkerMessage message)
    {
        var type = message.Type;
        if (sender == WorkerEndpoint.Core && type == "hello" && Phase == WorkerPhase.Created && message.Attempt is null)
            Phase = WorkerPhase.AwaitingCapabilities;
        else if (sender == WorkerEndpoint.Worker && type == "capabilities" && Phase == WorkerPhase.AwaitingCapabilities && message.Attempt is null)
            Phase = WorkerPhase.Ready;
        else if (sender == WorkerEndpoint.Core && type == "prepare" && Phase == WorkerPhase.Ready && message.Attempt is not null)
        {
            message.Attempt.Validate(); _active = message.Attempt; _lastCompleted = null; Phase = WorkerPhase.Preparing;
        }
        else if (sender == WorkerEndpoint.Worker && type == "prepared" && Phase == WorkerPhase.Preparing && Matches(message))
            Phase = WorkerPhase.Prepared;
        else if (sender == WorkerEndpoint.Core && type == "run" && Phase == WorkerPhase.Prepared && Matches(message))
            Phase = WorkerPhase.Running;
        else if (sender == WorkerEndpoint.Worker && (type is "progress" or "checkpoint") && (Phase is WorkerPhase.Running or WorkerPhase.Canceling) && Matches(message))
            return Phase == WorkerPhase.Canceling ? WorkerMessageDisposition.FencedAfterCancellation : WorkerMessageDisposition.Accepted;
        else if (sender == WorkerEndpoint.Worker && (type is "result" or "failure") &&
            ((Phase is WorkerPhase.Running or WorkerPhase.Canceling) || type == "failure" && (Phase is WorkerPhase.Preparing or WorkerPhase.Prepared)) && Matches(message))
        {
            if (Phase == WorkerPhase.Canceling) return WorkerMessageDisposition.FencedAfterCancellation;
            _lastCompleted = _active; _active = null; Phase = WorkerPhase.Ready;
        }
        else if (sender == WorkerEndpoint.Core && type == "cancel" && message.Attempt is not null &&
            ((Phase is WorkerPhase.Preparing or WorkerPhase.Prepared or WorkerPhase.Running) && Matches(message) ||
             Phase == WorkerPhase.Ready && _lastCompleted == message.Attempt))
        {
            _active = message.Attempt; Phase = WorkerPhase.Canceling;
        }
        else if (sender == WorkerEndpoint.Worker && type == "canceled" && Phase == WorkerPhase.Canceling && Matches(message))
        {
            _lastCompleted = _active; _active = null; Phase = WorkerPhase.Ready;
        }
        else if (sender == WorkerEndpoint.Core && type == "exit" && Phase == WorkerPhase.Ready && message.Attempt is null)
            Phase = WorkerPhase.Exiting;
        else if (sender == WorkerEndpoint.Worker && type == "exited" && Phase == WorkerPhase.Exiting && message.Attempt is null)
            Phase = WorkerPhase.Closed;
        else throw new ProtocolFormatException("Worker message violates role, phase, or active attempt.");
        return WorkerMessageDisposition.Accepted;
    }

    private bool Matches(WorkerMessage message) => _active is not null && _active == message.Attempt;
}
