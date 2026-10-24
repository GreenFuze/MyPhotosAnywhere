using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestCategory("ImmutableDependencyReuse")]
    [TestMethod]
    public async Task ChangedGenerationPublishesNewFactsWhileReusingExactUnchangedCompressedPartitions()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        var files = await fixture.Setup.Accounts.State.ExecuteAsync(db =>
        {
            var identity = new DriveAssetIdentity(new LibraryAuthorityRepository(db).Controller()!.LibraryId);
            return Enumerable.Range(0, 1024).Select(i => "reuse-photo-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .GroupBy(file => identity.Derive("permission-A", file).ToString("N")[..2], StringComparer.Ordinal)
                .Take(3).Select(group => group.First()).ToArray();
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(3, files);
        await AddPreparationPhotoAsync(fixture, files[0]).ConfigureAwait(false);
        await AddPreparationPhotoAsync(fixture, files[1]).ConfigureAwait(false);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        await using var completion = new CatalogGenerationCompletionService(fixture.Setup.Accounts.State, fixture.Budget, current);
        var first = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var head = await completion.FinishAsync(first.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2L, head.Assets);
        var original = await fixture.Setup.Accounts.State.ExecuteAsync(db =>
        {
            var outbox = new WorkspacePublicationRepository(db, fixture.Budget);
            return outbox.Identities(4096).Select(id => outbox.Read(id)!).Where(item =>
                item.Scope == first.Capture.Scope && item.Role == "facts-chunk").ToArray();
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(2, original);
        await AddPreparationPhotoAsync(fixture, files[2]).ConfigureAwait(false);
        var next = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(3L, next.Capture.Snapshot!.Assets); Assert.AreNotEqual(first.Plan!.RootIndex, next.Plan!.RootIndex);
        await fixture.Setup.Accounts.State.ExecuteAsync(db =>
        {
            var references = new CatalogDependencyReferenceRepository(db, fixture.Budget);
            var outbox = new WorkspacePublicationRepository(db, fixture.Budget);
            foreach (var old in original)
            {
                var selected = references.ForFile(next.Capture.Scope, old.FileId!)!;
                Assert.IsNotNull(selected); Assert.AreEqual(old.Id, selected.OriginalPublicationId);
                Assert.AreEqual(old.FileId, selected.FileId); Assert.AreEqual("application/gzip", selected.MimeType);
                var unchanged = outbox.Read(old.Id)!; Assert.AreEqual(old.Scope, unchanged.Scope);
                CollectionAssert.AreEqual(old.Payload.ToArray(), unchanged.Payload.ToArray());
            }
            Assert.HasCount(1, outbox.Identities(4096).Select(id => outbox.Read(id)!).Where(item =>
                item.Scope == next.Capture.Scope && item.Role == "facts-chunk").ToArray());
            Assert.AreEqual(2L, new CatalogGenerationCaptureRepository(db, fixture.Budget).Verification(first.Capture.Scope.GenerationId)!.Assets);
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
        var result = await completion.FinishAsync(next.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(head.Sequence + 1, result.Sequence); Assert.AreEqual(3L, result.Assets);
        await AssertRetiredCompletionAsync(fixture, result).ConfigureAwait(false);
    }
}
