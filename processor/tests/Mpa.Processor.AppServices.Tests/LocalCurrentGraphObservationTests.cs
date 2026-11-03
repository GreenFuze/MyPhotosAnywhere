using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task LocalCurrentGraphObservationsKeepExpectedReferencesSeparateFromActuallyReceivedBadBytesAcrossRetries()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var reference=GraphObservationReference();var metadata=GraphObservationMetadata(7);var bad=CurrentHash("XX"u8.ToArray());
        var reading=await GraphStateAsync(fixture,store=>store.RememberMetadata(capture.Attempt,reference,metadata)).ConfigureAwait(false);
        Assert.IsNull(reading.BodySha256);Assert.AreEqual(reference.Sha256,reading.Reference.Sha256);
        var actual=await GraphStateAsync(fixture,store=>store.RememberBytes(capture.Attempt,reading,bad)).ConfigureAwait(false);
        Assert.AreEqual(7L,actual.BodyVersion);Assert.AreEqual(bad,actual.BodySha256);Assert.AreNotEqual(reference.Sha256,actual.BodySha256);
        await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(()=>GraphStateAsync(fixture,store=>store.RememberBytes(capture.Attempt,actual,reference.Sha256))).ConfigureAwait(false);
        var next=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(()=>GraphStateAsync(fixture,store=>store.RememberMetadata(next.Attempt,reference,GraphObservationMetadata(6)))).ConfigureAwait(false);
        reading=await GraphStateAsync(fixture,store=>store.RememberMetadata(next.Attempt,reference,GraphObservationMetadata(8))).ConfigureAwait(false);
        var complete=await GraphStateAsync(fixture,store=>store.RememberBytes(next.Attempt,reading,reference.Sha256)).ConfigureAwait(false);
        Assert.AreEqual(8L,complete.BodyVersion);Assert.AreEqual(complete,await GraphStateAsync(fixture,store=>store.Current(next.Attempt,reference)).ConfigureAwait(false));
        Assert.AreEqual(3,fixture.Data.Creates);Assert.AreEqual(3,fixture.Data.Allocations);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task LocalCurrentGraphObservationsRejectLostOrCorruptPairsWithoutResettingWork(int change)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var reference=GraphObservationReference();await GraphStateAsync(fixture,store=>store.RememberMetadata(capture.Attempt,reference,GraphObservationMetadata(7))).ConfigureAwait(false);
        var sql=change switch
        {
            0=>"DELETE FROM processor_meta WHERE key GLOB 'local_current_graph_v1:object:*' AND key NOT LIKE '%:sha256'",
            1=>"DELETE FROM processor_meta WHERE key GLOB 'local_current_graph_v1:object:*:sha256'",
            _=>"UPDATE processor_meta SET value='{}' WHERE key GLOB 'local_current_graph_v1:object:*' AND key NOT LIKE '%:sha256'"
        };
        Assert.AreEqual(1,await CompletionSqlAsync(fixture.Data,sql).ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>GraphStateAsync(fixture,store=>store.Current(capture.Attempt,reference))).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>GraphStateAsync(fixture,store=>store.RememberMetadata(capture.Attempt,reference,GraphObservationMetadata(8)))).ConfigureAwait(false);
        Assert.AreEqual(capture.Attempt,await CurrentStateAsync(fixture.Data,store=>store.Current()).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task LocalCurrentGraphObservationsCannotRetargetExpectedBytesOrCompleteAnOldNonce()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var reference=GraphObservationReference();var reading=await GraphStateAsync(fixture,store=>store.RememberMetadata(capture.Attempt,reference,GraphObservationMetadata(7))).ConfigureAwait(false);
        var changed=reference with {Sha256=CurrentHash("[]"u8.ToArray())};
        await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(()=>GraphStateAsync(fixture,store=>store.RememberMetadata(capture.Attempt,changed,GraphObservationMetadata(8)))).ConfigureAwait(false);
        var next=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>GraphStateAsync(fixture,store=>store.RememberBytes(capture.Attempt,reading,reference.Sha256))).ConfigureAwait(false);
        Assert.AreEqual(reading,await GraphStateAsync(fixture,store=>store.Current(next.Attempt,reference)).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task LocalCurrentGraphObservationsKeepCheckpointWritesAtomicAndStorageReservationsJoined()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var reference=GraphObservationReference();var space=new CurrentAllocationSpace();var budget=new StorageBudgetManager(space);space.Available=1;
        await Assert.ThrowsExactlyAsync<InsufficientStorageBudgetException>(()=>GraphStateAsync(fixture,store=>store.RememberMetadata(capture.Attempt,reference,GraphObservationMetadata(7)),budget)).ConfigureAwait(false);
        Assert.IsNull(await GraphStateAsync(fixture,store=>store.Current(capture.Attempt,reference)).ConfigureAwait(false));space.Available=20*StorageBudgetManager.Gibibyte;
        var reading=await GraphStateAsync(fixture,store=>store.RememberMetadata(capture.Attempt,reference,GraphObservationMetadata(7)),budget).ConfigureAwait(false);
        await CompletionSqlAsync(fixture.Data,"""
            CREATE TRIGGER fail_graph_checkpoint BEFORE UPDATE ON processor_meta
            WHEN NEW.key GLOB 'local_current_graph_v1:object:*:sha256'
            BEGIN SELECT RAISE(ABORT,'synthetic graph checkpoint failure'); END;
            """).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(()=>GraphStateAsync(fixture,store=>store.RememberBytes(capture.Attempt,reading,reference.Sha256),budget)).ConfigureAwait(false);
        Assert.AreEqual(reading,await GraphStateAsync(fixture,store=>store.Current(capture.Attempt,reference)).ConfigureAwait(false));
        await CompletionSqlAsync(fixture.Data,"DROP TRIGGER fail_graph_checkpoint").ConfigureAwait(false);
        Assert.AreEqual(reference.Sha256,(await GraphStateAsync(fixture,store=>store.RememberBytes(capture.Attempt,reading,reference.Sha256),budget).ConfigureAwait(false)).BodySha256);
        Assert.AreEqual(0L,budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task LocalCurrentGraphObservationsKeepFullInt64VersionsAndRejectCorpusMimeAndSameVersionMetadataForks()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var reference=GraphObservationReference();var reading=await GraphStateAsync(fixture,store=>store.RememberMetadata(capture.Attempt,reference,GraphObservationMetadata(long.MaxValue))).ConfigureAwait(false);
        Assert.AreEqual(long.MaxValue,(await GraphStateAsync(fixture,store=>store.RememberBytes(capture.Attempt,reading,reference.Sha256)).ConfigureAwait(false)).BodyVersion);
        await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(()=>GraphStateAsync(fixture,store=>store.RememberMetadata(capture.Attempt,reference,GraphObservationMetadata(long.MaxValue) with {ParentsSha256=new string('b',64)}))).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>GraphStateAsync(fixture,store=>store.RememberMetadata(capture.Attempt,reference,GraphObservationMetadata(long.MaxValue) with {SharedDriveId="foreign"}))).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(()=>GraphStateAsync(fixture,store=>store.RememberMetadata(capture.Attempt,reference,GraphObservationMetadata(long.MaxValue) with {MimeType="image/jpeg"}))).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LocalCurrentGraphObservationsHonorRetainedLegacyImmutableFloorsAndCapturedPhase()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var reference=capture.Head.Publishers.Reference;
        await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(()=>GraphStateAsync(fixture,store=>store.RememberMetadata(capture.Attempt,reference,GraphObservationMetadata(1) with {Bytes=checked((int)reference.Bytes)}))).ConfigureAwait(false);
        var reading=await GraphStateAsync(fixture,store=>store.RememberMetadata(capture.Attempt,GraphObservationReference(),GraphObservationMetadata(7))).ConfigureAwait(false);
        _=await fixture.Reader.FenceAsync(capture,CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>GraphStateAsync(fixture,store=>store.RememberBytes(capture.Attempt,reading,reading.Reference.Sha256))).ConfigureAwait(false);
    }

    private static CatalogObjectReference GraphObservationReference()=>new("permission-A","graph-observation","metadata",2,CurrentHash("{}"u8.ToArray()));
    private static LocalCurrentGraphMetadata GraphObservationMetadata(long version)=>new(version,2,"application/json",null,new string('a',64));
    private static Task<T> GraphStateAsync<T>(CurrentReaderFixture fixture,Func<LocalCurrentGraphObservations,T> action,StorageBudgetManager? budget=null)=>
        fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>action(new(database,budget ?? fixture.Data.Budget)),CancellationToken.None);
}
