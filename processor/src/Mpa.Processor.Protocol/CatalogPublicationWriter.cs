using System.Globalization;
using System.Text.Json;

namespace Mpa.Processor.Protocol;

/// <summary>Outgoing canonical payload serialization only. This never constructs a
/// verified document, signs, publishes, establishes authority or validates ancestry.</summary>
public sealed class CatalogPublicationWriter(CatalogWorkspace workspace)
{
    private readonly CatalogPublicationReader _reader = new(workspace ?? throw new ArgumentNullException(nameof(workspace)));
    public byte[] EncodeCommit(CatalogCommit commit, long sequence)
    {
        ArgumentNullException.ThrowIfNull(commit); ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1L);
        var bytes = new CanonicalJson(CatalogPublicationReader.MaximumIndexBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            generationId = commit.GenerationId.ToString("D"), parent = commit.Parent is null ? null : Wire(commit.Parent),
            rootIndex = Wire(commit.RootIndex), sourceCoverage = Wire(commit.SourceCoverage),
            settingsRevision = commit.SettingsRevision.ToString(CultureInfo.InvariantCulture), minimumHistoryGeneration = commit.MinimumHistoryGeneration.ToString(CultureInfo.InvariantCulture)
        }));
        if (_reader.ValidateCommitPayload(bytes, sequence) != commit) throw new InvalidDataException("Outgoing catalog commit changed during canonical serialization.");
        return bytes;
    }
    private static object Wire(CatalogObjectReference value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.FileId == "root") throw new ProtocolFormatException("A catalog object cannot be the Drive root alias.");
        return new { accountId = value.AccountId, fileId = value.FileId, workspaceId = value.WorkspaceId, bytes = value.Bytes.ToString(CultureInfo.InvariantCulture), sha256 = value.Sha256 };
    }
}
