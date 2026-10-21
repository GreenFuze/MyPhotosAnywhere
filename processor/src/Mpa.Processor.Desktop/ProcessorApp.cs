using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Mpa.Processor.Core;
using Mpa.Processor.Web;
using System.Diagnostics;

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
    private DesktopWebRuntime? _web;
    private Task<DesktopWebRuntime>? _webStartup;
    private readonly CancellationTokenSource _stop = new();
    private readonly Button _open = new() { Content = "Open My Photos Anywhere", MinHeight = 48, IsEnabled = false };
    private IProcessorWorkControl CurrentWork => _web?.Work ?? _work;

    public ProcessorApp(Exception? startupError = null, ProcessorStateCoordinator? stateCoordinator = null, Guid installationId = default)
    {
        _startupError = startupError; _stateCoordinator = stateCoordinator; _installationId = installationId;
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
        var show = new NativeMenuItem("Open My Photos Anywhere");
        var exit = new NativeMenuItem("Exit Processor");
        show.Click += (_, _) => { if (_web is null) ShowWindow(); else OpenWeb(); };
        exit.Click += async (_, _) => await RequestExitAsync();
        _trayPause.Click += async (_, _) => await TogglePauseAsync();
        var menu = new NativeMenu();
        menu.Add(show); menu.Add(_trayPause); menu.Add(new NativeMenuItemSeparator()); menu.Add(exit);
        _tray = new TrayIcon { Icon = icon, Menu = menu, ToolTipText = "My Photos Anywhere", IsVisible = true };
        _tray.Clicked += (_, _) => { if (_web is null) ShowWindow(); else OpenWeb(); };
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
        _open.Click += (_, _) => OpenWeb();
        _window.Opened += async (_, _) => await InitializeStateAsync();
        base.OnFrameworkInitializationCompleted();
    }

    private Window BuildWindow(WindowIcon icon)
    {
        var content = new StackPanel { Spacing = 20, Margin = new Thickness(32) };
        content.Children.Add(new TextBlock { Text = "MY PHOTOS ANYWHERE", FontSize = 14, LetterSpacing = 2, FontWeight = FontWeight.SemiBold });
        content.Children.Add(new TextBlock { Text = "Desktop", FontSize = 36, FontWeight = FontWeight.Bold });
        var status = new StackPanel { Spacing = 12 };
        status.Children.Add(_state); status.Children.Add(_detail); status.Children.Add(_open); status.Children.Add(_pause);
        content.Children.Add(Card(status));
        content.Children.Add(new TextBlock { Text = "Your originals stay in Google Drive. The Processor publishes previews and metadata separately.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        content.Children.Add(_exit);
        return new Window { Title = "My Photos Anywhere Desktop", Width = 560, Height = 560, MinWidth = 440, MinHeight = 480,
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
            _webStartup = Task.Run(() => DesktopWebRuntime.StartAsync(_stateCoordinator, _installationId, Program.StateDirectory, _work,
                Path.Combine(AppContext.BaseDirectory, "MPA.Processor.oauth.json"), Path.Combine(AppContext.BaseDirectory, "wwwroot"), enableLan: false, _stop.Token));
            _web = await _webStartup;
            _web.Failed += error => Dispatcher.UIThread.Post(() => ShowFailure("Desktop engine needs attention", error));
            if (_shutdown is not null) return;
            if (_web.Failure is { } failure) throw new InvalidOperationException("The desktop engine stopped during startup.", failure);
            _pause.IsEnabled = true; _trayPause.IsEnabled = true;
            _open.IsEnabled = true;
            RefreshReadyStatus();
            if (Program.Background)
            {
                if (OperatingSystem.IsLinux()) _window!.WindowState = WindowState.Minimized;
                else _window!.Hide();
            }
            else OpenWeb();
        }
        catch (DesktopRuntimeStartException error) { _web = error.RetainedOwner; ShowFailure("Processor could not start", error); }
        catch (Exception error) { ShowFailure("Processor could not start", error); }
    }

    private async Task TogglePauseAsync()
    {
        if (_actionPending || _shutdown is not null || _web is null) return;
        _actionPending = true; _pause.IsEnabled = false; _trayPause.IsEnabled = false;
        try
        {
            if (CurrentWork.State == ProcessorWorkState.Paused) await CurrentWork.ResumeAsync();
            else { _state.Text = "Pausing…"; await CurrentWork.PauseAsync(); }
            if (CurrentWork.State == ProcessorWorkState.Faulted) throw CurrentWork.Failure ?? new InvalidOperationException("Processor entered a faulted state.");
            var paused = CurrentWork.State == ProcessorWorkState.Paused;
            RefreshReadyStatus();
            _pause.Content = paused ? "Resume processing" : "Pause processing";
            _trayPause.Header = paused ? "Resume processing" : "Pause processing";
        }
        catch (ProcessorWorkStateConflictException) { RefreshReadyStatus(); }
        catch (ObjectDisposedException) when (CurrentWork.State is ProcessorWorkState.Stopping or ProcessorWorkState.Stopped) { RefreshReadyStatus(); }
        catch (Exception error) { _web.ReportFailure(error); }
        finally
        {
            _actionPending = false;
            var usable = !_hasFailure && _shutdown is null && CurrentWork.State is ProcessorWorkState.Ready or ProcessorWorkState.Running or ProcessorWorkState.Paused;
            _pause.IsEnabled = usable; _trayPause.IsEnabled = usable;
        }
    }

    private void RefreshReadyStatus()
    {
        if (_hasFailure || _shutdown is not null) return;
        if (CurrentWork.State == ProcessorWorkState.Paused)
        { _state.Text = "Processing paused"; _detail.Text = "No processing work will start until you resume."; }
        else if (CurrentWork.State is ProcessorWorkState.Ready or ProcessorWorkState.Running)
        {
            _state.Text = _web is null ? "Starting desktop interface…" : "MPA is running";
            _detail.Text = "Open your photo library in the browser. Closing the browser leaves this engine running in the background.";
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
            _desktop!.Shutdown(!_hasFailure && _work.Failure is null && _web?.Failure is null ? 0 : 1);
        }
        catch (Exception error)
        {
            _shutdown = null; _ownedStop = null; _exit.IsEnabled = true;
            ShowFailure("Could not confirm safe shutdown", error);
        }
    }

    private async Task StopOwnedAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_webStartup is not null)
        {
            try { _web = await _webStartup.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { /* Joined canceled startup released partial owners. */ }
            catch when (_webStartup.IsFaulted && _hasFailure) { /* Startup already released its partial owners and the UI retains the failure. */ }
        }
        var failures = new List<Exception>();
        try { if (_web is not null) await _web.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        // Failure in one owner never bypasses the independently owned native worker.
        try { await _work.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        if (_control is not null)
        {
            try { await _control.DisposeAsync().ConfigureAwait(false); }
            catch when (_control.Completion.IsFaulted && _controlMonitor is not null)
            { _hasFailure = true; } // Listener has joined; its observer reports the exact failure and Exit remains unsuccessful.
            catch (Exception error) { failures.Add(error); }
            if (_controlMonitor is not null) await _controlMonitor.ConfigureAwait(false);
        }
        // Program owns the database until the desktop loop returns, after this work/IPC join.
        if (failures.Count != 0) throw new AggregateException("Desktop shutdown retained unconfirmed owners.", failures);
    }

    private void OpenWeb()
    {
        if (_web is null || _shutdown is not null || _hasFailure) return;
        try
        {
            var invitation = _web.Host.OpenLocalBrowser();
            using var launched = Process.Start(new ProcessStartInfo(invitation.OpenLink.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) { ShowFailure("Could not open MPA in your browser", error); }
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
                if (_web is null && command is LocalProcessorCommand.Pause or LocalProcessorCommand.Resume)
                { result.TrySetResult(new(false, "starting", "The desktop engine is still starting. Try again when it is ready.")); return; }
                switch (command)
                {
                    case LocalProcessorCommand.Show: if (_web is not null) OpenWeb(); else ShowWindow(); break;
                    case LocalProcessorCommand.Status: break;
                    case LocalProcessorCommand.Pause:
                        if (CurrentWork.State is ProcessorWorkState.Ready or ProcessorWorkState.Running) await TogglePauseAsync();
                        break;
                    case LocalProcessorCommand.Resume:
                        if (CurrentWork.State == ProcessorWorkState.Paused) await TogglePauseAsync();
                        break;
                    case LocalProcessorCommand.Stop:
                        _pause.IsEnabled = false; _trayPause.IsEnabled = false;
                        if (_web is not null) await _web.StopProcessingAsync(); else await _work.StopAsync();
                        result.TrySetResult(new(true, "stopping", null));
                        return;
                    default: throw new ArgumentOutOfRangeException(nameof(command));
                }
                var state = CurrentWork.State;
                var accepted = command switch { LocalProcessorCommand.Show => true, LocalProcessorCommand.Pause => state == ProcessorWorkState.Paused,
                    LocalProcessorCommand.Resume => state is ProcessorWorkState.Ready or ProcessorWorkState.Running, _ => !_hasFailure && state != ProcessorWorkState.Faulted };
                result.TrySetResult(new(accepted, _hasFailure ? "faulted" : _web is null ? "starting" : state.ToString().ToLowerInvariant(), accepted ? null : "Processor state did not permit this command; open its status window."));
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
            try
            {
                if (_web is not null) { _web.ReportFailure(error); await _web.StopProcessingAsync().ConfigureAwait(false); }
                else { await _stop.CancelAsync().ConfigureAwait(false); await _work.StopAsync().ConfigureAwait(false); }
            }
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
