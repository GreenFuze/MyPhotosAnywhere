using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Trusted installation description, supplied by native composition,
/// never a web command. Only the fixed installed Takeout analysis is selected.</summary>
public sealed partial class TakeoutWorkerInstallation
{
    private readonly string _runtime;
    private readonly string _runtimeHash;
    private readonly string _assembly;
    private readonly string _assemblyHash;
    private readonly bool _appHost;
    public TakeoutWorkerInstallation(string runtimePath,string runtimeSha256,string assemblyPath,string assemblySha256)
    {
        if(!System.IO.Path.IsPathFullyQualified(runtimePath)||!System.IO.Path.IsPathFullyQualified(assemblyPath)||System.IO.Path.GetFileName(assemblyPath)!="Mpa.Processor.WorkerHost.dll"||!Hash(runtimeSha256)||!Hash(assemblySha256))
            throw new ArgumentException("The exact installed runtime and first-party worker hashes are required.");
        _runtime=System.IO.Path.GetFullPath(runtimePath);_runtimeHash=runtimeSha256;_assembly=System.IO.Path.GetFullPath(assemblyPath);_assemblyHash=assemblySha256;
    }
    private TakeoutWorkerInstallation(string hostPath,string hostSha256,string assemblyPath,string assemblySha256,bool appHost)
        :this(hostPath,hostSha256,assemblyPath,assemblySha256)
    {
        var expected=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_assembly)!,OperatingSystem.IsWindows()?"Mpa.Processor.WorkerHost.exe":"Mpa.Processor.WorkerHost");
        if(!appHost||!string.Equals(_runtime,expected,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
            throw new ArgumentException("The installed first-party apphost must share its exact worker assembly directory.",nameof(hostPath));
        _appHost=true;
    }
    public static TakeoutWorkerInstallation ForAppHost(string hostPath,string hostSha256,string assemblyPath,string assemblySha256)
        =>new(hostPath,hostSha256,assemblyPath,assemblySha256,true);
    internal async Task<FileStream> OpenAssemblyAsync(CancellationToken token)
    {
        TakeoutMetadataStageStorage.Regular(System.IO.Path.GetDirectoryName(_assembly)!,true);
        var file=TakeoutMetadataStageStorage.OpenRead(_assembly);
        try
        {
            if(Convert.ToHexStringLower(await SHA256.HashDataAsync(file,token).ConfigureAwait(false))!=_assemblyHash)throw new InvalidDataException("The installed metadata worker changed; retain pending work and repair its installation.");
            return file;
        }
        catch {await file.DisposeAsync().ConfigureAwait(false);throw;}
    }
    internal WorkerLaunchSpec Launch(Guid session,string staging)=>new(_runtime,_runtimeHash,
        _appHost?["--session",session.ToString("D"),"--staging",staging]:[_assembly,"--session",session.ToString("D"),"--staging",staging],
        System.IO.Path.GetDirectoryName(_assembly)!,System.IO.Path.Combine(staging,"diagnostics.log"));
    private static bool Hash(string? value)=>value is {Length:64}&&value.All(c=>c is >='0' and <='9' or >='a' and <='f');
}

/// <summary>One actual metadata attempt. A valid framed result, independently
/// held bytes, exact semantic recomputation, exited acknowledgement and joined
/// native child all precede the fenced SQLite ready/outbox transaction.</summary>
public sealed class TakeoutMetadataWorkerOperation : OwnedWorkerOperation
{
    private readonly ProcessorStateCoordinator _state;
    private readonly TakeoutMetadataWork _work;
    private readonly TakeoutMetadataAttemptStage _stage;
    private readonly FileStream _assembly;
    private readonly WorkerMessageCodec _codec;
    private readonly WorkerConversation _conversation=new();
    private readonly WorkerAttempt _attempt;
    private FramedWorkerProcess? _worker;
    private FileStream? _result;
    private TakeoutMetadataDocument? _document;
    private ArtifactDescriptor? _artifact;
    private string? _issue;
    private bool _exited;
    private bool _terminal;
    private bool _closed;
    private bool _executed;
    public override WorkerLaunchSpec Launch { get; }
    private TakeoutMetadataWorkerOperation(ProcessorStateCoordinator state,TakeoutMetadataWork work,TakeoutMetadataAttemptStage stage,FileStream assembly,TakeoutWorkerInstallation installation)
    {
        _state=state;_work=work;_stage=stage;_assembly=assembly;
        var session=Guid.NewGuid();_codec=new(session);_attempt=new(work.Lease.JobId,work.Lease.ItemId,work.Lease.LeaseId);Launch=installation.Launch(session,stage.Path);
    }
    public static async Task<TakeoutMetadataWorkerOperation> CreateAsync(ProcessorStateCoordinator state,TakeoutMetadataWork work,TakeoutMetadataStageStorage storage,TakeoutWorkerInstallation installation,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(state);ArgumentNullException.ThrowIfNull(work);ArgumentNullException.ThrowIfNull(storage);ArgumentNullException.ThrowIfNull(installation);
        await state.ExecuteAsync(database=>{new TakeoutMetadataJobRepository(database).AssertCurrent(work);return true;},token).ConfigureAwait(false);
        FileStream? assembly=null;TakeoutMetadataAttemptStage? stage=null;
        try
        {
            assembly=await installation.OpenAssemblyAsync(token).ConfigureAwait(false);
            stage=await storage.OpenAttemptAsync(work,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
            return new(state,work,stage,assembly,installation);
        }
        catch {stage?.Dispose();if(assembly is not null)await assembly.DisposeAsync().ConfigureAwait(false);throw;}
    }

    public override async Task ExecuteAsync(FramedWorkerProcess worker,CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worker);
        if(_executed||_closed)throw new InvalidOperationException("A captured metadata operation can execute only once.");
        _executed=true;_worker=worker;
        await _state.ExecuteAsync(database=>{new TakeoutMetadataJobRepository(database).AssertCurrent(_work);return true;},cancellationToken).ConfigureAwait(false);
        await using var frames=worker.ReadFramesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        await SendAsync("hello",new {pluginId="mpa.takeout-metadata",pluginVersion="0.6.0"},null).ConfigureAwait(false);
        var capabilities=await ReceiveAsync().ConfigureAwait(false);Exact(capabilities.Payload,"pluginId","pluginVersion","recipeSha256","capabilities");
        if(capabilities.Type!="capabilities"||Text(capabilities.Payload,"pluginId")!="mpa.takeout-metadata"||Text(capabilities.Payload,"pluginVersion")!="0.6.0"||Text(capabilities.Payload,"recipeSha256")!=TakeoutMetadataDocument.RecipeSha256)
            throw new ProtocolCompatibilityException("The installed metadata analysis changed its exact recipe or identity.");
        var features=capabilities.Payload.GetProperty("capabilities");
        if(features.ValueKind!=JsonValueKind.Array||features.GetArrayLength()!=2||features[0].GetString()!="takeout-media-metadata"||features[1].GetString()!="exact-sidecar-provenance")
            throw new ProtocolCompatibilityException("The installed metadata analysis changed its capabilities.");
        _stage.Recheck();
        await SendAsync("prepare",new {recipeSha256=TakeoutMetadataDocument.RecipeSha256,input=new {relativePath="input.bin",bytes=_work.Sidecar.Bytes.ToString(CultureInfo.InvariantCulture),sha256=_work.Sidecar.Sha256}},_attempt).ConfigureAwait(false);
        var message=await ReceiveAsync().ConfigureAwait(false);
        if(message.Type=="prepared")
        {
            Exact(message.Payload);_stage.Recheck();await SendAsync("run",new {},_attempt).ConfigureAwait(false);
            message=await ReceiveAsync().ConfigureAwait(false);
        }
        if(message.Type=="result")
        {
            Exact(message.Payload,"artifacts");var artifacts=message.Payload.GetProperty("artifacts");
            if(artifacts.ValueKind!=JsonValueKind.Array||artifacts.GetArrayLength()!=1)throw new ProtocolFormatException("Metadata requires one bounded artifact.");
            var value=artifacts[0];Exact(value,"relativePath","bytes","sha256");var size=Text(value,"bytes");
            if(!long.TryParse(size,NumberStyles.None,CultureInfo.InvariantCulture,out var bytes)||bytes.ToString(CultureInfo.InvariantCulture)!=size)throw new ProtocolFormatException("Noncanonical metadata artifact size.");
            _artifact=new(bytes,Text(value,"sha256"),Text(value,"relativePath"));_stage.Recheck();
            _result=_stage.OpenResult(_artifact);
            await new ArtifactVerifier(TakeoutMetadataDocument.MaximumArtifactBytes).VerifyAsync(_result,_artifact,cancellationToken).ConfigureAwait(false);
            _result.Position=0;var encoded=new byte[checked((int)_artifact.Bytes)];await _result.ReadExactlyAsync(encoded,cancellationToken).ConfigureAwait(false);
            _document=TakeoutMetadataDocument.ReadVerifiedArtifact(encoded,_work.Sidecar);
        }
        else if(message.Type=="failure")
        {
            Exact(message.Payload,"code","retryable","stage");_issue=Text(message.Payload,"code");
            if(_issue is not("metadata-too-large" or "invalid-metadata" or "unsupported-metadata")||message.Payload.GetProperty("retryable").ValueKind!=JsonValueKind.False||Text(message.Payload,"stage") is not("prepare" or "run"))
                throw new ProtocolFormatException("Metadata worker returned an unrecognized failure; retain evidence.");
        }
        else throw new ProtocolFormatException("Metadata worker did not return its exact result or operational failure.");
        await SendAsync("exit",new {},null).ConfigureAwait(false);
        var exit=await ReceiveAsync().ConfigureAwait(false);if(exit.Type!="exited")throw new ProtocolFormatException("Metadata worker did not acknowledge exit.");Exact(exit.Payload);
        // Drain the actual consumer too: a trailing frame or malformed tail cannot
        // hide behind an otherwise successful exit message.
        if(await frames.MoveNextAsync().ConfigureAwait(false))throw new ProtocolFormatException("Metadata worker sent bytes after its acknowledged exit.");
        await worker.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);_exited=true;
        if(new FileInfo(Launch.NewDiagnosticPath).Length!=0)throw new InvalidDataException("The metadata worker emitted unresolved diagnostics; evidence was retained.");
        await _state.ExecuteAsync(database=>
        {
            cancellationToken.ThrowIfCancellationRequested();var repository=new TakeoutMetadataJobRepository(database);
            repository.AssertCurrent(_work);
            AdmitDurableResult(()=>
            {
                if(_issue is null)repository.CompleteAfterJoinedWorker(_work,this);else repository.FailAfterJoinedWorker(_work,this);
                _terminal=true;
            },cancellationToken);
            return true;
        },cancellationToken).ConfigureAwait(false);

        async Task SendAsync<T>(string type,T payload,WorkerAttempt? attempt)
        {
            cancellationToken.ThrowIfCancellationRequested();var value=JsonSerializer.SerializeToElement(payload);
            _conversation.Observe(WorkerEndpoint.Core,new(type,value,attempt));await worker.SendAsync(_codec.Encode(type,value,attempt),cancellationToken).ConfigureAwait(false);
        }
        async Task<WorkerMessage> ReceiveAsync()
        {
            if(!await frames.MoveNextAsync().ConfigureAwait(false))throw new ProtocolFormatException("Metadata worker exited before its expected message.");
            var reply=_codec.Read(frames.Current);_conversation.Observe(WorkerEndpoint.Worker,reply);return reply;
        }
    }

    internal (ArtifactDescriptor Artifact,TakeoutMetadataDocument Document) VerifiedResult(TakeoutMetadataWork work)
    {
        AssertJoined(work);
        if(_issue is not null||_artifact is null||_document is null||_result is null||!_result.CanRead)throw new InvalidOperationException("No independently held verified metadata result exists.");
        return(_artifact,_document);
    }
    internal string VerifiedIssue(TakeoutMetadataWork work)
    {AssertJoined(work);return _issue??throw new InvalidOperationException("No acknowledged metadata issue exists.");}
    private void AssertJoined(TakeoutMetadataWork work)
    {
        if(!ReferenceEquals(work,_work)||_closed||!_exited||_conversation.Phase!=WorkerPhase.Closed||_worker?.Completion.IsCompletedSuccessfully!=true)
            throw new InvalidOperationException("The exact metadata protocol, native process and I/O have not joined successfully.");
    }
    public override async ValueTask DisposeAsync()
    {
        if(_closed)return;
        if(_worker is not null&&!_worker.RetirementConfirmed)throw new InvalidOperationException("Retain metadata staging until the exact worker retires.");
        if(!_terminal)
        {
            // This is invoked by the supervisor only after physical retirement,
            // or by the caller when admission failed and no child was launched.
            await _state.ExecuteAsync(database=>{new TakeoutMetadataJobRepository(database).ReturnJoinedOperation(_work);return true;},CancellationToken.None).ConfigureAwait(false);
            _terminal=true;
        }
        if(_result is not null)await _result.DisposeAsync().ConfigureAwait(false);
        await _assembly.DisposeAsync().ConfigureAwait(false);_stage.Dispose();_closed=true;
    }
    private static string Text(JsonElement value,string name)=>value.TryGetProperty(name,out var field)&&field.ValueKind==JsonValueKind.String?field.GetString()!:throw new ProtocolFormatException("Missing metadata worker field: "+name);
    private static void Exact(JsonElement value,params string[] names)
    {if(value.ValueKind!=JsonValueKind.Object||value.EnumerateObject().Count()!=names.Length||names.Any(name=>!value.TryGetProperty(name,out _)))throw new ProtocolFormatException("Unexpected metadata worker payload fields.");}
}
