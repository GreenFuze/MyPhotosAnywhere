using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Google.Apis.Auth.OAuth2.Responses;
using Mpa.Processor.AppServices;
using Mpa.Processor.Core;
using Mpa.Processor.Credentials;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.Desktop;

/// <summary>Small account setup view. Background owners never await UI dispatch during shutdown.</summary>
internal sealed class ProcessorAccountPanel : StackPanel, IAsyncDisposable
{
    private readonly TextBlock _message = new() { Text = "Opening Google connection settings…", TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _entries = new() { Spacing = 12 };
    private readonly Button _connect = Button("Connect Google Drive");
    private readonly Button _cancel = Button("Cancel connection");
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<Exception> _fatal;
    private readonly object _gate = new();
    private ProcessorAccountService? _service;
    private NativeCredentialVault? _vault;
    private Task? _startup;
    private Task? _action;
    private Task? _disposal;
    private CancellationTokenSource? _actionCancellation;
    private bool _stopping;
    private bool _busy;
    private bool _failed;
    public Exception? Failure { get; private set; }
    public event Action<ProcessorAccountSnapshot>? StateChanged;

    public ProcessorAccountPanel(Action<Exception> fatal)
    {
        _fatal = fatal; Spacing = 12;
        Children.Add(new TextBlock { Text = "Google Drive", FontSize = 20, FontWeight = FontWeight.SemiBold });
        Children.Add(_message); Children.Add(_entries); Children.Add(_connect); Children.Add(_cancel);
        _connect.IsEnabled = false; _cancel.IsVisible = false;
        _connect.Click += (_, _) => StartAction(async token =>
        {
            await Service.ConnectAsync(null, new SystemAuthorizationBrowser(), token).ConfigureAwait(false);
            await Service.CleanCredentialsAsync(token).ConfigureAwait(false);
            return "Google account connected.";
        });
        _cancel.Click += (_, _) => _actionCancellation?.Cancel();
    }

    private ProcessorAccountService Service => _service ?? throw new InvalidOperationException("Account service has not initialized.");

    public Task InitializeAsync(ProcessorStateCoordinator state, Guid installationId, string stateDirectory)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            return _startup ??= Task.Run(async () =>
            {
                try
                {
                    var configurationPath = Path.Combine(AppContext.BaseDirectory, "MPA.Processor.oauth.json");
                    if (!File.Exists(configurationPath))
                    {
                        Post(() => _message.Text = "Google connection is unavailable in this package. Install the complete Processor package.");
                        return;
                    }
                    var configuration = DesktopOAuthConfiguration.Load(configurationPath);
                    _vault = new NativeCredentialVault(installationId, Path.Combine(stateDirectory, "credentials"));
                    _service = new ProcessorAccountService(state, _vault, new GoogleAccountConnectionFactory(configuration, _vault));
                    var snapshot = await Service.SnapshotAsync(_stop.Token).ConfigureAwait(false);
                    Post(() => Render(snapshot, snapshot.Accounts.Count == 0 ? "Connect the Google accounts containing your photos." : "Google account settings loaded."));
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { /* Joined owner-requested startup cancellation. */ }
                catch (Exception error) { Failure = error; } // App observes this before treating startup as successful; disposal still joins/releases partial setup.
            }, CancellationToken.None);
        }
    }

    private void StartAction(Func<CancellationToken, Task<string>> action)
    {
        lock (_gate)
        {
            if (_stopping || _busy || _failed || _service is null) return;
            _busy = true;
            _actionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var cancellation = _actionCancellation;
            _action = Task.Run(() => RunActionAsync(action, cancellation), CancellationToken.None);
        }
        _message.Text = "Updating Google account settings…"; SetBusy(true);
    }

    private async Task RunActionAsync(Func<CancellationToken, Task<string>> action, CancellationTokenSource cancellation)
    {
        var message = "Google account settings updated.";
        Exception? fatal = null;
        ProcessorAccountSnapshot? snapshot = null;
        try { message = await action(cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { message = "Connection canceled. Any unfinished setup can be resumed below."; }
        catch (CredentialVaultException error) { message = error.Message; }
        catch (TokenResponseException) { message = "Google authorization could not be renewed. Reconnect this account; your photos and saved work are preserved."; }
        catch (HttpRequestException error) { message = error.StatusCode.HasValue ? "Google Drive could not complete the request (HTTP " + (int)error.StatusCode + "). Try again." : "Google Drive is unavailable. Check your connection and try again."; }
        catch (IOException) { message = "Local account settings could not be saved. Check available disk space and try again."; }
        catch (StaleDriveConnectionException error) { message = error.Message; }
        catch (DriveAccountAlreadyConnectedException error) { message = error.Message; }
        catch (Exception error) { fatal = error; }
        try
        {
            if (!_stop.IsCancellationRequested) snapshot = await Service.SnapshotAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { fatal = fatal is null ? error : new AggregateException(fatal, error); }
        finally
        {
            lock (_gate) { _actionCancellation = null; }
            cancellation.Dispose();
        }
        if (fatal is not null) Failure = fatal;
        Post(() =>
        {
            _busy = false;
            if (snapshot is not null) Render(snapshot, message); else { _message.Text = message; SetBusy(false); }
            if (fatal is not null) { _failed = true; SetBusy(true); _cancel.IsVisible = false; _fatal(fatal); }
        });
    }

    private void Render(ProcessorAccountSnapshot snapshot, string message)
    {
        _message.Text = message; _entries.Children.Clear();
        foreach (var account in snapshot.Accounts)
        {
            var row = new StackPanel { Spacing = 6 };
            row.Children.Add(new TextBlock { Text = account.DisplayName, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(new TextBlock { Text = account.EmailAddress ?? "Google Drive account", TextWrapping = TextWrapping.Wrap });
            row.Children.Add(new TextBlock { Text = account.Connected ? "Connected" : "Disconnected", Opacity = 0.75 });
            var actions = new WrapPanel { Orientation = Orientation.Horizontal };
            if (account.Connected)
            {
                actions.Children.Add(ActionButton("Check connection", async token =>
                    await Service.CheckAsync(account.PermissionId, token).ConfigureAwait(false) is null ? "Reconnect this account to restore Google access." : "Google Drive connection verified."));
                actions.Children.Add(ActionButton("Disconnect", async token =>
                {
                    await Service.DisconnectAsync(account.PermissionId, account.Revision, token).ConfigureAwait(false);
                    await Service.CleanCredentialsAsync(token).ConfigureAwait(false);
                    return "Disconnected from this Processor. Photos and saved work are preserved.";
                }));
            }
            if (!snapshot.Pending.Any(item => item.ExpectedPermissionId == account.PermissionId))
                actions.Children.Add(ActionButton("Reconnect", async token =>
                {
                    await Service.ConnectAsync(account.PermissionId, new SystemAuthorizationBrowser(), token).ConfigureAwait(false);
                    await Service.CleanCredentialsAsync(token).ConfigureAwait(false);
                    return "Google account reconnected.";
                }));
            row.Children.Add(actions); _entries.Children.Add(row);
        }
        foreach (var pending in snapshot.Pending)
        {
            var row = new StackPanel { Spacing = 6 };
            var previous = snapshot.Accounts.SingleOrDefault(account => account.PermissionId == pending.ExpectedPermissionId);
            row.Children.Add(new TextBlock { Text = previous is null ? "Unfinished Google connection" : "Reconnect " + previous.DisplayName, TextWrapping = TextWrapping.Wrap });
            var actions = new WrapPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(ActionButton("Resume setup", async token =>
            {
                await Service.ResumePendingAsync(pending.Id, new SystemAuthorizationBrowser(), token).ConfigureAwait(false);
                await Service.CleanCredentialsAsync(token).ConfigureAwait(false);
                return "Google account connected.";
            }));
            actions.Children.Add(ActionButton("Remove setup", async token =>
            {
                await Service.AbandonPendingAsync(pending.Id, token).ConfigureAwait(false);
                await Service.CleanCredentialsAsync(token).ConfigureAwait(false);
                return "Unfinished connection removed. Existing accounts and photos are preserved.";
            }));
            row.Children.Add(actions); _entries.Children.Add(row);
        }
        if (snapshot.Cleanup.Count > 0) _entries.Children.Add(ActionButton("Retry credential cleanup", async token =>
        {
            await Service.CleanCredentialsAsync(token).ConfigureAwait(false);
            return "Old connection credentials removed.";
        }));
        SetBusy(_busy);
        StateChanged?.Invoke(snapshot);
    }

    private Button ActionButton(string text, Func<CancellationToken, Task<string>> action)
    {
        var button = Button(text); button.Margin = new Avalonia.Thickness(0, 0, 8, 8);
        button.Click += (_, _) => StartAction(action);
        return button;
    }
    private static Button Button(string text) => new() { Content = text, MinHeight = 48, MinWidth = 48 };
    private void SetBusy(bool busy) { _connect.IsEnabled = !busy && _service is not null; _entries.IsEnabled = !busy; _cancel.IsVisible = busy; }
    private void Post(Action action) => Dispatcher.UIThread.Post(() => { lock (_gate) { if (!_stopping) action(); } });

    public ValueTask DisposeAsync()
    {
        lock (_gate) { _stopping = true; return new(_disposal ??= StopAsync()); }
    }
    private async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_startup is not null) await _startup.ConfigureAwait(false);
        if (_action is not null) await _action.ConfigureAwait(false);
        if (_service is not null) await _service.DisposeAsync().ConfigureAwait(false);
        if (_vault is not null) await _vault.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
