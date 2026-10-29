using Avalonia;
using Avalonia.Logging;
using System.Text.Json;
using Mpa.Processor.Core;

namespace Mpa.Processor.Desktop;

internal static class Program
{
    public static string StateDirectory { get; private set; } = "";
    public static bool Background { get; private set; }
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--generate-branding")
        {
            BrandingAssetWriter.Write(args[1], args[2]);
            return 0;
        }
        LocalProcessorCommand command = LocalProcessorCommand.Show;
        var hasCommand = false;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--state-directory" && index + 1 < args.Length && StateDirectory.Length == 0)
                StateDirectory = Path.GetFullPath(args[++index]);
            else if (args[index] == "--background" && !Background) Background = true;
            else if (!hasCommand)
            {
                command = args[index] switch { "--show" => LocalProcessorCommand.Show, "--status" => LocalProcessorCommand.Status,
                    "--pause" => LocalProcessorCommand.Pause, "--resume" => LocalProcessorCommand.Resume, "--stop" => LocalProcessorCommand.Stop,
                    _ => throw new ArgumentException("Unknown Processor command. Use --show, --status, --pause, --resume, --stop and optional --state-directory/--background.") };
                hasCommand = true;
            }
            else throw new ArgumentException("Repeated/invalid Processor command.");
        }
        if (StateDirectory.Length == 0)
        {
            var local = OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) throw new InvalidOperationException("No current-user application data directory is available.");
            StateDirectory = Path.Combine(local, "MyPhotosAnywhere", "Processor");
        }
        var reply = LocalProcessorControl.TrySendAsync(StateDirectory, command, CancellationToken.None).GetAwaiter().GetResult();
        if (reply is not null) { Console.WriteLine(JsonSerializer.Serialize(reply)); return reply.Ok ? 0 : 1; }
        if (command != LocalProcessorCommand.Show)
        {
            Console.WriteLine(JsonSerializer.Serialize(new LocalProcessorReply(false, "unavailable", "No Processor control endpoint is available for this state directory.")));
            return 3;
        }
        Logger.Sink = new DiagnosticSink();
        ProcessorStateDatabase? database = null;
        ProcessorStateCoordinator? state = null;
        Exception? startupError = null;
        try
        {
            // Acquire the durable single-instance owner before creating a window. A concurrent
            // launcher can then wait for this owner's control endpoint rather than opening a second UI/database.
            try { database = Task.Run(() => new ProcessorStateDatabase(StateDirectory)).GetAwaiter().GetResult(); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    reply = LocalProcessorControl.TrySendAsync(StateDirectory, command, CancellationToken.None).GetAwaiter().GetResult();
                    if (reply is not null) { Console.WriteLine(JsonSerializer.Serialize(reply)); return reply.Ok ? 0 : 1; }
                }
                startupError = new IOException("Another Processor owns this local state but its controls are unavailable. Open or exit it from its tray menu, then try again.", error);
            }
            catch (Exception error) { startupError = error; }
            if (database is not null) state = new ProcessorStateCoordinator(database);
            try { return AppBuilder.Configure(() => new ProcessorApp(startupError, state, database?.InstallationId ?? Guid.Empty)).UsePlatformDetect().StartWithClassicDesktopLifetime(args); }
            finally { if (Application.Current is ProcessorApp app) app.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        finally
        {
            try { state?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            finally { database?.Dispose(); }
        }
    }

    private sealed class DiagnosticSink : ILogSink
    {
        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Console.Error.WriteLine($"{level} [{area}] {messageTemplate}");
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
            => Console.Error.WriteLine($"{level} [{area}] {messageTemplate} | {string.Join(" | ", propertyValues.Select(value => value?.ToString() ?? "null"))}");
    }
}
