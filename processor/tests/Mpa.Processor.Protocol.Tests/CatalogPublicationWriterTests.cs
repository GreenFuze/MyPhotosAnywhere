using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

public sealed partial class CatalogPublicationTests
{
    [TestMethod]
    public void OutgoingCommitMatchesTheSharedGoldenPayloadWithoutClaimingSignatureVerification()
    {
        using var fixture = Fixture(); var payload = fixture.RootElement.GetProperty("commit");
        var commit = new CatalogPublicationReader(Workspace).ReadCommit(Verified(payload));
        var encoded = new CatalogPublicationWriter(Workspace).EncodeCommit(commit, 3);
        CollectionAssert.AreEqual(Canonical(payload), encoded);
        using var parsed = JsonDocument.Parse(encoded); Assert.IsFalse(parsed.RootElement.TryGetProperty("signature", out _));
        Assert.AreEqual(commit, new CatalogPublicationReader(Workspace).ReadCommit(Verified(parsed.RootElement)));
    }

    [TestMethod]
    public void OutgoingCommitRejectsInvalidReferencesAliasingHistoryAndGenesisBeforeSigning()
    {
        using var fixture = Fixture(); var valid = new CatalogPublicationReader(Workspace).ReadCommit(Verified(fixture.RootElement.GetProperty("commit")));
        var writer = new CatalogPublicationWriter(Workspace);
        foreach (var wrong in new[] { valid with { GenerationId = Guid.Empty }, valid with { Parent = null }, valid with { RootIndex = valid.SourceCoverage },
            valid with { Parent = valid.RootIndex }, valid with { SettingsRevision = -1 }, valid with { MinimumHistoryGeneration = 4 },
            valid with { RootIndex = valid.RootIndex with { AccountId = "other" } }, valid with { RootIndex = valid.RootIndex with { WorkspaceId = "other" } },
            valid with { RootIndex = valid.RootIndex with { FileId = "root" } }, valid with { RootIndex = valid.RootIndex with { Bytes = 262145 } },
            valid with { SourceCoverage = valid.SourceCoverage with { Sha256 = "invalid" } } })
            Assert.ThrowsExactly<ProtocolFormatException>(() => writer.EncodeCommit(wrong, 3));
        Assert.ThrowsExactly<ProtocolFormatException>(() => writer.EncodeCommit(valid, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => writer.EncodeCommit(valid, 0));
        Assert.IsNotNull(writer.EncodeCommit(valid with { Parent = null }, 1));
    }
}
