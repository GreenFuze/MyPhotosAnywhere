using Mpa.Processor.AppServices;
using Mpa.Processor.Core;
using Mpa.Processor.Credentials;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Lan;

namespace Mpa.Processor.Web;

/// <summary>Engine-lifetime composition root shared by tray and headless hosts. State/work are
/// borrowed; vault, Google sessions, identity and browser listeners are joined before return.</summary>
public sealed class DesktopWebRuntime : IAsyncDisposable
{
    private NativeCredentialVault? _vault;
    private ProcessorAccountService? _accounts;
    private DesktopEngineApplication? _application;
    private LanIdentityOwner? _identity;
    private DesktopWebHost? _host;
    private readonly object _gate = new();
    private Task? _disposal;
    public Exception? Failure { get; private set; }
    public event Action<Exception>? Failed;
    public DesktopWebHost Host => _host ?? throw new InvalidOperationException("The desktop web interface has not started.");
    public ProcessorAccountService Accounts => _accounts ?? throw new InvalidOperationException("The account service has not started.");

    private DesktopWebRuntime() { }
    public static async Task<DesktopWebRuntime> StartAsync(ProcessorStateCoordinator state, Guid installationId, string stateDirectory,
        ProcessorWorkSupervisor work, string oauthConfigurationPath, string assetsDirectory, bool enableLan, CancellationToken cancellationToken)
    {
        var owner = new DesktopWebRuntime();
        try
        {
            var configuration = DesktopOAuthConfiguration.Load(oauthConfigurationPath);
            owner._vault = new NativeCredentialVault(installationId, Path.Combine(stateDirectory, "credentials"));
            var library = await state.ExecuteAsync(database => new ProcessorLibraryRepository(database).GetOrCreateIdentity(), cancellationToken).ConfigureAwait(false);
            owner._identity = new LanIdentityOwner(owner._vault, library, installationId);
            await owner._identity.InitializeAsync(cancellationToken).ConfigureAwait(false);
            owner._accounts = new ProcessorAccountService(state, owner._vault, new GoogleAccountConnectionFactory(configuration, owner._vault));
            owner._application = new DesktopEngineApplication(library, state, owner._accounts, configuration, owner._vault, work);
            owner._host = await DesktopWebHost.StartAsync(owner._application, assetsDirectory, enableLan ? owner._identity.Identity : null, cancellationToken).ConfigureAwait(false);
            owner._host.Diagnostic += error => { owner.Failure = error; owner.Failed?.Invoke(error); };
            return owner;
        }
        catch { await owner.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposal ??= StopAsync());
    }
    private async Task StopAsync()
    {
        if (_host is not null) await _host.DisposeAsync().ConfigureAwait(false);
        if (_application is not null) await _application.DisposeAsync().ConfigureAwait(false);
        if (_accounts is not null) await _accounts.DisposeAsync().ConfigureAwait(false);
        if (_identity is not null) await _identity.DisposeAsync().ConfigureAwait(false);
        if (_vault is not null) await _vault.DisposeAsync().ConfigureAwait(false);
    }
}
