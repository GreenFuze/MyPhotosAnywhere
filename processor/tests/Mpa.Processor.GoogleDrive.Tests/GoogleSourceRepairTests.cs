using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

public sealed partial class GoogleSourceTests
{
    [TestMethod]
    public async Task IndividualRepairReadsOnlyTheCapturedFileAndRevalidatesItsParent()
    {
        using var fixture = new Fixture("""
            {"id":"file-A","version":"7","name":"repaired.jpg","mimeType":"image/jpeg","parents":["folder-A"],"driveId":"drive-A","size":"123"}
            """);
        await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
        var item = await connection.ReadSourceItemAsync("folder-A", "drive-A", "file-A", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("file-A", item.Id); Assert.AreEqual(7L, item.Version); Assert.AreEqual(123L, item.Size);
        Assert.AreEqual(2, fixture.Calls); StringAssert.Contains(fixture.Query!, "supportsAllDrives=true");
    }

    [TestMethod]
    public async Task RepairRejectsIdentityOrScopeSubstitutionBeforeParsingOptionalMetadata()
    {
        using (var wrongIdentity = new Fixture("""{"id":"different-file","parents":["folder-A"],"driveId":"drive-A"}"""))
        {
            await using var connection = await wrongIdentity.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => connection.ReadSourceItemAsync("folder-A", "drive-A", "file-A", CancellationToken.None)).ConfigureAwait(false);
        }
        foreach (var body in new[] {
            """{"id":"file-A","parents":["another-folder"],"driveId":"drive-A"}""",
            """{"id":"file-A","parents":["folder-A"],"driveId":"another-drive"}"""
        })
        {
            using var fixture = new Fixture(body); await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<DriveSourceItemUnavailableException>(() => connection.ReadSourceItemAsync("folder-A", "drive-A", "file-A", CancellationToken.None)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task InvalidRepairMetadataIsExplicitAndBadRequestIdsDoNotReachGoogle()
    {
        using var fixture = new Fixture("""{"id":"file-A","parents":["folder-A"],"version":"invalid"}""");
        await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadSourceItemAsync("root", null, "file-A", CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadSourceItemAsync("folder-A", null, "invalid/path", CancellationToken.None));
        Assert.AreEqual(0, fixture.Calls);
        await Assert.ThrowsExactlyAsync<DriveSourceMetadataException>(() => connection.ReadSourceItemAsync("folder-A", null, "file-A", CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MalformedTimestampTextIsAnIsolatedFailureInsteadOfCrashingCatalogConversion()
    {
        using var fixture = new Fixture("""
            {"files":[
              {"id":"bad-time","version":"1","name":"bad.jpg","mimeType":"image/jpeg","parents":["folder-A"],"imageMediaMetadata":{"time":"2026\ninvalid"}},
              {"id":"healthy","version":"1","name":"good.jpg","mimeType":"image/jpeg","parents":["folder-A"]}
            ]}
            """);
        await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
        var page = await connection.ReadSourcePageAsync("folder-A", null, null, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("healthy", page.Items.Single().Id); Assert.AreEqual("bad-time", page.Failures.Single().FileId);
    }
}
