namespace Mpa.Processor.Protocol;

public sealed class ProtocolFormatException : FormatException
{
    public ProtocolFormatException() : base("Invalid MPA protocol document.") { }
    public ProtocolFormatException(string message) : base(message) { }
    public ProtocolFormatException(string message, Exception innerException) : base(message, innerException) { }
}
