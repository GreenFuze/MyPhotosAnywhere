using Mpa.Processor.AppServices;
using Mpa.Processor.Core;
using Mpa.Processor.Credentials;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Lan;

namespace Mpa.Processor.Web;

public sealed class DesktopRuntimeStartException(Exception cause, DesktopWebRuntime retainedOwner)
    : Exception("Desktop startup failed and cleanup is not confirmed; its owner is retained.", cause)
{ public DesktopWebRuntime RetainedOwner { get; } = retainedOwner; }

/// <summary>Engine-lifetime composition root shared by tray and headless hosts. State/native
/// work are borrowed. Owns source dispatch, vault, Google sessions, identity and listeners;
/// borrowed resources remain owned until actual requests/reads have joined.</summary>
public sealed class DesktopWebRuntime : IAsyncDisposable
{
    private readonly ProcessorWorkSupervisor _native;
    private NativeCredentialVault? _vault;
    private ProcessorAccountService? _accounts;
    private LanIdentityOwner? _identity;
    private LibrarySigningIdentityOwner? _signing;
    private DesktopWebHost? _host;
    private ProcessorSourceRuntime? _sources;
    private CatalogViewerSessions? _viewers;
    private readonly object _gate = new();
    private Task? _processingStop;
    private Task? _failureStop;
    private Task? _disposal;
    private Exception? _failure;
    private bool _hostClosed;
    private bool _accountsClosed;
    private bool _identityClosed;
    private bool _signingClosed;
    private bool _vaultClosed;
    private bool _exitConfirmed;
    public Exception? Failure { get { lock (_gate) return _sources?.Failure ?? _failure ?? _native.Failure; } }
    public bool ExitConfirmed { get { lock (_gate) return _exitConfirmed; } }
    public event Action<Exception>? Failed;
    public DesktopWebHost Host => _host ?? throw new InvalidOperationException("The desktop web interface has not started.");
    public ProcessorAccountService Accounts => _accounts ?? throw new InvalidOperationException("The account service has not started.");
    public IProcessorWorkControl Work => _sources is { } sources ? sources.Work : _native;

    private DesktopWebRuntime(ProcessorWorkSupervisor native) => _native = native;
    public static async Task<DesktopWebRuntime> StartAsync(ProcessorStateCoordinator state, Guid installationId, string stateDirectory,
        ProcessorWorkSupervisor work, string oauthConfigurationPath, string assetsDirectory, bool enableLan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        var owner = new DesktopWebRuntime(work);
        try
        {
            var configuration = DesktopOAuthConfiguration.Load(oauthConfigurationPath);
            owner._vault = new NativeCredentialVault(installationId, Path.Combine(stateDirectory, "credentials"));
            var library = await state.ExecuteAsync(database => new ProcessorLibraryRepository(database).GetOrCreateIdentity(), cancellationToken).ConfigureAwait(false);
            owner._identity = new LanIdentityOwner(owner._vault, library, installationId);
            await owner._identity.InitializeAsync(cancellationToken).ConfigureAwait(false);
            owner._accounts = new ProcessorAccountService(state, owner._vault, new GoogleAccountConnectionFactory(configuration, owner._vault),
                new GoogleLibraryConnectionFactory(configuration, owner._vault));
            var storage = new StorageBudgetManager(new ProcessorStateStorageReader(stateDirectory));
            var pin = await state.ExecuteAsync(database => new LibraryAuthorityRepository(database, storage).Controller(), cancellationToken).ConfigureAwait(false);
            owner._signing = new LibrarySigningIdentityOwner(owner._vault, library, installationId, pin);
            await owner._signing.InitializeAsync(cancellationToken).ConfigureAwait(false);
            owner._sources = await ProcessorSourceRuntime.CreatePausedAsync(state, owner._accounts, work, storage, cancellationToken).ConfigureAwait(false);
            owner._sources.Failed += owner.OnFailure;
            owner._viewers = new(state, stateDirectory, installationId, storage);
            var setup = new LibraryWorkspaceSetup(state, owner._accounts, owner._signing, storage);
            var application = new DesktopEngineApplication(library, state, owner._accounts, owner._sources.Work, owner._sources, owner._viewers, setup);
            owner._host = await DesktopWebHost.StartAsync(application, assetsDirectory, enableLan ? owner._identity.Identity : null, cancellationToken).ConfigureAwait(false);
            owner._host.Diagnostic += owner.ReportFailure;
            await owner._sources.ActivateAsync(cancellationToken).ConfigureAwait(false);
            return owner;
        }
        catch (Exception error)
        {
            if (error is ProcessorSourceStartException sources) owner._sources = sources.RetainedOwner;
            try { await owner.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new DesktopRuntimeStartException(new AggregateException(error, cleanup), owner); }
            throw;
        }
    }
    public void ReportFailure(Exception error)
    {
        if (_sources is not null) _sources.ReportFailure(error);
        else OnFailure(error);
    }
    private void OnFailure(Exception error)
    {
        Retain(error);
        lock (_gate) _failureStop ??= Task.Run(async () =>
        {
            try { await StopProcessingAsync().ConfigureAwait(false); }
            catch (Exception shutdown) { Retain(shutdown); }
        }, CancellationToken.None);
        Failed?.Invoke(error);
    }
    private void Retain(Exception error)
    { lock (_gate) _failure = _failure is null ? error : ReferenceEquals(_failure, error) ? _failure : new AggregateException(_failure, error); }

    public Task StopProcessingAsync()
    {
        lock (_gate)
        {
            if (_processingStop is { IsFaulted: true }) _processingStop = null;
            return _processingStop ??= JoinProcessingAsync();
        }
    }
    private async Task JoinProcessingAsync()
    {
        static async Task StopSourcesAsync(ProcessorSourceRuntime? sources)
        { if (sources is not null) await sources.DisposeAsync().ConfigureAwait(false); }
        static async Task StopNativeAsync(ProcessorWorkSupervisor native) => await native.StopAsync().ConfigureAwait(false);
        static async Task StopViewersAsync(CatalogViewerSessions? viewers) { if (viewers is not null) await viewers.DisposeAsync().ConfigureAwait(false); }
        var joined = Task.WhenAll(StopSourcesAsync(_sources), StopNativeAsync(_native), StopViewersAsync(_viewers));
        try { await joined.ConfigureAwait(false); }
        catch { if (joined.Exception is not null) throw joined.Exception; throw; }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is { IsFaulted: true } && !_exitConfirmed) _disposal = null;
            return new(_disposal ??= StopAsync());
        }
    }
    private async Task StopAsync()
    {
        if (!_hostClosed)
        {
            try { if (_host is not null) await _host.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { Retain(error); }
            _hostClosed = _host is null || _host.ExitConfirmed;
        }
        // A listener failure cannot bypass independent background-work shutdown.
        try { await StopProcessingAsync().ConfigureAwait(false); }
        catch (Exception error) { Retain(error); }
        if (!_hostClosed || _sources is { ExitConfirmed: false } || _viewers is { ExitConfirmed: false } || _native.State != ProcessorWorkState.Stopped)
            throw new InvalidOperationException("Desktop requests or processing resources have not joined; account/vault/state ownership is retained.", Failure);
        if (!_accountsClosed) { if (_accounts is not null) await _accounts.DisposeAsync().ConfigureAwait(false); _accountsClosed = true; }
        if (!_identityClosed) { if (_identity is not null) await _identity.DisposeAsync().ConfigureAwait(false); _identityClosed = true; }
        if (!_signingClosed) { if (_signing is not null) await _signing.DisposeAsync().ConfigureAwait(false); _signingClosed = true; }
        if (!_vaultClosed) { if (_vault is not null) await _vault.DisposeAsync().ConfigureAwait(false); _vaultClosed = true; }
        lock (_gate) _exitConfirmed = true;
        // Confirmed joined failures remain sticky and force an unsuccessful app exit.
    }
}
