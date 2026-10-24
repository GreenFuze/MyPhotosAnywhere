using System.Globalization;
using System.Text.Json;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

public sealed record CatalogCommitPlan(WorkspacePublicationScope Scope, PreparedCatalogDependencies Dependencies,
    AcceptedSourceConfiguration ApprovedSources, CatalogObjectReference? Parent, long SettingsRevision, long MinimumHistoryGeneration);

/// <summary>Pure bounded body factory for the journaled signer. It serializes already
/// captured dependencies, not a discovery pointer or publication permission. The
/// generation owner still proves complete ancestry, current authority and cloud history.</summary>
public sealed class CatalogCommitDocumentWriter(CatalogCommitPlan plan)
{
    private readonly CatalogCommitPlan _plan = plan ?? throw new ArgumentNullException(nameof(plan));
    private static readonly string[] Capabilities = [DriveCatalogFacts.Capability, CatalogPublicationReader.Capability];
    public byte[] Encode(LibrarySigningPublicIdentity identity, long sequence)
    {
        ArgumentNullException.ThrowIfNull(identity); var plan = _plan;
        var scope = plan.Scope; var dependencies = plan.Dependencies; var approved = plan.ApprovedSources;
        if (scope.LibraryId == Guid.Empty || scope.ActorId == Guid.Empty || scope.GenerationId == Guid.Empty || scope.ActorEpoch < 1 ||
            scope.LibraryId != identity.LibraryId || scope.ActorId != identity.PublisherId || scope.ActorEpoch != identity.PublisherEpoch ||
            dependencies.LibraryId != scope.LibraryId || approved.Configuration.LibraryId != scope.LibraryId ||
            dependencies.ConfigurationRevision != scope.ConfigurationRevision || approved.Configuration.Revision != scope.ConfigurationRevision ||
            dependencies.SourcePolicySha256 != approved.EnvelopeSha256 || approved.Configuration.Workspace.AccountId != scope.AccountId ||
            approved.Configuration.Workspace.FolderId != scope.FolderId || scope.FolderId == "root")
            throw new InvalidDataException("Prepared catalog commit changed its captured publisher, workspace or approved source configuration.");
        var payload = new CatalogPublicationWriter(new(scope.AccountId, scope.FolderId)).EncodeCommit(new(scope.GenerationId, plan.Parent,
            dependencies.RootIndex, dependencies.SourceCoverage, plan.SettingsRevision, plan.MinimumHistoryGeneration), sequence);
        using var document = JsonDocument.Parse(payload);
        var body = new CanonicalJson(ProtocolSigningJournal.MaximumBodyBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "mpa", major = 1, minor = 0, libraryId = scope.LibraryId.ToString("D"), actorId = scope.ActorId.ToString("D"),
            epoch = scope.ActorEpoch.ToString(CultureInfo.InvariantCulture), sequence = sequence.ToString(CultureInfo.InvariantCulture),
            kind = "catalog-commit", requiredCapabilities = Capabilities, payload = document.RootElement
        }));
        // Validate outgoing scope without creating a supposedly verified incoming
        // document or signing anything before the durable sequence is reserved.
        new ProtocolDocumentReader(Capabilities).ValidateForSigning(body, new(identity.LibraryId, identity.PublisherId, identity.PublisherEpoch,
            Convert.FromBase64String(identity.PublisherPublicKeySpki), ["catalog-commit"]));
        return body;
    }
}
