using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mpa.Processor.Protocol;

/// <summary>Exact opaque source bytes. Content reuse never associates photos or
/// grants provider access; the native ready-receipt owner supplies provenance.</summary>
public sealed class MetadataSourceBytes
{
    private readonly byte[] _bytes;
    public ImportedMetadataFingerprint Fingerprint{get;}
    private MetadataSourceBytes(byte[] bytes)
    {_bytes=bytes;Fingerprint=new(bytes.Length,Convert.ToHexStringLower(SHA256.HashData(bytes)));}
    public static MetadataSourceBytes Capture(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length is <1 or >TakeoutMetadataDocument.MaximumSourceBytes)throw new ProtocolFormatException("Raw metadata source exceeds its fixed bound.");
        return new(bytes.ToArray());
    }
    public byte[] CopyBytes()=>_bytes.ToArray();
    internal ReadOnlySpan<byte> Bytes=>_bytes;
}

/// <summary>Bounded, content-indexed preservation of exact raw sidecars, including
/// unknown fields/encoding. This format is inert data, not a facts chunk, signed
/// catalog, source association, upload receipt or cleanup permission.</summary>
public sealed class MetadataSourceBundle
{
    public const string Role="metadata-source-bundle";
    public const string MimeType="application/octet-stream";
    public const int MaximumBytes=4*1024*1024;
    public const int MaximumSources=128;
    private const int MaximumHeaderBytes=32*1024;
    private static ReadOnlySpan<byte> Magic=>"MPA-RAW-METADATA-1\n"u8;
    public static bool HasSignature(ReadOnlySpan<byte> bytes)=>bytes.StartsWith(Magic);
    private readonly byte[] _payload;
    private readonly ReadOnlyCollection<Slot> _slots;
    private sealed record Slot(ImportedMetadataFingerprint Source,int Offset);
    public ReadOnlyCollection<ImportedMetadataFingerprint> Sources{get;}
    private MetadataSourceBundle(byte[] payload,IEnumerable<Slot> slots)
    {_payload=payload;_slots=Array.AsReadOnly(slots.ToArray());Sources=Array.AsReadOnly(_slots.Select(slot=>slot.Source).ToArray());}
    public byte[] CopyPayload()=>_payload.ToArray();
    public byte[] ReadSource(string sha256)
    {
        var slot=_slots.SingleOrDefault(value=>value.Source.Sha256==sha256)??throw new ProtocolFormatException("The source is absent from this metadata bundle.");
        return _payload.AsSpan(slot.Offset,checked((int)slot.Source.Bytes)).ToArray();
    }
    public static MetadataSourceBundle Capture(IEnumerable<MetadataSourceBytes> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);var owned=sources.Take(MaximumSources+1).ToArray();
        if(owned.Length is <1 or >MaximumSources||owned.Any(source=>source is null))throw new ProtocolFormatException("A raw metadata bundle requires a bounded nonempty source set.");
        var ordered=owned.OrderBy(source=>source.Fingerprint.Sha256,StringComparer.Ordinal).ToArray();
        if(ordered.Select(source=>source.Fingerprint.Sha256).Distinct(StringComparer.Ordinal).Count()!=ordered.Length)throw new ProtocolFormatException("Raw metadata bundle source hashes must be unique.");
        var header=Header(ordered.Select(source=>source.Fingerprint));var start=checked(Magic.Length+4+header.Length);
        var size=ordered.Aggregate((long)start,(total,source)=>checked(total+source.Fingerprint.Bytes));
        if(size>MaximumBytes)throw new ProtocolFormatException("Raw metadata bundle exceeds its immutable object bound.");
        var payload=new byte[checked((int)size)];Magic.CopyTo(payload);BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(Magic.Length,4),header.Length);header.CopyTo(payload,start-header.Length);
        var slots=new List<Slot>(ordered.Length);var offset=start;
        foreach(var source in ordered)
        {
            if(source.Fingerprint.Bytes!=source.Bytes.Length||source.Fingerprint.Sha256!=Convert.ToHexStringLower(SHA256.HashData(source.Bytes)))throw new ProtocolFormatException("Raw metadata source changed its captured bytes.");
            slots.Add(new(source.Fingerprint,offset));source.Bytes.CopyTo(payload.AsSpan(offset));offset=checked(offset+source.Bytes.Length);
        }
        return new(payload,slots);
    }
    public static MetadataSourceBundle Read(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length>MaximumBytes||bytes.Length<Magic.Length+5||!bytes[..Magic.Length].SequenceEqual(Magic))throw new ProtocolFormatException("Unknown or oversized raw metadata bundle.");
        var owned=bytes.ToArray();var headerLength=BinaryPrimitives.ReadInt32BigEndian(owned.AsSpan(Magic.Length,4));
        if(headerLength is <1 or >MaximumHeaderBytes||headerLength>owned.Length-Magic.Length-4)throw new ProtocolFormatException("Raw metadata bundle has an invalid bounded header.");
        var header=owned.AsSpan(Magic.Length+4,headerLength);var offset=Magic.Length+4+headerLength;
        try
        {
            using var json=JsonDocument.Parse(header.ToArray());var root=json.RootElement;
            if(root.GetProperty("format").GetString()!="mpa-raw-metadata-bundle"||root.GetProperty("major").GetInt32()!=1)throw new ProtocolCompatibilityException("Unsupported raw metadata preservation format.");
            var rows=root.GetProperty("sources");if(rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength() is <1 or >MaximumSources)throw new ProtocolFormatException("Raw metadata source index exceeds its fixed bound.");
            var slots=new List<Slot>(rows.GetArrayLength());string? previous=null;
            foreach(var row in rows.EnumerateArray())
            {
                var hash=row.GetProperty("sha256").GetString()!;var size=row.GetProperty("bytes").GetString();
                if(hash is not{Length:64}||hash.Any(c=>c is not(>='0' and <='9') and not(>='a' and <='f'))||previous is not null&&StringComparer.Ordinal.Compare(previous,hash)>=0||
                    !int.TryParse(size,NumberStyles.None,CultureInfo.InvariantCulture,out var length)||length is <1 or >TakeoutMetadataDocument.MaximumSourceBytes||length.ToString(CultureInfo.InvariantCulture)!=size||length>owned.Length-offset)
                    throw new ProtocolFormatException("Raw metadata source index changed its ordering, size or digest.");
                if(Convert.ToHexStringLower(SHA256.HashData(owned.AsSpan(offset,length)))!=hash)throw new ProtocolFormatException("Raw metadata source bytes changed inside their bundle.");
                slots.Add(new(new(length,hash),offset));offset=checked(offset+length);previous=hash;
            }
            if(offset!=owned.Length||!Header(slots.Select(slot=>slot.Source)).AsSpan().SequenceEqual(header))throw new ProtocolFormatException("Raw metadata bundle changed its closed canonical index or exact length.");
            return new(owned,slots);
        }
        catch(Exception error)when(error is not(ProtocolCompatibilityException or ProtocolFormatException)&&error is (JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException))
        {throw new ProtocolFormatException("Raw metadata bundle has an invalid source index.",error);}
    }
    private static byte[] Header(IEnumerable<ImportedMetadataFingerprint> sources)=>new CanonicalJson(MaximumHeaderBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(new
    {format="mpa-raw-metadata-bundle",major=1,sources=sources.Select(source=>new{bytes=source.Bytes.ToString(CultureInfo.InvariantCulture),sha256=source.Sha256}).ToArray()}));
}
