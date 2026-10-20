using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Mpa.Processor.Core;

namespace Mpa.Processor.Desktop;

public sealed class ProcessorApp : Application, IAsyncDisposable
{
    private readonly ProcessorWorkSupervisor _work = new();
    private readonly TextBlock _state = new() { Text = "Starting…", FontSize = 28, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _detail = new() { Text = "Opening the local Processor state.", TextWrapping = TextWrapping.Wrap };
    private readonly Button _pause = new() { Content = "Pause processing", MinHeight = 48, IsEnabled = false };
    private readonly Button _exit = new() { Content = "Exit Processor", MinHeight = 48 };
    private readonly NativeMenuItem _trayPause = new("Pause processing") { IsEnabled = false };
    private readonly Exception? _startupError;
    private bool _initialized;
    private Task? _shutdown;
    private Window? _window;
    private TrayIcon? _tray;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private bool _allowClose;
    private bool _actionPending;
    private bool _hasFailure;
    private Task? _ownedStop;
    private LocalProcessorControl? _control;
    private Task? _controlMonitor;
    private readonly ProcessorStateCoordinator? _stateCoordinator;
    private readonly Guid _installationId;
    private readonly ProcessorAccountPanel _accounts;
    private bool _hasGoogleAccount;

    public ProcessorApp(Exception? startupError = null, ProcessorStateCoordinator? stateCoordinator = null, Guid installationId = default)
    {
        _startupError = startupError; _stateCoordinator = stateCoordinator; _installationId = installationId;
        _accounts = new ProcessorAccountPanel(error => ShowFailure("Google account setup stopped", error));
        _accounts.StateChanged += snapshot => { _hasGoogleAccount = snapshot.Accounts.Any(account => account.Connected); RefreshReadyStatus(); };
    }

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            throw new InvalidOperationException("Processor requires a desktop application lifetime.");
        _desktop = desktop;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        using var iconStream = AssetLoader.Open(new Uri("avares://MPA.Processor/Assets/mpa.ico"));
        var icon = new WindowIcon(iconStream);
        _window = BuildWindow(icon);
        desktop.MainWindow = _window;
        var show = new NativeMenuItem("Open My Photos Anywhere Processor");
        var exit = new NativeMenuItem("Exit Processor");
        show.Click += (_, _) => ShowWindow();
        exit.Click += async (_, _) => await RequestExitAsync();
        _trayPause.Click += async (_, _) => await TogglePauseAsync();
        var menu = new NativeMenu();
        menu.Add(show); menu.Add(_trayPause); menu.Add(new NativeMenuItemSeparator()); menu.Add(exit);
        _tray = new TrayIcon { Icon = icon, Menu = menu, ToolTipText = "My Photos Anywhere Processor", IsVisible = true };
        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
        _window.Closing += (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            if (OperatingSystem.IsLinux()) _window.WindowState = WindowState.Minimized; // Keep a taskbar return path on desktops without a tray host.
            else _window.Hide();
        };
        desktop.ShutdownRequested += async (_, args) => { if (!_allowClose) { args.Cancel = true; await RequestExitAsync(); } };
        _pause.Click += async (_, _) => await TogglePauseAsync();
        _exit.Click += async (_, _) => await RequestExitAsync();
        _window.Opened += async (_, _) => await InitializeStateAsync();
        base.OnFrameworkInitializationCompleted();
    }

    private Window BuildWindow(WindowIcon icon)
    {
        var content = new StackPanel { Spacing = 20, Margin = new Thickness(32) };
        content.Children.Add(new TextBlock { Text = "MY PHOTOS ANYWHERE", FontSize = 14, LetterSpacing = 2, FontWeight = FontWeight.SemiBold });
        content.Children.Add(new TextBlock { Text = "Processor", FontSize = 36, FontWeight = FontWeight.Bold });
        var status = new StackPanel { Spacing = 12 };
        status.Children.Add(_state); status.Children.Add(_detail); status.Children.Add(_pause);
        content.Children.Add(Card(status));
        content.Children.Add(Card(_accounts));
        content.Children.Add(Card(new StackPanel { Spacing = 10, Children =
        {
            new TextBlock { Text = "Phone", FontSize = 20, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = "No phone paired", TextWrapping = TextWrapping.Wrap }
        } }));
        content.Children.Add(new TextBlock { Text = "Your originals stay in Google Drive. The Processor publishes previews and metadata separately.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        content.Children.Add(_exit);
        return new Window { Title = "My Photos Anywhere Processor", Width = 620, Height = 720, MinWidth = 440, MinHeight = 520,
            Icon = icon, Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            WindowStartupLocation = WindowStartupLocation.CenterScreen };
    }

    private static Border Card(Control content) => new() { Child = content, Padding = new Thickness(24), CornerRadius = new CornerRadius(24),
        Background = new SolidColorBrush(Color.FromArgb(20, 120, 140, 140)) };

    private async Task InitializeStateAsync()
    {
        if (_initialized) return; // Opened fires again after close-to-tray and Show.
        _initialized = true;
        if (_startupError is not null) { ShowFailure("Processor could not start", _startupError); return; }
        try
        {
            if (_shutdown is not null) return;
            if (_stateCoordinator is null || _installationId == Guid.Empty) throw new InvalidOperationException("Processor state owner/installation identity is unavailable.");
            _control = new LocalProcessorControl(Program.StateDirectory, HandleControlAsync,
                () => Dispatcher.UIThread.Post(async () => await RequestExitAsync()));
            _controlMonitor = ObserveControlAsync(_control);
            RefreshReadyStatus();
            _pause.IsEnabled = true; _trayPause.IsEnabled = true;
            await _accounts.InitializeAsync(_stateCoordinator, _installationId, Program.StateDirectory);
            if (_accounts.Failure is not null) throw _accounts.Failure;
        }
        catch (Exception error) { ShowFailure("Processor could not start", error); }
    }

    private async Task TogglePauseAsync()
    {
        if (_actionPending || _shutdown is not null) return;
        _actionPending = true; _pause.IsEnabled = false; _trayPause.IsEnabled = false;
        try
        {
            if (_work.State == ProcessorWorkState.Paused) _work.Resume();
            else { _state.Text = "Pausing…"; await _work.PauseAsync(); }
            if (_work.State == ProcessorWorkState.Faulted) throw _work.Failure ?? new InvalidOperationException("Processor entered a faulted state.");
            var paused = _work.State == ProcessorWorkState.Paused;
            RefreshReadyStatus();
            _pause.Content = paused ? "Resume processing" : "Pause processing";
            _trayPause.Header = paused ? "Resume processing" : "Pause processing";
        }
        catch (Exception error) { ShowFailure("Processing stopped", error); }
        finally
        {
            _actionPending = false;
            var usable = _shutdown is null && _work.State is ProcessorWorkState.Ready or ProcessorWorkState.Paused;
            _pause.IsEnabled = usable; _trayPause.IsEnabled = usable;
        }
    }

    private void RefreshReadyStatus()
    {
        if (_hasFailure || _shutdown is not null) return;
        if (_work.State == ProcessorWorkState.Paused)
        { _state.Text = "Processing paused"; _detail.Text = "No processing work will start until you resume."; }
        else if (_work.State == ProcessorWorkState.Ready)
        {
            _state.Text = _hasGoogleAccount ? "Ready to pair your phone" : "Connect Google Drive";
            _detail.Text = _hasGoogleAccount ? "Google Drive is connected. Pair your phone to finish setup." : "Connect the Google accounts containing your photos, then pair your phone.";
        }
    }

    private async Task RequestExitAsync()
    {
        if (_shutdown is not null) return;
        _state.Text = "Stopping…"; _detail.Text = "Waiting for processing and local state to close safely.";
        _pause.IsEnabled = false; _trayPause.IsEnabled = false; _exit.IsEnabled = false;
        _shutdown = DisposeAsync().AsTask();
        try
        {
            await _shutdown;
            _allowClose = true;
            _desktop!.Shutdown(!_hasFailure && _work.Failure is null && _accounts.Failure is null ? 0 : 1);
        }
        catch (Exception error)
        {
            _shutdown = null; _ownedStop = null; _exit.IsEnabled = true;
            ShowFailure("Could not confirm safe shutdown", error);
        }
    }

    private async Task StopOwnedAsync()
    {
        var owners = Task.WhenAll(_work.DisposeAsync().AsTask(), _accounts.DisposeAsync().AsTask());
        try { await owners.ConfigureAwait(false); }
        catch { throw owners.Exception ?? throw new InvalidOperationException("Processor owners failed to stop without an exception."); }
        if (_control is not null)
        {
            try { await _control.DisposeAsync().ConfigureAwait(false); }
            catch when (_control.Completion.IsFaulted && _controlMonitor is not null)
            { _hasFailure = true; } // Listener has joined; its observer reports the exact failure and Exit remains unsuccessful.
            if (_controlMonitor is not null) await _controlMonitor.ConfigureAwait(false);
        }
        // Program owns the database until the desktop loop returns, after this work/IPC join.
    }
    private Task<LocalProcessorReply> HandleControlAsync(LocalProcessorCommand command, CancellationToken token)
    {
        var result = new TaskCompletionSource<LocalProcessorReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (token.IsCancellationRequested) { result.TrySetCanceled(token); return; }
                if (_hasFailure && command is LocalProcessorCommand.Pause or LocalProcessorCommand.Resume)
                { result.TrySetResult(new(false, "faulted", "Processor needs attention; open its status window.")); return; }
                switch (command)
                {
                    case LocalProcessorCommand.Show: ShowWindow(); break;
                    case LocalProcessorCommand.Status: break;
                    case LocalProcessorCommand.Pause:
                        if (_work.State is ProcessorWorkState.Ready or ProcessorWorkState.Running) await TogglePauseAsync();
                        break;
                    case LocalProcessorCommand.Resume:
                        if (_work.State == ProcessorWorkState.Paused) await TogglePauseAsync();
                        break;
                    case LocalProcessorCommand.Stop:
                        _pause.IsEnabled = false; _trayPause.IsEnabled = false;
                        await _work.StopAsync();
                        result.TrySetResult(new(true, "stopping", null));
                        return;
                    default: throw new ArgumentOutOfRangeException(nameof(command));
                }
                var state = _work.State;
                var accepted = command switch { LocalProcessorCommand.Show => true, LocalProcessorCommand.Pause => state == ProcessorWorkState.Paused,
                    LocalProcessorCommand.Resume => state == ProcessorWorkState.Ready, _ => !_hasFailure && state != ProcessorWorkState.Faulted };
                result.TrySetResult(new(accepted, _hasFailure ? "faulted" : state.ToString().ToLowerInvariant(), accepted ? null : "Processor state did not permit this command; open its status window."));
            }
            catch (Exception error) { ShowFailure("Processor control failed", error); result.TrySetException(error); }
        });
        return result.Task.WaitAsync(token);
    }

    private async Task ObserveControlAsync(LocalProcessorControl control)
    {
        try { await control.Completion.ConfigureAwait(false); }
        catch (Exception error)
        {
            try { await _work.StopAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { error = new AggregateException("Local control failed and work could not stop.", error, cleanup); }
            Dispatcher.UIThread.Post(() => ShowFailure("Local Processor controls stopped", error));
        }
    }

    private void ShowFailure(string title, Exception error)
    {
        _hasFailure = true;
        Console.Error.WriteLine(error);
        _state.Text = title; _detail.Text = error.Message;
        _pause.IsEnabled = false; _trayPause.IsEnabled = false;
        ShowWindow();
    }
    public ValueTask DisposeAsync()
    {
        // Called on the owning UI thread, including Program's final fallback after its loop exits.
        _tray?.Dispose(); _tray = null;
        _ownedStop ??= StopOwnedAsync();
        return new(_ownedStop);
    }
    private void ShowWindow()
    {
        _window?.Show();
        if (_window is not null) { _window.WindowState = WindowState.Normal; _window.Activate(); }
    }
}
