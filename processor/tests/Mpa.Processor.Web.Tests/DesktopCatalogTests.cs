using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.AppServices;
using Mpa.Processor.Credentials;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.Web.Tests;

[TestClass]
public sealed class DesktopCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PreviewResolvesOnlyVisibleCanonicalIdentitiesAndRejectsLateSourceOrRevisionChanges(bool replaceRevision)
    {
        await using var fixture = new Fixture(); await using var vault = new NoCredentials(); var google = new HeldPreviews();
        await using var accounts = new ProcessorAccountService(fixture.State, vault, google, google);
        await using var work = new ProcessorWorkSupervisor();
        var application = new DesktopEngineApplication(Guid.NewGuid(), fixture.State, accounts, work);
        await fixture.State.ExecuteAsync(database => new DiscoveredCatalogRepository(database).ApplyBatch(fixture.SourceA, 1, 1, [Fact("captured-file", 10)]), CancellationToken.None).ConfigureAwait(false);
        var page = await application.CatalogAsync(null, null, false, null, CancellationToken.None).ConfigureAwait(false); var id = page.Photos.Single().Id;
        Assert.IsNull(await application.CatalogPreviewAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false)); Assert.AreEqual(0, google.Reads);
        var preview = application.CatalogPreviewAsync(id, CancellationToken.None);
        await google.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        try
        {
            await fixture.State.ExecuteAsync(database =>
            {
                if (replaceRevision) return new DiscoveredCatalogRepository(database).ApplyBatch(fixture.SourceA, 1, 1, [Fact("captured-file", 20) with { DriveVersion = 2 }]);
                var sources = new LibrarySourceRepository(database); sources.Update(fixture.SourceA, false, true, 1, sources.Snapshot().Revision); return 0;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { google.Release.TrySetResult(); }
        Assert.IsNull(await preview.ConfigureAwait(false)); Assert.AreEqual("captured-file", google.File); Assert.AreEqual(1, google.Reads);
        if (!replaceRevision) { Assert.IsNull(await application.CatalogPreviewAsync(id, CancellationToken.None).ConfigureAwait(false)); Assert.AreEqual(1, google.Reads); }
    }
    [TestMethod]
    public async Task GalleryCombinesSelectedSourcesAcrossAccountsWithStableBidirectionalKeysAndUnknownDatesLast()
    {
        await using var fixture = new Fixture();
        await fixture.State.ExecuteAsync(database =>
        {
            var catalog = new DiscoveredCatalogRepository(database);
            catalog.ApplyBatch(fixture.SourceA, 1, 1, Enumerable.Range(0, 137).Select(index => Fact("file-" + index.ToString(CultureInfo.InvariantCulture), index < 130 ? index / 7 : null)).ToArray());
            catalog.ApplyBatch(fixture.SourceB, 1, 1, [Fact("file-1", 10)]); return true;
        }, CancellationToken.None).ConfigureAwait(false);
        var first = await fixture.Reader.ReadAsync(null, null, false, null, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(60, first.Photos.Count); Assert.IsNull(first.NewerCursor); Assert.IsNotNull(first.OlderCursor);
        var next = await fixture.Reader.ReadAsync(null, first.OlderCursor, false, null, CancellationToken.None).ConfigureAwait(false);
        var previous = await fixture.Reader.ReadAsync(null, next.NewerCursor, true, null, CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(first.Photos.Select(photo => photo.Id).ToArray(), previous.Photos.Select(photo => photo.Id).ToArray());
        var last = await fixture.Reader.ReadAsync(null, next.OlderCursor, false, null, CancellationToken.None).ConfigureAwait(false);
        var all = first.Photos.Concat(next.Photos).Concat(last.Photos).ToArray();
        Assert.AreEqual(138, all.Length); Assert.AreEqual(138, all.Select(photo => photo.Id).Distinct().Count()); Assert.IsNull(last.OlderCursor);
        Assert.AreEqual(7, all.Count(photo => photo.DateProvenance == "Unknown")); Assert.IsTrue(all[^1].EffectiveUtc is null);
        var filtered = await fixture.Reader.ReadAsync("account-B", null, false, null, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, filtered.Photos.Count); Assert.AreEqual("account-B", filtered.Photos[0].AccountId);
        Assert.AreNotEqual(all.Single(photo => photo.AccountId == "account-A" && photo.Name == "file-1.jpg").Id, filtered.Photos[0].Id);
    }

    [TestMethod]
    public async Task DateSeekAndCapturedKeysRemainUsableDuringInsertionAndAnchorRemovalWithoutReadingGoogle()
    {
        await using var fixture = new Fixture();
        await fixture.State.ExecuteAsync(database => new DiscoveredCatalogRepository(database).ApplyBatch(fixture.SourceA, 1, 1,
            Enumerable.Range(0, 80).Select(index => Fact("initial-" + index.ToString(CultureInfo.InvariantCulture), index)).ToArray()), CancellationToken.None).ConfigureAwait(false);
        var first = await fixture.Reader.ReadAsync(null, null, false, 75, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(75L, first.Photos[0].EffectiveUtc); var edge = first.Photos[^1];
        await fixture.State.ExecuteAsync(database => new DiscoveredCatalogRepository(database).ApplyBatch(fixture.SourceB, 1, 1,
            [Fact("newer", 999), Fact("older", 1)]), CancellationToken.None).ConfigureAwait(false);
        var next = await fixture.Reader.ReadAsync(null, first.OlderCursor, false, null, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(next.Photos.All(photo => photo.EffectiveUtc <= edge.EffectiveUtc));
        await fixture.State.ExecuteAsync(database =>
        {
            var sources = new LibrarySourceRepository(database); sources.Update(fixture.SourceA, false, true, 1, sources.Snapshot().Revision); return true;
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(await fixture.Reader.ResolveAsync(edge.Id, CancellationToken.None).ConfigureAwait(false));
        var afterRemoval = await fixture.Reader.ReadAsync(null, first.OlderCursor, false, null, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("older.jpg", afterRemoval.Photos.Single().Name);
    }

    [TestMethod]
    public async Task DisconnectedAccountRetainsIndexedFactsAndMalformedOrCrossFilterCursorsAreOperationalErrors()
    {
        await using var fixture = new Fixture();
        await fixture.State.ExecuteAsync(database =>
        {
            var catalog = new DiscoveredCatalogRepository(database); catalog.ApplyBatch(fixture.SourceA, 1, 1,
                Enumerable.Range(0, 70).Select(index => Fact("offline-" + index.ToString(CultureInfo.InvariantCulture), index)).ToArray());
            var accounts = new DriveAccountRepository(database); var account = accounts.Accounts().Single(item => item.PermissionId == "account-A");
            accounts.Disconnect(account.PermissionId, account.Revision); return true;
        }, CancellationToken.None).ConfigureAwait(false);
        var page = await fixture.Reader.ReadAsync("account-A", null, false, null, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(60, page.Photos.Count); Assert.IsNotNull(page.OlderCursor);
        await Assert.ThrowsExactlyAsync<InvalidDesktopCatalogQueryException>(() => fixture.Reader.ReadAsync("account-B", page.OlderCursor, false, null, CancellationToken.None)).ConfigureAwait(false);
        foreach (var invalid in new[] { "invalid", "1|account-A|+1|00000000-0000-4000-8000-000000000001", "1|account-A|253402300800000|00000000-0000-4000-8000-000000000001" })
            await Assert.ThrowsExactlyAsync<InvalidDesktopCatalogQueryException>(() => fixture.Reader.ReadAsync("account-A", invalid, false, null, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDesktopCatalogQueryException>(() => fixture.Reader.ReadAsync(null, null, true, null, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNotNull(await fixture.Reader.ResolveAsync(page.Photos[0].Id, CancellationToken.None).ConfigureAwait(false));
    }
    private static DiscoveredDriveAsset Fact(string file, long? date) => new(file, 1, file + ".jpg", "image/jpeg", 40, 30, date, null, null, 0);
    private sealed class NoCredentials : ICredentialVault
    {
        public Task<SecretLease?> ReadAsync(string key, CancellationToken cancellationToken) => throw new InvalidOperationException("No Google credential is needed by this fixture.");
        public Task WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken) => throw new InvalidOperationException("No credential mutation is authorized.");
        public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw new InvalidOperationException("No credential deletion is authorized.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class HeldPreviews : IGoogleAccountConnectionFactory, IGoogleLibraryConnectionFactory
    {
        public int Reads { get; private set; }
        public string? File { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IGoogleAccountConnection IGoogleAccountConnectionFactory.Create(Guid connectionId, string? expectedPermissionId) => throw new InvalidOperationException("Unexpected authorization.");
        IGoogleLibraryConnection IGoogleLibraryConnectionFactory.Create(Guid connectionId, string permissionId) => new Connection(this);
        private sealed class Connection(HeldPreviews owner) : IGoogleLibraryConnection
        {
            public async Task<DriveMediaPreview?> ReadMediaPreviewAsync(string fileId, CancellationToken cancellationToken)
            {
                owner.File = fileId; owner.Reads++; owner.Started.TrySetResult(); await owner.Release.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested(); return new([1, 2, 3], "image/jpeg");
            }
            public Task<DriveMediaPage> ReadMediaPageAsync(string? pageToken, CancellationToken cancellationToken) => throw new InvalidOperationException("Gallery must not list Google Drive.");
            public Task<DriveFolder> ReadFolderAsync(string folderId, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected folder read.");
            public Task<DriveFolderPage> ReadFolderPageAsync(string parentId, string? pageToken, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected folder listing.");
            public Task<DriveSourcePage> ReadSourcePageAsync(string parentId, string? sharedDriveId, string? pageToken, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected source listing.");
            public Task<DriveSourceItem> ReadSourceItemAsync(string parentId, string? sharedDriveId, string fileId, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected source read.");
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "mpa-catalog-web-" + Guid.NewGuid().ToString("N"));
        private readonly ProcessorStateDatabase _database;
        public ProcessorStateCoordinator State { get; }
        public DesktopCatalogReader Reader { get; }
        public Guid SourceA { get; }
        public Guid SourceB { get; }
        public Fixture()
        {
            _database = new(_path); State = new(_database); Reader = new(State);
            SourceA = Add("account-A"); SourceB = Add("account-B");
        }
        private Guid Add(string account)
        {
            var accounts = new DriveAccountRepository(_database); var pending = accounts.Begin(Guid.NewGuid()); accounts.Promote(pending.Id, account, "Synthetic", null);
            var sources = new LibrarySourceRepository(_database); var id = Guid.NewGuid(); sources.Add(id, account, 1, "folder", "Synthetic", true, sources.Snapshot().Revision); return id;
        }
        public async ValueTask DisposeAsync()
        { await State.DisposeAsync().ConfigureAwait(false); _database.Dispose(); Directory.Delete(_path, recursive: true); }
    }
}
