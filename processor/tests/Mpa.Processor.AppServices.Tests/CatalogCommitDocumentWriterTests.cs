using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class JournaledProtocolSignerTests
{
    private static readonly string[] WrittenCommitCapabilities = [DriveCatalogFacts.Capability, CatalogPublicationReader.Capability];

    [TestMethod]
    public async Task CapturedCommitBodyUsesJournaledPublisherAndReplaysExactEnvelope()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false); var plan = await CommitPlanAsync(fixture).ConfigureAwait(false);
        var writer = new CatalogCommitDocumentWriter(plan); var id = Guid.NewGuid();
        var signed = await fixture.Signer.SignCatalogAsync(id, writer.Encode, CancellationToken.None).ConfigureAwait(false);
        var publicIdentity = fixture.Keys.PublicIdentity;
        var verified = new SignedProtocolDocuments(WrittenCommitCapabilities).VerifyAndRead(signed.Envelope!.Value.Span,
            new(publicIdentity.LibraryId, publicIdentity.PublisherId, publicIdentity.PublisherEpoch, Convert.FromBase64String(publicIdentity.PublisherPublicKeySpki), ["catalog-commit"]));
        var commit = new CatalogPublicationReader(new("account", "metadata")).ReadCommit(verified);
        Assert.AreEqual(plan.Scope.GenerationId, commit.GenerationId); Assert.AreEqual(plan.Dependencies.RootIndex, commit.RootIndex); Assert.IsNull(commit.Parent);
        Assert.AreEqual(1L, signed.Sequence);
        var replay = await fixture.Signer.SignCatalogAsync(id, writer.Encode, CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(signed.Envelope.Value.ToArray(), replay.Envelope!.Value.ToArray());
        using var body = JsonDocument.Parse(signed.Body); Assert.IsFalse(body.RootElement.TryGetProperty("signature", out _));
        await Assert.ThrowsExactlyAsync<ProtocolSigningConflictException>(() => fixture.Signer.SignCatalogAsync(id,
            new CatalogCommitDocumentWriter(plan with { SettingsRevision = 1 }).Encode, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task InvalidCommitContextCannotBurnSequenceAndLaterCommitsRequireTheirCapturedParent()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false); var plan = await CommitPlanAsync(fixture).ConfigureAwait(false);
        var writer = new CatalogCommitDocumentWriter(plan);
        foreach (var wrong in new[] { plan with { Scope = plan.Scope with { ActorId = fixture.Keys.PublicIdentity.ControllerId } },
            plan with { Dependencies = plan.Dependencies with { SourcePolicySha256 = new string('b', 64) } },
            plan with { Dependencies = plan.Dependencies with { ConfigurationRevision = 2 } } })
        {
            var id = Guid.NewGuid();
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Signer.SignCatalogAsync(id, new CatalogCommitDocumentWriter(wrong).Encode, CancellationToken.None)).ConfigureAwait(false);
            Assert.IsNull(await fixture.ReadAsync(id).ConfigureAwait(false));
        }
        Assert.AreEqual(1L, (await fixture.Signer.SignCatalogAsync(Guid.NewGuid(), writer.Encode, CancellationToken.None).ConfigureAwait(false)).Sequence);
        var invalidLater = Guid.NewGuid();
        await Assert.ThrowsExactlyAsync<ProtocolFormatException>(() => fixture.Signer.SignCatalogAsync(invalidLater,
            writer.Encode, CancellationToken.None)).ConfigureAwait(false); Assert.IsNull(await fixture.ReadAsync(invalidLater).ConfigureAwait(false));
        var next = plan with { Scope = plan.Scope with { GenerationId = Guid.NewGuid() }, Parent = plan.Dependencies.RootIndex with { FileId = "previous-commit" } };
        var later = await fixture.Signer.SignCatalogAsync(Guid.NewGuid(), new CatalogCommitDocumentWriter(next).Encode, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2L, later.Sequence); // Typed parent validation here is not an ancestry/remote-availability proof.
    }

    private static async Task<CatalogCommitPlan> CommitPlanAsync(Fixture fixture)
    {
        await fixture.BootstrapAsync(new(1, false)).ConfigureAwait(false);
        var sources = await fixture.Signer.SignInitialSourceConfigurationAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
        var approved = await fixture.State.ExecuteAsync(db => new LibraryAuthorityRepository(db, fixture.Budget).SelectSourceConfiguration(sources.Envelope!.Value.Span), CancellationToken.None).ConfigureAwait(false);
        var identity = fixture.Keys.PublicIdentity;
        var scope = new WorkspacePublicationScope(identity.LibraryId, identity.PublisherId, identity.PublisherEpoch, Guid.NewGuid(), "account", 1, "metadata", 1, new string('a', 64));
        var dependencies = new PreparedCatalogDependencies(identity.LibraryId, 1, 1, 0, approved.EnvelopeSha256,
            new("account", "index", "metadata", 16, new string('b', 64)), new("account", "coverage", "metadata", 16, new string('c', 64)));
        return new(scope, dependencies, approved, null, 0, 1);
    }
}
