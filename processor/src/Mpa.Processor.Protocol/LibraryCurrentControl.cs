using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mpa.Processor.Protocol;

public sealed record LibraryPolicyReference(CatalogObjectReference Reference, long Sequence);

/// <summary>A root-signed snapshot for one independently pinned mutable Drive object.
/// Decoding never proves freshness. The transport must read that exact actual file,
/// fence its version after staging, and atomically retain its high-water mark before
/// activation. Neither this snapshot nor its connection grants original-write access.</summary>
public sealed class LibraryCurrentControl
{
    public const string Kind = "library-current-control";
    public const string Capability = "library.current.v1";
    public string ConnectionSha256 { get; }
    public long Sequence { get; }
    public string EnvelopeSha256 { get; }
    public LibraryPolicyReference Publishers { get; }
    public LibraryPolicyReference Sources { get; }
    public CatalogObjectReference? Catalog { get; }

    private LibraryCurrentControl(string connectionSha256, long sequence, string hash,
        LibraryPolicyReference publishers, LibraryPolicyReference sources, CatalogObjectReference? catalog)
    {
        ConnectionSha256 = connectionSha256; Sequence = sequence; EnvelopeSha256 = hash;
        Publishers = publishers; Sources = sources; Catalog = catalog;
    }

    public static LibraryCurrentControl Read(ReadOnlySpan<byte> envelope, LibraryConnectionDescriptor approved)
    {
        ArgumentNullException.ThrowIfNull(approved);
        if (approved.CurrentFileId is null) throw new ProtocolFormatException("Independently approve a current-control file identity first; legacy pins cannot infer one.");
        var authority = new TrustedProtocolActor(approved.LibraryId, approved.ControllerId, approved.ControllerEpoch,
            Convert.FromBase64String(approved.ControllerPublicKeySpki), [Kind]);
        var owned = OwnEnvelope(envelope);
        var verified = new SignedProtocolDocuments([Capability]).VerifyAndRead(owned, authority);
        if (verified.Kind != Kind || !verified.RequiredCapabilities.Contains(Capability, StringComparer.Ordinal))
            throw new ProtocolFormatException("A current-control snapshot requires its explicit capability.");
        var selected = ValidatePayload(Encoding.UTF8.GetBytes(verified.CanonicalPayload),verified.Sequence,approved);
        return new(approved.DocumentSha256,verified.Sequence,Convert.ToHexStringLower(SHA256.HashData(owned)),selected.Publishers,selected.Sources,selected.Catalog);
    }

    internal static (LibraryPolicyReference Publishers, LibraryPolicyReference Sources, CatalogObjectReference? Catalog)
        ValidatePayload(ReadOnlySpan<byte> payload,long sequence,LibraryConnectionDescriptor approved)
    {
        ArgumentNullException.ThrowIfNull(approved);
        if (approved.CurrentFileId is null || sequence < 1) throw new ProtocolFormatException("A reserved concrete control identity and positive root sequence are required.");
        var canonical = new CanonicalJson(ProtocolDocumentReader.MaximumDocumentBytes).Encode(payload);
        if (!payload.SequenceEqual(canonical)) throw new ProtocolFormatException("Current-control payload must already be canonical.");
        using var document = JsonDocument.Parse(canonical);var root = document.RootElement;
        Fields(root,"workspace","currentFileId","publishers","sources","catalog");
        var workspace = root.GetProperty("workspace");Fields(workspace,"accountId","folderId");
        if (Text(workspace,"accountId") != approved.Workspace.AccountId || Text(workspace,"folderId") != approved.Workspace.FolderId || Text(root,"currentFileId") != approved.CurrentFileId)
            throw new ProtocolFormatException("Current control cannot retarget its independently approved workspace or file.");
        var ids = new HashSet<string>(StringComparer.Ordinal) { approved.CurrentFileId };
        var publishers = Policy(root.GetProperty("publishers")); var sources = Policy(root.GetProperty("sources"));
        if (publishers.Sequence == sources.Sequence) throw new ProtocolFormatException("Distinct controller policies cannot reuse a root sequence.");
        var catalogValue = root.GetProperty("catalog"); var catalog = catalogValue.ValueKind == JsonValueKind.Null ? null : Reference(catalogValue);
        return (publishers,sources,catalog);

        LibraryPolicyReference Policy(JsonElement value)
        {
            Fields(value, "object", "sequence"); var selectedSequence = Counter(value, "sequence");
            if (selectedSequence >= sequence) throw new ProtocolFormatException("A selected policy must precede its current-control signature in the root stream.");
            return new(Reference(value.GetProperty("object")), selectedSequence);
        }
        CatalogObjectReference Reference(JsonElement value)
        {
            Fields(value, "accountId", "fileId", "workspaceId", "bytes", "sha256");
            var account = Text(value, "accountId"); var file = Text(value, "fileId"); var folder = Text(value, "workspaceId");
            CatalogPublicationReader.CheckOpaque(file);
            var length = Counter(value, "bytes"); var hash = Text(value, "sha256");
            if (account != approved.Workspace.AccountId || folder != approved.Workspace.FolderId || file == "root" || file == folder ||
                !ids.Add(file) || length > ProtocolDocumentReader.MaximumDocumentBytes || hash.Length != 64 || hash.Any(c => !IsHex(c)))
                throw new ProtocolFormatException("Current control requires distinct bounded immutable objects in its pinned workspace.");
            return new(account, file, folder, length, hash);
        }
    }

    /// <summary>Compare against the durably accepted exact head, never a received
    /// self-asserted predecessor. Changed policies must have been signed after that
    /// earlier control head. This also rejects unpublished older orphan policies.</summary>
    public void AssertSuccessorOf(LibraryCurrentControl previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (ConnectionSha256 != previous.ConnectionSha256 || Sequence < previous.Sequence ||
            Sequence == previous.Sequence && EnvelopeSha256 != previous.EnvelopeSha256 ||
            Sequence > previous.Sequence && (Publishers != previous.Publishers && Publishers.Sequence <= previous.Sequence ||
                Sources != previous.Sources && Sources.Sequence <= previous.Sequence))
            throw new ProtocolFormatException("Current-control history was rewound, forked, retargeted or selected an older policy.");
    }

    public CatalogPublisherAuthorization ReadPublishers(ReadOnlySpan<byte> envelope, LibraryConnectionDescriptor approved)
    {
        var owned = OwnEnvelope(envelope);
        AssertEnvelope(owned, Publishers, approved);
        var result = new CatalogPublicationReader(approved.Workspace).ReadPublisherAuthorization(owned, Root(approved, "library-publisher-authorization"));
        if (result.Sequence != Publishers.Sequence || result.Publisher is null && Catalog is not null)
            throw new ProtocolFormatException("Current-control publisher sequence or revoked catalog selection is invalid.");
        return result;
    }

    public CatalogSourceConfiguration ReadSources(ReadOnlySpan<byte> envelope, LibraryConnectionDescriptor approved)
    {
        var owned = OwnEnvelope(envelope);
        AssertEnvelope(owned, Sources, approved);
        var verified = new SignedProtocolDocuments([CatalogSourceConfiguration.Capability]).VerifyAndRead(owned, Root(approved, "library-source-configuration"));
        if (verified.Sequence != Sources.Sequence) throw new ProtocolFormatException("Current-control source sequence changed.");
        return new CatalogPublicationReader(approved.Workspace).ReadSourceConfiguration(verified, approved.LibraryId);
    }

    private static byte[] OwnEnvelope(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length is < 1 or > ProtocolDocumentReader.MaximumDocumentBytes) throw new ProtocolFormatException("Current-control envelope exceeds its wire bound.");
        return envelope.ToArray();
    }
    private void AssertEnvelope(ReadOnlySpan<byte> envelope, LibraryPolicyReference selected, LibraryConnectionDescriptor approved)
    {
        ArgumentNullException.ThrowIfNull(approved);
        if (approved.DocumentSha256 != ConnectionSha256 || envelope.Length != selected.Reference.Bytes ||
            Convert.ToHexStringLower(SHA256.HashData(envelope)) != selected.Reference.Sha256)
            throw new ProtocolFormatException("Selected policy differs from the pinned control's exact immutable bytes.");
    }
    private static TrustedProtocolActor Root(LibraryConnectionDescriptor approved, string kind) => new(approved.LibraryId, approved.ControllerId,
        approved.ControllerEpoch, Convert.FromBase64String(approved.ControllerPublicKeySpki), [kind]);
    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f';
    private static long Counter(JsonElement root, string name)
    {
        var text = Text(root, name);
        if (text.Length is < 1 or > 19 || text[0] == '0' || text.Any(c => c is < '0' or > '9') ||
            !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result < 1)
            throw new ProtocolFormatException("A canonical positive decimal counter is required.");
        return result;
    }
    private static string Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new ProtocolFormatException("Missing current-control text field.");
    private static void Fields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != fields.Length || fields.Any(field => !value.TryGetProperty(field, out _)))
            throw new ProtocolFormatException("Unexpected current-control fields.");
    }
}
