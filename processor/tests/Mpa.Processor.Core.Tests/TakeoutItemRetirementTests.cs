using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class TakeoutMetadataOperationTests
{
    [TestMethod]
    public async Task ExplicitBackedUpMetadataCleanupFreesTheOwnedSlotAndRetainsExactRawAndNativeIdentitiesAcrossDatabaseReopen()
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);
        var ready=await CompleteImportedObservationAsync(fixture,fixture.Sidecar,await CopyInstalledBundleAsync(fixture.Root.Path,fixture.Token).ConfigureAwait(false)).ConfigureAwait(false);
        var backup=await RetirementBackupAsync(fixture,ready.Result).ConfigureAwait(false);var paths=ReplayPaths(fixture,ready.Result);
        var original=await File.ReadAllBytesAsync(Path.Combine(fixture.Completed.Parts.Single().Location,"a.jpg"),fixture.Token).ConfigureAwait(false);
        var before=TakeoutMetadataStageStorage.MeasureOwnedBytes(fixture.StateDirectory);
        Assert.IsTrue(await fixture.Storage.RetireBackedUpItemAsync(ready.Result,backup,fixture.State,fixture.Token).ConfigureAwait(false));
        Assert.IsFalse(Directory.Exists(paths.Item));Assert.IsFalse(Directory.Exists(Path.Combine(fixture.StateDirectory,"takeout-metadata-retiring")));
        Assert.IsLessThan(before,TakeoutMetadataStageStorage.MeasureOwnedBytes(fixture.StateDirectory));
        CollectionAssert.AreEqual(fixture.Raw,(await fixture.Storage.ReadVerifiedAndRetireReplayAsync(ready.Result,fixture.State,fixture.Token).ConfigureAwait(false)).CopyRawSource());
        Assert.IsFalse(await fixture.Storage.RetireBackedUpItemAsync(ready.Result,backup,fixture.State,fixture.Token).ConfigureAwait(false));
        var next=await AdmitAsync(fixture.State,fixture.Raw,fixture.Token).ConfigureAwait(false);
        using(var raw=new MemoryStream(fixture.Raw,false))await fixture.Storage.CaptureAsync(next,raw,fixture.Token).ConfigureAwait(false);
        Assert.AreEqual(1,Directory.GetDirectories(Path.Combine(fixture.StateDirectory,"takeout-metadata-stages")).Length);
        CollectionAssert.AreEqual(fixture.Raw,await File.ReadAllBytesAsync(SourcePath(fixture.StateDirectory,next),fixture.Token).ConfigureAwait(false));
        await fixture.State.ExecuteAsync(db=>{new TakeoutMetadataJobRepository(db).ReturnJoinedOperation(next);return true;},fixture.Token).ConfigureAwait(false);
        fixture.Storage.Dispose();await fixture.State.DisposeAsync().ConfigureAwait(false);fixture.Database.Dispose();
        using var database=new ProcessorStateDatabase(fixture.StateDirectory);await using var state=new ProcessorStateCoordinator(database);using var storage=new TakeoutMetadataStageStorage(database,fixture.Budget);
        var retained=await state.ExecuteAsync(db=>new TakeoutMetadataJobRepository(db).ReadCompletedImported(fixture.Completed.ImportId,fixture.Sidecar.Key)!,fixture.Token).ConfigureAwait(false);
        Assert.AreEqual(ready.Result.JobId,retained.JobId);Assert.AreEqual(ready.Result.Artifact.Sha256,retained.Artifact.Sha256);
        CollectionAssert.AreEqual(fixture.Raw,(await storage.ReadVerifiedAndRetireReplayAsync(retained,state,fixture.Token).ConfigureAwait(false)).CopyRawSource());
        CollectionAssert.AreEqual(original,await File.ReadAllBytesAsync(Path.Combine(fixture.Completed.Parts.Single().Location,"a.jpg"),fixture.Token).ConfigureAwait(false));
        Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow("intent")]
    [DataRow("renamed")]
    [DataRow("source")]
    [DataRow("claims")]
    [DataRow("artifact")]
    [DataRow("empty")]
    [DataRow("removed")]
    [DataRow("completed-slot")]
    public async Task NativeRestartRecoversEveryPartialMetadataUnlinkFromItsFirstDurableRawIntent(string point)
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);
        var ready=await CompleteImportedObservationAsync(fixture,fixture.Sidecar,await CopyInstalledBundleAsync(fixture.Root.Path,fixture.Token).ConfigureAwait(false)).ConfigureAwait(false);
        var backup=await RetirementBackupAsync(fixture,ready.Result).ConfigureAwait(false);var paths=ReplayPaths(fixture,ready.Result);
        var capture=await fixture.State.ExecuteAsync(db=>
        {var journal=new TakeoutMetadataRetirementRepository(db,fixture.Budget);var value=journal.Capture(ready.Result,new(backup.Scope.AccountId,backup.Scope.FolderId),backup.PublicationId);journal.Prepare(value);return value;},fixture.Token).ConfigureAwait(false);
        var slot=Path.Combine(fixture.StateDirectory,"takeout-metadata-retiring");Assert.AreEqual(Path.GetFullPath(fixture.StateDirectory),Path.GetDirectoryName(Path.GetFullPath(slot)));
        if(point!="intent")Directory.Move(paths.Item,slot);
        var attempt=point=="intent"?Path.GetDirectoryName(paths.Replay)!:Path.Combine(slot,Path.GetFileName(Path.GetDirectoryName(paths.Replay)!));
        if(point=="source")File.Delete(Path.Combine(slot,"source.bin"));
        if(point=="claims"){File.Delete(Path.Combine(slot,"claim.json"));File.Delete(Path.Combine(attempt,"claim.json"));}
        if(point=="artifact")File.Delete(Path.Combine(attempt,ready.Result.Artifact.RelativePath));
        if(point is "empty" or "removed")
        {
            foreach(var file in Directory.EnumerateFiles(attempt))File.Delete(file);
            Directory.Delete(attempt,recursive:false);foreach(var file in Directory.EnumerateFiles(slot))File.Delete(file);
            if(point=="removed")Directory.Delete(slot,recursive:false);
        }
        if(point=="completed-slot")await fixture.State.ExecuteAsync(db=>{new TakeoutMetadataRetirementRepository(db,fixture.Budget).Complete(capture);return true;},fixture.Token).ConfigureAwait(false);
        _=TakeoutMetadataStageStorage.MeasureOwnedBytes(fixture.StateDirectory);fixture.Storage.Dispose();
        using var fresh=await fixture.State.ExecuteAsync(db=>new TakeoutMetadataStageStorage(db,fixture.Budget),fixture.Token).ConfigureAwait(false);
        Assert.IsFalse(Directory.Exists(paths.Item));Assert.IsFalse(Directory.Exists(slot));
        CollectionAssert.AreEqual(fixture.Raw,(await fresh.ReadVerifiedAndRetireReplayAsync(ready.Result,fixture.State,fixture.Token).ConfigureAwait(false)).CopyRawSource());
        await fixture.State.ExecuteAsync(db=>{var journal=new TakeoutMetadataRetirementRepository(db,fixture.Budget);Assert.IsNull(journal.Pending());Assert.AreEqual(capture.Body,journal.Read(ready.Result)!.Body);return true;},fixture.Token).ConfigureAwait(false);
        Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow("source")]
    [DataRow("artifact")]
    [DataRow("claim")]
    [DataRow("unknown")]
    [DataRow("pending")]
    [DataRow("diagnostics")]
    [DataRow("attempt")]
    [DataRow("outbox")]
    public async Task ExplicitMetadataCleanupPreservesAllFilesForUnknownFailedChangedOrUnbackedEvidence(string damage)
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);
        var ready=await CompleteImportedObservationAsync(fixture,fixture.Sidecar,await CopyInstalledBundleAsync(fixture.Root.Path,fixture.Token).ConfigureAwait(false)).ConfigureAwait(false);
        var backup=await RetirementBackupAsync(fixture,ready.Result).ConfigureAwait(false);var paths=ReplayPaths(fixture,ready.Result);var attempt=Path.GetDirectoryName(paths.Replay)!;
        var path=damage switch{"source"=>Path.Combine(paths.Item,"source.bin"),"artifact"=>Path.Combine(attempt,ready.Result.Artifact.RelativePath),"claim"=>Path.Combine(paths.Item,"claim.json"),"pending"=>Path.Combine(paths.Item,"source.pending"),"diagnostics"=>Path.Combine(attempt,"diagnostics.log"),_=>Path.Combine(attempt,"unknown.bin")};
        byte[]? saved=null;var existed=File.Exists(path);var extraAttempt=Path.Combine(paths.Item,"attempt-"+Guid.NewGuid().ToString("N"));
        if(damage=="attempt")Directory.CreateDirectory(extraAttempt);
        else if(damage=="outbox")await fixture.State.ExecuteAsync(db=>{using var command=db.Connection.CreateCommand();command.CommandText="UPDATE workspace_publication SET verified_version=NULL WHERE id=$id";command.Parameters.AddWithValue("$id",backup.PublicationId.ToString("D"));Assert.AreEqual(1,command.ExecuteNonQuery());return true;},fixture.Token).ConfigureAwait(false);
        else{saved=existed?await File.ReadAllBytesAsync(path,fixture.Token).ConfigureAwait(false):[];var changed=saved.ToArray();if(changed.Length==0)changed=[1];else changed[0]^=1;await File.WriteAllBytesAsync(path,changed,fixture.Token).ConfigureAwait(false);}
        var before=ReplayFiles(paths.Item);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>fixture.Storage.RetireBackedUpItemAsync(ready.Result,backup,fixture.State,fixture.Token)).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(before.Select(pair=>pair.Key+":"+pair.Value).ToArray(),ReplayFiles(paths.Item).Select(pair=>pair.Key+":"+pair.Value).ToArray());
        Assert.IsTrue(Directory.Exists(paths.Item));Assert.IsFalse(Directory.Exists(Path.Combine(fixture.StateDirectory,"takeout-metadata-retiring")));
        if(saved is not null){if(existed)await File.WriteAllBytesAsync(path,saved,fixture.Token).ConfigureAwait(false);else File.Delete(path);}
        if(damage=="attempt")Directory.Delete(extraAttempt,recursive:false);
        if(damage=="outbox")await fixture.State.ExecuteAsync(db=>{using var command=db.Connection.CreateCommand();command.CommandText="UPDATE workspace_publication SET verified_version=7 WHERE id=$id";command.Parameters.AddWithValue("$id",backup.PublicationId.ToString("D"));Assert.AreEqual(1,command.ExecuteNonQuery());return true;},fixture.Token).ConfigureAwait(false);
        Assert.IsTrue(await fixture.Storage.RetireBackedUpItemAsync(ready.Result,backup,fixture.State,fixture.Token).ConfigureAwait(false));
        CollectionAssert.AreEqual(fixture.Raw,(await fixture.Storage.ReadVerifiedAndRetireReplayAsync(ready.Result,fixture.State,fixture.Token).ConfigureAwait(false)).CopyRawSource());Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task MissingPrivateFilesWithoutNativeRetirementIntentNeverBecomeSuccessfulRecovery()
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);
        var ready=await CompleteImportedObservationAsync(fixture,fixture.Sidecar,await CopyInstalledBundleAsync(fixture.Root.Path,fixture.Token).ConfigureAwait(false)).ConfigureAwait(false);
        _=await RetirementBackupAsync(fixture,ready.Result).ConfigureAwait(false);var paths=ReplayPaths(fixture,ready.Result);File.Delete(Path.Combine(paths.Item,"source.bin"));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(()=>fixture.Storage.ReadVerifiedAndRetireReplayAsync(ready.Result,fixture.State,fixture.Token)).ConfigureAwait(false);
        Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(paths.Replay)!,ready.Result.Artifact.RelativePath)));
    }

    [TestMethod]
    public async Task FailedNativeCleanupIntentCannotDeleteFilesAndAJoinedRetryKeepsTheFirstBackupIdentity()
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);
        var ready=await CompleteImportedObservationAsync(fixture,fixture.Sidecar,await CopyInstalledBundleAsync(fixture.Root.Path,fixture.Token).ConfigureAwait(false)).ConfigureAwait(false);
        var backup=await RetirementBackupAsync(fixture,ready.Result).ConfigureAwait(false);var paths=ReplayPaths(fixture,ready.Result);var before=ReplayFiles(paths.Item);
        await fixture.State.ExecuteAsync(db=>{using var command=db.Connection.CreateCommand();command.CommandText="CREATE TRIGGER fail_metadata_retirement BEFORE INSERT ON processor_meta WHEN NEW.key LIKE 'takeout-metadata-retirement-%' BEGIN SELECT RAISE(ABORT,'synthetic intent write failure'); END";command.ExecuteNonQuery();return true;},fixture.Token).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(()=>fixture.Storage.RetireBackedUpItemAsync(ready.Result,backup,fixture.State,fixture.Token)).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(before.Select(pair=>pair.Key+":"+pair.Value).ToArray(),ReplayFiles(paths.Item).Select(pair=>pair.Key+":"+pair.Value).ToArray());
        await fixture.State.ExecuteAsync(db=>{var journal=new TakeoutMetadataRetirementRepository(db,fixture.Budget);Assert.IsNull(journal.Read(ready.Result));Assert.IsNull(journal.Pending());using var command=db.Connection.CreateCommand();command.CommandText="DROP TRIGGER fail_metadata_retirement";command.ExecuteNonQuery();return true;},fixture.Token).ConfigureAwait(false);
        Assert.IsTrue(await fixture.Storage.RetireBackedUpItemAsync(ready.Result,backup,fixture.State,fixture.Token).ConfigureAwait(false));
        Assert.AreEqual(backup,await fixture.State.ExecuteAsync(db=>new MetadataSourceBackupRepository(db,fixture.Budget).Read(ready.Result,new(backup.Scope.AccountId,backup.Scope.FolderId)),fixture.Token).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ExplicitCleanupAndCanceledQueuedReadsKeepTheirAdmissionUntilActualNativeStateWorkJoins()
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);
        var ready=await CompleteImportedObservationAsync(fixture,fixture.Sidecar,await CopyInstalledBundleAsync(fixture.Root.Path,fixture.Token).ConfigureAwait(false)).ConfigureAwait(false);
        var backup=await RetirementBackupAsync(fixture,ready.Result).ConfigureAwait(false);using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held=fixture.State.ExecuteAsync(_=>{entered.TrySetResult();release.Wait(fixture.Token);return true;},fixture.Token);Task<bool>? cleanup=null;
        try
        {
            await entered.Task.WaitAsync(fixture.Token).ConfigureAwait(false);cleanup=fixture.Storage.RetireBackedUpItemAsync(ready.Result,backup,fixture.State,fixture.Token);Assert.IsFalse(cleanup.IsCompleted);
            using var canceled=CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);var queued=fixture.Storage.ReadVerifiedAsync(ready.Result,canceled.Token);
            Assert.IsFalse(queued.IsCompleted);Assert.ThrowsExactly<InvalidOperationException>(()=>fixture.Storage.Dispose());await canceled.CancelAsync().ConfigureAwait(false);
            await Assert.ThrowsAsync<OperationCanceledException>(()=>queued).ConfigureAwait(false);Assert.ThrowsExactly<InvalidOperationException>(()=>fixture.Storage.Dispose());
        }
        finally{release.Set();await held.ConfigureAwait(false);if(cleanup is not null)Assert.IsTrue(await cleanup.ConfigureAwait(false));}
        CollectionAssert.AreEqual(fixture.Raw,(await fixture.Storage.ReadVerifiedAndRetireReplayAsync(ready.Result,fixture.State,fixture.Token).ConfigureAwait(false)).CopyRawSource());Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow("unknown")]
    [DataRow("changed-intent")]
    [DataRow("missing-item-intent")]
    [DataRow("missing-ready-outbox")]
    [DataRow("unmarked")]
    public async Task RestartCannotAdoptOrDeleteUnexplainedRetirementEvidence(string damage)
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);
        var ready=await CompleteImportedObservationAsync(fixture,fixture.Sidecar,await CopyInstalledBundleAsync(fixture.Root.Path,fixture.Token).ConfigureAwait(false)).ConfigureAwait(false);
        var backup=await RetirementBackupAsync(fixture,ready.Result).ConfigureAwait(false);var paths=ReplayPaths(fixture,ready.Result);var slot=Path.Combine(fixture.StateDirectory,"takeout-metadata-retiring");
        if(damage!="unmarked")await fixture.State.ExecuteAsync(db=>{var journal=new TakeoutMetadataRetirementRepository(db,fixture.Budget);journal.Prepare(journal.Capture(ready.Result,new(backup.Scope.AccountId,backup.Scope.FolderId),backup.PublicationId));return true;},fixture.Token).ConfigureAwait(false);
        Directory.Move(paths.Item,slot);
        if(damage=="unknown")await File.WriteAllBytesAsync(Path.Combine(slot,"unknown.bin"),new byte[]{42},fixture.Token).ConfigureAwait(false);
        if(damage is "changed-intent" or "missing-item-intent" or "missing-ready-outbox")await fixture.State.ExecuteAsync(db=>
        {
            using var command=db.Connection.CreateCommand();command.CommandText=damage switch
            {"changed-intent"=>"UPDATE processor_meta SET value=json_set(value,'$.sidecarKey','changed') WHERE key='takeout-metadata-retirement-active-v1'","missing-item-intent"=>"DELETE FROM processor_meta WHERE key LIKE 'takeout-metadata-retirement-v1:%'",_=>"DELETE FROM state_outbox WHERE job_id=$job"};
            command.Parameters.AddWithValue("$job",ready.Result.JobId.ToString("D"));Assert.AreEqual(1,command.ExecuteNonQuery());return true;
        },fixture.Token).ConfigureAwait(false);
        var before=ReplayFiles(slot);fixture.Storage.Dispose();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>fixture.State.ExecuteAsync(db=>new TakeoutMetadataStageStorage(db,fixture.Budget),fixture.Token)).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(before.Select(pair=>pair.Key+":"+pair.Value).ToArray(),ReplayFiles(slot).Select(pair=>pair.Key+":"+pair.Value).ToArray());Assert.IsTrue(Directory.Exists(slot));
    }

    [TestMethod]
    public async Task PendingCleanupBytesStayAccountedWhenThePrivateStageRootIsLostAndRecoveryRevalidatesTheSameIntent()
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);
        var ready=await CompleteImportedObservationAsync(fixture,fixture.Sidecar,await CopyInstalledBundleAsync(fixture.Root.Path,fixture.Token).ConfigureAwait(false)).ConfigureAwait(false);
        var backup=await RetirementBackupAsync(fixture,ready.Result).ConfigureAwait(false);var paths=ReplayPaths(fixture,ready.Result);var slot=Path.Combine(fixture.StateDirectory,"takeout-metadata-retiring");
        await fixture.State.ExecuteAsync(db=>{var journal=new TakeoutMetadataRetirementRepository(db,fixture.Budget);journal.Prepare(journal.Capture(ready.Result,new(backup.Scope.AccountId,backup.Scope.FolderId),backup.PublicationId));return true;},fixture.Token).ConfigureAwait(false);
        Directory.Move(paths.Item,slot);fixture.Storage.Dispose();var root=Path.Combine(fixture.StateDirectory,"takeout-metadata-stages");var lost=Path.Combine(fixture.StateDirectory,"lost-stage-root-test");
        Assert.AreEqual(Path.GetFullPath(fixture.StateDirectory),Path.GetDirectoryName(Path.GetFullPath(lost)));Directory.Move(root,lost);
        var expected=Directory.EnumerateFiles(slot,"*",SearchOption.AllDirectories).Sum(path=>new FileInfo(path).Length);Assert.AreEqual(expected,TakeoutMetadataStageStorage.MeasureOwnedBytes(fixture.StateDirectory));Assert.IsGreaterThan(0L,expected);
        using var fresh=await fixture.State.ExecuteAsync(db=>new TakeoutMetadataStageStorage(db,fixture.Budget),fixture.Token).ConfigureAwait(false);
        Assert.IsFalse(Directory.Exists(slot));CollectionAssert.AreEqual(fixture.Raw,(await fresh.ReadVerifiedAndRetireReplayAsync(ready.Result,fixture.State,fixture.Token).ConfigureAwait(false)).CopyRawSource());
    }

    [TestMethod]
    public async Task SequentialCleanupAdvancesOnlyTheLastJoinedPointerAndKeepsEveryFirstRawBindingRecoverable()
    {
        await using var fixture=await ImportedFixture.CreateAsync(conflictingSidecar:true).ConfigureAwait(false);var bundle=await CopyInstalledBundleAsync(fixture.Root.Path,fixture.Token).ConfigureAwait(false);
        var first=await CompleteImportedObservationAsync(fixture,fixture.Sidecar,bundle).ConfigureAwait(false);
        var other=fixture.Catalog.ReadPage(TakeoutEntryKind.JsonSidecar).Entries.Single(entry=>entry.Key!=fixture.Sidecar.Key);
        var second=await CompleteImportedObservationAsync(fixture,other,bundle).ConfigureAwait(false);
        var firstBackup=await RetirementBackupAsync(fixture,first.Result,second).ConfigureAwait(false);
        var secondBackup=(await fixture.State.ExecuteAsync(db=>new MetadataSourceBackupRepository(db,fixture.Budget).Read(second.Result,new(firstBackup.Scope.AccountId,firstBackup.Scope.FolderId)),fixture.Token).ConfigureAwait(false))!;
        Assert.IsTrue(await fixture.Storage.RetireBackedUpItemAsync(first.Result,firstBackup,fixture.State,fixture.Token).ConfigureAwait(false));
        Assert.IsTrue(await fixture.Storage.RetireBackedUpItemAsync(second.Result,secondBackup,fixture.State,fixture.Token).ConfigureAwait(false));
        Assert.AreEqual(0,Directory.GetDirectories(Path.Combine(fixture.StateDirectory,"takeout-metadata-stages")).Length);
        CollectionAssert.AreEqual(first.Document.CopyRawSource(),(await fixture.Storage.ReadVerifiedAndRetireReplayAsync(first.Result,fixture.State,fixture.Token).ConfigureAwait(false)).CopyRawSource());
        CollectionAssert.AreEqual(second.Document.CopyRawSource(),(await fixture.Storage.ReadVerifiedAndRetireReplayAsync(second.Result,fixture.State,fixture.Token).ConfigureAwait(false)).CopyRawSource());
        await fixture.State.ExecuteAsync(db=>{var journal=new TakeoutMetadataRetirementRepository(db,fixture.Budget);Assert.IsNull(journal.Pending());Assert.AreEqual(second.Result.JobId,journal.Last()!.Ready.JobId);Assert.AreEqual(firstBackup,journal.Read(first.Result)!.Backup);return true;},fixture.Token).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task UnretiredMetadataReadsKeepTheirSmallWorkingSpaceBoundWithoutReservingARawBundle()
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);
        var ready=await CompleteImportedObservationAsync(fixture,fixture.Sidecar,await CopyInstalledBundleAsync(fixture.Root.Path,fixture.Token).ConfigureAwait(false)).ConfigureAwait(false);fixture.Storage.Dispose();
        var reader=new MeasuredSpace(fixture.StateDirectory);var budget=new StorageBudgetManager(reader);using var fresh=new TakeoutMetadataStageStorage(fixture.Database,budget);
        reader.AvailableBytes=reader.Read().CapacityBytes/20+1024*1024;Assert.AreEqual(1024L*1024,budget.Status().ReservableBytes);
        CollectionAssert.AreEqual(fixture.Raw,(await fresh.ReadVerifiedAndRetireReplayAsync(ready.Result,fixture.State,fixture.Token).ConfigureAwait(false)).CopyRawSource());
        Assert.AreEqual(0L,budget.Status().ReservedBytes);
    }

    private static Task<MetadataSourceBackupReceipt> RetirementBackupAsync(ImportedFixture fixture,TakeoutVerifiedMetadata ready,(TakeoutVerifiedMetadata Result,TakeoutMetadataDocument Document)? additional=null)=>fixture.State.ExecuteAsync(db=>
    {
        // Unit-level simulated transport receipt; the separate desktop integration
        // exercises actual current upload/readback before explicit cleanup.
        using(var identity=db.Connection.CreateCommand())
        {identity.CommandText="INSERT INTO processor_meta(key,value) VALUES('library_id',$id)";identity.Parameters.AddWithValue("$id",fixture.Completed.Target.LibraryId.ToString("D"));identity.ExecuteNonQuery();}
        var accounts=new DriveAccountRepository(db);var account=accounts.Begin(Guid.NewGuid());accounts.Promote(account.Id,"account-one","Synthetic",null);
        var scope=new WorkspacePublicationScope(fixture.Completed.Target.LibraryId,Guid.NewGuid(),1,Guid.NewGuid(),"account-one",1,"workspace-one",1,new string('a',64));
        var sources=new List<MetadataSourceBytes>{MetadataSourceBytes.Capture(fixture.Raw)};var results=new List<TakeoutVerifiedMetadata>{ready};
        if(additional is{} other){sources.Add(MetadataSourceBytes.Capture(other.Document.CopyRawSource()));results.Add(other.Result);}
        var bundle=MetadataSourceBundle.Capture(sources);var payload=CatalogDependencyPayload.Capture(MetadataSourceBundle.Role,MetadataSourceBundle.MimeType,bundle.CopyPayload());var id=payload.IdFor(scope);
        var outbox=new WorkspacePublicationRepository(db,fixture.Budget);var row=outbox.Prepare(id,scope,MetadataSourceBundle.Role,MetadataSourceBundle.MimeType,bundle.CopyPayload());outbox.AssignFileId(id,row.Sha256,"metadata-source-file");outbox.ConfirmVerified(id,"account-one","workspace-one","metadata-source-file",row.Sha256,row.Payload.Length,7);
        return new MetadataSourceBackupRepository(db,fixture.Budget).RecordBundle(id,results).Single(row=>row.JobId==ready.JobId);
    },fixture.Token);
}
