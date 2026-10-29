namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentControlReader
{
    private readonly SemaphoreSlim _stateAdmissionSerial=new(1,1);

    // A state-only callback may already own the actual account lease. Never take
    // the network reader lane here: a concurrent scan can hold it while awaiting
    // that same account. SQLite remains serialized by the real state owner, and
    // reader retirement separately joins this lane before releasing its lifetime.
    private async Task<T> StateOnlyOperationAsync<T>(Func<CancellationToken,Task<T>> action,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(action);
        CancellationTokenSource linked;
        lock(_gate){ObjectDisposedException.ThrowIf(_stopping,this);linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token);}
        using(linked)
        {
            await _stateAdmissionSerial.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                Check(caller,linked.Token);var result=await action(linked.Token).ConfigureAwait(false);
                lock(_gate){Check(caller,linked.Token);_failure=null;return result;}
            }
            catch(Exception error){lock(_gate) _failure=error;throw;}
            finally{_stateAdmissionSerial.Release();}
        }
    }
}
