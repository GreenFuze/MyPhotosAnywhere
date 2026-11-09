using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mpa.Processor.Protocol;

/// <summary>RFC 8785 string/key ordering with the protocol's integer-only numeric subset.
/// Large counters, byte lengths and epoch milliseconds travel as canonical decimal strings.</summary>
public sealed class CanonicalJson
{
    public const int MaximumDocumentBytes = 16 * 1024 * 1024;
    public const int MaximumStringBytes = 64 * 1024;
    public const int MaximumNameBytes = 128;
    public const int MaximumCollectionItems = 1000;
    public const int MaximumProperties = 256;
    public const int MaximumDepth = 32;
    public const long MaximumInteger = 9007199254740991;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly int _documentLimit;

    public CanonicalJson(int documentLimit = MaximumDocumentBytes)
    {
        if (documentLimit is < 1 or > MaximumDocumentBytes) throw new ArgumentOutOfRangeException(nameof(documentLimit));
        _documentLimit = documentLimit;
    }

    public byte[] Encode(ReadOnlySpan<byte> json)
    {
        if (json.Length == 0 || json.Length > _documentLimit) throw new ProtocolFormatException("Protocol document exceeds its byte bound or is empty.");
        try
        {
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = MaximumDepth
            });
            var output = new StringBuilder();
            Write(document.RootElement, output);
            var bytes = StrictUtf8.GetBytes(output.ToString());
            if (bytes.Length > _documentLimit) throw new ProtocolFormatException("Canonical document exceeds its byte bound.");
            return bytes;
        }
        catch (JsonException error) { throw new ProtocolFormatException("Invalid JSON document.", error); }
        catch (EncoderFallbackException error) { throw new ProtocolFormatException("Invalid Unicode scalar sequence.", error); }
    }

    private static void Write(JsonElement element, StringBuilder output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = new List<JsonProperty>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (properties.Count >= MaximumProperties) throw new ProtocolFormatException("Too many object properties.");
                    CheckString(property.Name, MaximumNameBytes);
                    if (!names.Add(property.Name)) throw new ProtocolFormatException("Duplicate JSON property.");
                    properties.Add(property);
                }
                properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
                output.Append('{');
                for (var index = 0; index < properties.Count; index++)
                {
                    if (index != 0) output.Append(',');
                    WriteString(properties[index].Name, output);
                    output.Append(':');
                    Write(properties[index].Value, output);
                }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                if (element.GetArrayLength() > MaximumCollectionItems) throw new ProtocolFormatException("Too many array entries.");
                output.Append('[');
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first) output.Append(',');
                    first = false;
                    Write(item, output);
                }
                output.Append(']');
                break;
            case JsonValueKind.String:
                string value;
                try { value = element.GetString() ?? throw new ProtocolFormatException("Missing string value."); }
                catch (InvalidOperationException error) { throw new ProtocolFormatException("Invalid escaped Unicode string.", error); }
                CheckString(value, MaximumStringBytes);
                WriteString(value, output);
                break;
            case JsonValueKind.Number:
                var number = element.GetRawText();
                if (number.Contains('.', StringComparison.Ordinal) || number.Contains('e', StringComparison.OrdinalIgnoreCase) ||
                    !element.TryGetInt64(out var integer) || integer is < -MaximumInteger or > MaximumInteger)
                    throw new ProtocolFormatException("Protocol numbers must be safe integers; encode wider values as decimal strings.");
                output.Append(integer.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True: output.Append("true"); break;
            case JsonValueKind.False: output.Append("false"); break;
            case JsonValueKind.Null: output.Append("null"); break;
            default: throw new ProtocolFormatException("Unsupported JSON token.");
        }
    }

    private static void CheckString(string value, int limit)
    {
        if (StrictUtf8.GetByteCount(value) > limit) throw new ProtocolFormatException("Protocol string exceeds its byte bound.");
    }

    private static void WriteString(string value, StringBuilder output)
    {
        const string hex = "0123456789abcdef";
        output.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (character < 32) output.Append("\\u00").Append(hex[character >> 4]).Append(hex[character & 15]);
                    else output.Append(character);
                    break;
            }
        }
        output.Append('"');
    }
}
