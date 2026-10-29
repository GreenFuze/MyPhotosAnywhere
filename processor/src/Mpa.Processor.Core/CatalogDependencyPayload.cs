using System.Security.Cryptography;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Owns exact bounded caller bytes before asynchronous admission. It is
/// immutable input, never proof of current Drive authority or graph validity.</summary>
public sealed class CatalogDependencyPayload
{
    private readonly byte[] _bytes;
    public string Role {get;}
    public string MimeType {get;}
    public string Sha256 {get;}
    private CatalogDependencyPayload(string role,string mime,byte[] owned)
    {Role=role;MimeType=mime;_bytes=owned;Sha256=Convert.ToHexStringLower(SHA256.HashData(owned));}

    public static CatalogDependencyPayload Capture(string role,string mime,ReadOnlySpan<byte> bytes)
    {
        ValidateRole(role,mime);
        if(bytes.Length is <1 or >WorkspacePublicationRepository.MaximumPayloadBytes ||
            mime=="application/json" && bytes.Length>ProtocolDocumentReader.MaximumDocumentBytes)
            throw new ArgumentException("Catalog dependency payload exceeds its exact role bound.",nameof(bytes));
        var owned=bytes.ToArray();
        if(mime=="application/json" && !new CanonicalJson(ProtocolDocumentReader.MaximumDocumentBytes).Encode(owned).AsSpan().SequenceEqual(owned))
            throw new ProtocolFormatException("Catalog dependency JSON must be canonical before preparation.");
        return new(role,mime,owned);
    }

    internal byte[] CopyBytes()=>_bytes.ToArray();
    public Guid IdFor(WorkspacePublicationScope scope)=>ObjectId(scope,Role,MimeType,Sha256);

    // Keep the established byte format, GUID bits and endianness exactly. The
    // existing legacy dependency sink delegates here without changing identities.
    public static Guid ObjectId(WorkspacePublicationScope scope,string role,string mime,string hash)
    {
        WorkspacePublicationRepository.ValidateScope(scope);ValidateRole(role,mime);
        if(hash is not {Length:64} || hash.Any(value=>value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("An exact captured dependency digest is required.",nameof(hash));
        var context=new CanonicalJson(8192).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {format="mpa-catalog-dependency-outbox",major=1,scope,role,mimeType=mime,payloadSha256=hash}));
        var digest=SHA256.HashData(context);digest[6]=(byte)((digest[6]&15)|128);digest[8]=(byte)((digest[8]&63)|128);
        return new(digest.AsSpan(0,16),bigEndian:true);
    }
    private static void ValidateRole(string role,string mime)
    {
        if(role=="facts-chunk" ? mime!="application/gzip" : role is not("facts-index" or "coverage-page" or "coverage-root") || mime!="application/json")
            throw new ArgumentException("Only exact catalog dependency roles and MIME types may be prepared.");
    }
}
