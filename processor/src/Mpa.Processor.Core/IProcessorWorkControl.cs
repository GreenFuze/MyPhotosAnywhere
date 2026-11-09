namespace Mpa.Processor.Core;

public interface IProcessorWorkControl
{
    ProcessorWorkState State { get; }
    Exception? Failure { get; }
    Task PauseAsync();
    Task ResumeAsync();
}

public sealed class ProcessorWorkStateConflictException() : InvalidOperationException("Processing is starting, stopping or faulted. Refresh its current state before changing it.");
