namespace Mpa.Processor.GoogleDrive;

public sealed partial class GoogleAccountConnection
{
    private MetadataNetworkAdmission? _metadataAdmission;

    // ExecuteAsync already owns the connection's serial operation. The context is
    // lexical and retired BEFORE its serial lease, never cached for a later call.
    private Task<T> ExecuteAdmittedMetadataAsync<T>(Func<CancellationToken,Task<T>> operation,
        Func<CancellationToken,Task> admit,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(operation);ArgumentNullException.ThrowIfNull(admit);
        return ExecuteAsync(async token=>
        {
            using var admission=new MetadataNetworkAdmission(this,admit);
            return await operation(token).ConfigureAwait(false);
        },caller);
    }

    // Replaces only the old SendOwnedAsync body. With no admitted context it is
    // exactly the existing transport gateway; no old API gains a new authority.
    private Task<HttpResponseMessage> SendMetadataAwareOwnedAsync(HttpRequestMessage request,CancellationToken token)
    {
        MetadataNetworkAdmission? admission;
        lock(_gate) admission=_metadataAdmission;
        return admission is null ? AdmitNetwork(()=>_http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token),token)
            : SendAdmittedMetadataOwnedAsync(admission,request,token);
    }
    private async Task<HttpResponseMessage> SendAdmittedMetadataOwnedAsync(MetadataNetworkAdmission admission,
        HttpRequestMessage request,CancellationToken token)
    {
        await admission.BeforeSendAsync(token).ConfigureAwait(false);
        // Token/shutdown are checked AGAIN inside the original final gateway.
        return await AdmitNetwork(()=>_http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token),token).ConfigureAwait(false);
    }

    private sealed class MetadataNetworkAdmission : IDisposable
    {
        private readonly GoogleAccountConnection _owner;
        private readonly Func<CancellationToken,Task> _admit;
        private int _disposed;
        internal MetadataNetworkAdmission(GoogleAccountConnection owner,Func<CancellationToken,Task> admit)
        {
            _owner=owner;_admit=admit;
            lock(owner._gate)
            {
                if(owner._metadataAdmission is not null) throw new InvalidOperationException("Metadata admission cannot nest or escape its operation.");
                owner._metadataAdmission=this;
            }
        }
        internal async Task BeforeSendAsync(CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed)!=0,this);
            lock(_owner._gate) if(!ReferenceEquals(_owner._metadataAdmission,this)) throw new InvalidOperationException("Metadata admission lost its exact connection owner.");
            token.ThrowIfCancellationRequested();await _admit(token).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed)!=0,this);token.ThrowIfCancellationRequested();
        }
        public void Dispose()
        {
            if(Interlocked.Exchange(ref _disposed,1)!=0) return;
            lock(_owner._gate)
            {
                if(!ReferenceEquals(_owner._metadataAdmission,this)) throw new InvalidOperationException("Metadata admission retired a different operation.");
                _owner._metadataAdmission=null;
            }
        }
    }
}
