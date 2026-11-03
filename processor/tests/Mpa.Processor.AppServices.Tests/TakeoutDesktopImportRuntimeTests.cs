using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public Task DesktopFolderImportReviewsActualInputBeforeExplicitStartAndBrowserCancellationDoesNotOwnTheCopy()=>VerifyImportedPhotoPublicationAsync(legacyFinished:false);
    [TestMethod]
    public Task DesktopImportUpgradesOldFinishedMetadataFromPrivateEvidenceWithoutAnotherUploadOrWorker()=>VerifyImportedPhotoPublicationAsync(legacyFinished:true);
    [TestMethod]
    public Task DesktopExplicitMetadataSelectionSurvivesBrowserClosureAndPublishesOnlyTheChosenVerifiedOriginal()=>VerifyImportedPhotoPublicationAsync(legacyFinished:false,manualAssociation:true);
    [TestMethod]
    public Task DesktopImportBacksUpExactRawSidecarThroughCurrentPublicationBeforeAnyPrivateRetirement()=>VerifyImportedPhotoPublicationAsync(legacyFinished:false,rawBackup:true);
    [TestMethod]
    public Task DesktopRawMetadataRecoveryUsesSignedCatalogAndActualDriveWithoutPrivateInputsJobsOrLocators()=>VerifyImportedPhotoPublicationAsync(legacyFinished:false,rawBackup:true,recover:true);
    [TestMethod]
    public Task DesktopExplicitMetadataCleanupUsesActualRawBackupAndRetainsRetryWithoutAnotherOriginalUploadOrWorker()=>VerifyImportedPhotoPublicationAsync(legacyFinished:false,rawBackup:true,cleanup:true);
    private static async Task VerifyImportedPhotoPublicationAsync(bool legacyFinished,bool manualAssociation=false,bool rawBackup=false,bool recover=false,bool cleanup=false)
    {
        var started=Stopwatch.GetTimestamp();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        void Measure(string phase)=>Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{importPublicationPhase=phase,elapsedMilliseconds=Stopwatch.GetElapsedTime(started).TotalMilliseconds,conditionalMetadataReads=fixture.Http.MetadataReads}));
        Measure("fixture-ready");
        var selection=await NativeTakeoutSelectionAsync(fixture,deadline.Token,metadata:true).ConfigureAwait(false);var state=fixture.Data.Setup.Accounts.State;
        if(manualAssociation)
        {
            var raw=System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllBytesAsync(selection.Path+".json",deadline.Token).ConfigureAwait(false))!;raw["title"]="Different export title.jpg";
            await File.WriteAllBytesAsync(selection.Path+".json",System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(raw),deadline.Token).ConfigureAwait(false);
        }
        var setup=await state.ExecuteAsync(db=>new{db.StateDirectory,db.InstallationId,Source=new LibrarySourceRepository(db).Snapshot().Sources.Single().Id},deadline.Token).ConfigureAwait(false);
        var originalAllocator=fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate;var cloud=new OriginalCloud(selection.Payload);WireOriginal(fixture.Data.Setup.Accounts,cloud);
        using var metadataStorage=await state.ExecuteAsync(db=>new TakeoutMetadataStageStorage(db,fixture.Data.Budget),deadline.Token).ConfigureAwait(false);
        await using var current=new CurrentLibraryCatalogRuntime(state,setup.StateDirectory,setup.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget,rawBackup?metadataStorage:null);
        await using var inventory=new TakeoutInventoryService(state,fixture.Data.Budget);
        await using var native=new ProcessorWorkSupervisor();var metadataLaunches=0;
        var metadata=new TakeoutImportMetadataRunner(state,fixture.Data.Budget,metadataStorage,native,token=>
        {metadataLaunches++;return TakeoutWorkerInstallation.OpenInstalledAsync(Path.Combine(AppContext.BaseDirectory,"workers","takeout-metadata"),token);});
        var scanQueued=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var scanNotifications=0;var metadataNotifications=0;
        await using var imports=new TakeoutDesktopImportRuntime(state,fixture.Data.Budget,inventory,current,_=>Task.FromResult<string?>(Path.GetDirectoryName(selection.Path)),()=>{Interlocked.Increment(ref scanNotifications);scanQueued.TrySetResult();},metadata,()=>Interlocked.Increment(ref metadataNotifications));
        await imports.ResumeAsync().ConfigureAwait(false);var review=await imports.ChooseFolderAsync(setup.Source,deadline.Token).ConfigureAwait(false);Assert.IsNotNull(review);
        var ready=await WaitImportAsync(imports,review.Id,"ready",deadline.Token).ConfigureAwait(false);Assert.AreEqual(1L,ready.Media);Assert.IsFalse(ready.Requested);Assert.AreEqual(0,cloud.Allocations);
        Measure("input-reviewed");
        Assert.IsFalse(ready.InputName.Contains(Path.DirectorySeparatorChar));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>state.ExecuteAsync(db=>new SourceDiscoveryRepository(db).EnsureImportedOriginalsScheduled(review.Id),deadline.Token)).ConfigureAwait(false);
        using var browser=new CancellationTokenSource();var admitted=await imports.StartAsync(review.Id,browser.Token).ConfigureAwait(false);Assert.IsTrue(admitted.Requested);await browser.CancelAsync().ConfigureAwait(false);
        var verified=await WaitImportAsync(imports,review.Id,"originals-verified",deadline.Token).ConfigureAwait(false);Assert.AreEqual(1L,verified.Verified);Assert.AreEqual(1,cloud.Allocations);
        Measure("original-verified");
        CollectionAssert.AreEqual(selection.Payload,cloud.Uploaded);CollectionAssert.AreEqual(selection.Payload,await File.ReadAllBytesAsync(selection.Path,deadline.Token).ConfigureAwait(false));
        await scanQueued.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        var processed=await WaitImportMetadataAsync(imports,review.Id,deadline.Token).ConfigureAwait(false);
        if(manualAssociation)
        {
            Assert.AreEqual(new TakeoutMetadataProgress(1,0,1,0,true),processed.Metadata);Assert.AreEqual(0,metadataLaunches);Assert.AreEqual(0,metadataNotifications);
            await imports.PauseAsync().ConfigureAwait(false);
            var issue=(await imports.MetadataIssuesAsync(review.Id,null,deadline.Token).ConfigureAwait(false)).Issues.Single();Assert.AreEqual("name-mismatch",issue.Code);
            var choices=await imports.OriginalChoicesAsync(review.Id,null,deadline.Token).ConfigureAwait(false);Assert.AreEqual(1,choices.Originals.Count);var chosen=choices.Originals.Single();Assert.AreEqual("first-original-id",chosen.FileId);
            using var selectingBrowser=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            await imports.SelectMetadataOriginalAsync(review.Id,issue.Key,chosen.Key,selectingBrowser.Token).ConfigureAwait(false);await selectingBrowser.CancelAsync().ConfigureAwait(false);
            Assert.IsFalse(await state.ExecuteAsync(db=>new TakeoutImportedPhotoFactsRepository(db).HasObservedImport(review.Id),deadline.Token).ConfigureAwait(false));
            await imports.ResumeAsync().ConfigureAwait(false);
            while(Volatile.Read(ref metadataNotifications)==0)
            {if(imports.Failure is{}failure)throw new InvalidOperationException("Explicit metadata selection faulted.",failure);await Task.Delay(10,deadline.Token).ConfigureAwait(false);}
            processed=await WaitImportMetadataAsync(imports,review.Id,deadline.Token).ConfigureAwait(false);
            Assert.AreEqual(0,(await imports.MetadataIssuesAsync(review.Id,null,deadline.Token).ConfigureAwait(false)).Issues.Count);
            await imports.SelectMetadataOriginalAsync(review.Id,issue.Key,chosen.Key,deadline.Token).ConfigureAwait(false);
        }
        Assert.AreEqual(new TakeoutMetadataProgress(1,1,0,0,true),processed.Metadata);Assert.AreEqual(1,metadataLaunches);
        Measure("metadata-ready");
        var expectedMetadata=TakeoutMetadataDocument.ParseSource(await File.ReadAllBytesAsync(selection.Path+".json",deadline.Token).ConfigureAwait(false));
        var metadataArea=Path.Combine(setup.StateDirectory,"takeout-metadata-stages");
        Assert.AreEqual(0,Directory.EnumerateFiles(metadataArea,"input.bin",SearchOption.AllDirectories).Count(),"Only joined duplicate worker inputs should retire automatically.");
        var retainedRaw=Directory.EnumerateFiles(metadataArea,"source.bin",SearchOption.AllDirectories).Single();
        CollectionAssert.AreEqual(expectedMetadata.CopyRawSource(),await File.ReadAllBytesAsync(retainedRaw,deadline.Token).ConfigureAwait(false));
        File.Delete(selection.Path+".json");await imports.PauseAsync().ConfigureAwait(false);
        if(legacyFinished)
        {
            Assert.AreEqual(1,metadataNotifications);
            await state.ExecuteAsync(db=>
            {
                // Reproduce pre-profile local state: finished processing and real
                // ready/raw evidence, with no new projection rows or version hint.
                using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(db.StateDirectory,"processor.db"),Mode=SqliteOpenMode.ReadWrite,Pooling=false,DefaultTimeout=5}.ToString());connection.Open();
                using var remove=connection.CreateCommand();remove.CommandText="DELETE FROM processor_meta WHERE key LIKE 'takeout-photo-observation-v1:%' OR key=$marker";remove.Parameters.AddWithValue("$marker","takeout-photo-import-observed-v1:"+review.Id.ToString("D"));Assert.AreEqual(2,remove.ExecuteNonQuery());
                Assert.IsTrue(new TakeoutImportInventoryRepository(db).Activity(review.Id).Metadata!.Finished);return true;
            },deadline.Token).ConfigureAwait(false);
            await imports.ResumeAsync().ConfigureAwait(false);
            while(Volatile.Read(ref metadataNotifications)<2)
            {if(imports.Failure is {}failure)throw new InvalidOperationException("Legacy metadata upgrade faulted.",failure);await Task.Delay(10,deadline.Token).ConfigureAwait(false);}
            await imports.PauseAsync().ConfigureAwait(false);Assert.AreEqual(1,cloud.Allocations);Assert.AreEqual(1,metadataLaunches);
        }
        var scan=await state.ExecuteAsync(db=>
        {
            var repository=new SourceDiscoveryRepository(db);var retained=repository.EnsureImportedOriginalsScheduled(review.Id);Assert.IsFalse(retained.Created);
            var activity=repository.ReadActivity().Jobs.Single(value=>value.JobId==retained.JobId);
            Assert.AreEqual(review.Target.AccountId,activity.Scope.AccountId);Assert.AreEqual(review.Target.FolderId,activity.Scope.RootFolderId);Assert.AreEqual(setup.Source,activity.Scope.SourceId);
            Assert.AreEqual(1,new DurableJobRepository(db).Summary(retained.JobId).Queued);return retained;
        },deadline.Token).ConfigureAwait(false);
        var sourceReads=0;var originalFolderReader=fixture.Data.Setup.Accounts.Readers.FolderRead;fixture.Data.Setup.Accounts.Readers.FolderRead=(folder,token)=>
        {token.ThrowIfCancellationRequested();Assert.AreEqual(review.Target.FolderId,folder);return Task.FromResult(new DriveFolder(folder,"Imported photos",true,[],null));};
        fixture.Data.Setup.Accounts.Readers.SourceRead=(folder,_,cursor,token)=>
        {
            token.ThrowIfCancellationRequested();Assert.AreEqual(review.Target.FolderId,folder);Assert.IsNull(cursor);sourceReads++;
            return Task.FromResult(new DriveSourcePage([new("first-original-id",7,"Human renamed.jpg","image/jpeg",selection.Payload.Length,null,
                new("first-original-id","Human renamed.jpg","image/jpeg",400,300,"2026:10:20 15:30:00","2026-10-20T15:30:00Z"))],[],null,false));
        };
        await using(var discovery=new SourceDiscoveryRunner(state,fixture.Data.Setup.Accounts.Service,fixture.Data.Budget))
        {
            var steps=0;while(await discovery.RunNextAsync(deadline.Token).ConfigureAwait(false) is not null)Assert.IsTrue(++steps<=4,"A single imported source must have bounded verification/listing work.");
            Assert.IsNull(discovery.Failure);
        }
        var discovered=await state.ExecuteAsync(db=>new DiscoveredCatalogRepository(db).ReadPage(),deadline.Token).ConfigureAwait(false);
        var imported=discovered.Assets.Single(value=>value.Facts.FileId=="first-original-id");Assert.AreEqual("Human renamed.jpg",imported.Facts.Name);
        Assert.AreEqual(new DriveAssetIdentity(review.Target.LibraryId).Derive(review.Target.AccountId,"first-original-id"),imported.Id);Assert.AreEqual(1,sourceReads);
        fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate=originalAllocator;
        fixture.Data.Setup.Accounts.Readers.FolderRead=originalFolderReader;
        ConfigureCurrentPublication(fixture.Data);InstallCurrentInventoryTransport(fixture);InstallImportedCatalogFixture(fixture);
        Measure("catalog-publication-start");
        var published=await current.RunUntilCurrentAsync(deadline.Token).ConfigureAwait(false);
        Measure("catalog-published");
        var activated=current.Catalog.Selection;Assert.IsNotNull(activated);Assert.AreEqual(published.Commit,activated.Catalog);
        var page=await current.Catalog.ReadPageAsync(activated.StageId,null,60,deadline.Token).ConfigureAwait(false);Assert.IsNotNull(page);
        var portable=page.Page.Assets.Single(value=>value.Id==imported.Id);Assert.AreEqual("Human renamed.jpg",portable.Name);
        Assert.IsNotNull(portable.ImportedMetadata);Assert.AreEqual(expectedMetadata.SourceSha256,portable.ImportedMetadata.Source.Sha256);Assert.AreEqual(expectedMetadata.Description,portable.ImportedMetadata.Description);
        Assert.AreEqual(expectedMetadata.Title,portable.ImportedMetadata.Title);Assert.AreEqual(expectedMetadata.CapturedUtc,portable.ImportedMetadata.CapturedUtc);
        Assert.AreEqual(imported.Facts.CapturedUtc,portable.CapturedUtc);
        Assert.AreEqual(imported.Facts.CapturedUtc??expectedMetadata.CapturedUtc??imported.Facts.ModifiedUtc,portable.EffectiveUtc,"The imported capture is used only when Drive capture is absent.");
        CollectionAssert.AreEqual(expectedMetadata.ImportedNames.ToArray(),portable.ImportedMetadata.ImportedNames.ToArray());
        if(rawBackup)
        {
            var backup=await state.ExecuteAsync(db=>
            {
                var actual=new TakeoutMetadataJobRepository(db).ReadCompletedImported(review.Id,portable.ImportedMetadata.SidecarKey)!;
                var uploads=new WorkspacePublicationRepository(db,fixture.Data.Budget);
                var raw=uploads.Identities().Select(id=>uploads.Read(id)!).Single(row=>row.Role==MetadataSourceBundle.Role);
                var workspace=new CatalogWorkspace(raw.Scope.AccountId,raw.Scope.FolderId);var repository=new MetadataSourceBackupRepository(db,fixture.Data.Budget);
                var saved=repository.Read(actual,workspace);Assert.IsNotNull(saved);
                Assert.AreEqual(saved,repository.RecordBundle(raw.Id,[actual]).Single());
                Assert.ThrowsExactly<InvalidDataException>(()=>repository.RecordBundle(Guid.NewGuid(),[actual]));
                Assert.ThrowsExactly<ArgumentException>(()=>repository.RecordBundle(raw.Id,[actual,actual]));
                // Corruption and a failed native journal write cannot fabricate a
                // backup receipt. These edits affect this disposable fixture only.
                using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(db.StateDirectory,"processor.db"),Mode=SqliteOpenMode.ReadWrite,Pooling=false,DefaultTimeout=5}.ToString());connection.Open();
                using var row=connection.CreateCommand();row.CommandText="SELECT key,value FROM processor_meta WHERE key LIKE 'metadata-source-backup-v1:%'";
                string key,body;using(var record=row.ExecuteReader()){Assert.IsTrue(record.Read());key=record.GetString(0);body=record.GetString(1);Assert.IsFalse(record.Read());}
                using var edit=connection.CreateCommand();edit.CommandText="UPDATE processor_meta SET value=json_set(value,'$.sourceSha256',$hash) WHERE key=$key";edit.Parameters.AddWithValue("$key",key);edit.Parameters.AddWithValue("$hash",new string('b',64));Assert.AreEqual(1,edit.ExecuteNonQuery());
                Assert.ThrowsExactly<InvalidDataException>(()=>repository.Read(actual,workspace));Assert.ThrowsExactly<InvalidDataException>(()=>repository.RecordBundle(raw.Id,[actual]));
                edit.CommandText="DELETE FROM processor_meta WHERE key=$key";Assert.AreEqual(1,edit.ExecuteNonQuery());
                edit.CommandText="CREATE TRIGGER fail_raw_backup BEFORE INSERT ON processor_meta WHEN NEW.key LIKE 'metadata-source-backup-v1:%' BEGIN SELECT RAISE(ABORT,'synthetic backup write failure'); END";edit.ExecuteNonQuery();
                Assert.ThrowsExactly<SqliteException>(()=>repository.RecordBundle(raw.Id,[actual]));Assert.IsNull(repository.Read(actual,workspace));
                edit.CommandText="DROP TRIGGER fail_raw_backup";edit.ExecuteNonQuery();Assert.AreEqual(saved,repository.RecordBundle(raw.Id,[actual]).Single());
                row.CommandText="SELECT value FROM processor_meta WHERE key=$key";row.Parameters.AddWithValue("$key",key);Assert.AreEqual(body,(string)row.ExecuteScalar()!);
                return saved;
            },deadline.Token).ConfigureAwait(false);
            Assert.IsNotNull(backup);Assert.AreEqual(portable.ImportedMetadata.JobId,backup.JobId);Assert.AreEqual(7L,backup.VerifiedVersion);
            var remote=fixture.Data.Cloud[backup.Bundle.FileId];Assert.AreEqual(MetadataSourceBundle.MimeType,remote.MimeType);
            CollectionAssert.AreEqual(expectedMetadata.CopyRawSource(),MetadataSourceBundle.Read(remote.Bytes.Span).ReadSource(backup.Source.Sha256));
            CollectionAssert.AreEqual(expectedMetadata.CopyRawSource(),await File.ReadAllBytesAsync(retainedRaw,deadline.Token).ConfigureAwait(false));
            Assert.AreEqual(1,fixture.Data.MetadataMimeTypes.Values.Count(mime=>mime==MetadataSourceBundle.MimeType));
        }
        var replay=await imports.StartAsync(review.Id,deadline.Token).ConfigureAwait(false);Assert.AreEqual("originals-verified",replay.State);Assert.AreEqual(1,cloud.Allocations);
        if(cleanup)
        {
            using var browserCleanup=new CancellationTokenSource();var operation=imports.CleanupMetadataAsync(review.Id,browserCleanup.Token);await browserCleanup.CancelAsync().ConfigureAwait(false);
            Assert.AreEqual(new TakeoutMetadataCleanupResult(1,0),await operation.ConfigureAwait(false));
            Assert.AreEqual(0,Directory.GetDirectories(metadataArea).Length);Assert.IsFalse(File.Exists(retainedRaw));
            Assert.AreEqual(new TakeoutMetadataCleanupResult(0,0),await imports.CleanupMetadataAsync(review.Id,deadline.Token).ConfigureAwait(false));
            var retained=await state.ExecuteAsync(db=>new TakeoutMetadataJobRepository(db).ReadCompletedImported(review.Id,portable.ImportedMetadata!.SidecarKey)!,deadline.Token).ConfigureAwait(false);
            CollectionAssert.AreEqual(expectedMetadata.CopyRawSource(),(await metadataStorage.ReadVerifiedAndRetireReplayAsync(retained,state,deadline.Token).ConfigureAwait(false)).CopyRawSource());
            var completed=await state.ExecuteAsync(db=>new TakeoutImportInventoryRepository(db).ReadCompleted(review.Id)!,deadline.Token).ConfigureAwait(false);
            var retried=await metadata.RunAsync(completed,deadline.Token).ConfigureAwait(false);Assert.AreEqual(1L,retried.Retained);Assert.IsFalse(retried.ObservationsChanged);
            Assert.AreEqual(1,metadataLaunches);Assert.AreEqual(1,cloud.Allocations);CollectionAssert.AreEqual(selection.Payload,await File.ReadAllBytesAsync(selection.Path,deadline.Token).ConfigureAwait(false));
        }
        await imports.ResumeAsync().ConfigureAwait(false);await imports.PauseAsync().ConfigureAwait(false);Assert.AreEqual(1,scanNotifications);Assert.AreEqual(1,metadataLaunches);Assert.AreEqual(legacyFinished?2:1,metadataNotifications);
        if(recover)
        {
            await current.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(current.ExitConfirmed);metadataStorage.Dispose();
            // Simulate loss of this disposable fixture's private metadata area,
            // jobs and locators. Product recovery performs no such deletion.
            var lost=Path.Combine(setup.StateDirectory,"lost-metadata-test-inputs");
            Assert.AreEqual(Path.TrimEndingDirectorySeparator(Path.GetFullPath(setup.StateDirectory)),Path.GetDirectoryName(Path.GetFullPath(metadataArea)));
            Assert.AreEqual(Path.TrimEndingDirectorySeparator(Path.GetFullPath(setup.StateDirectory)),Path.GetDirectoryName(Path.GetFullPath(lost)));Directory.Move(metadataArea,lost);
            await state.ExecuteAsync(db=>
            {
                using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(db.StateDirectory,"processor.db"),Mode=SqliteOpenMode.ReadWrite,Pooling=false,ForeignKeys=true,DefaultTimeout=5}.ToString());connection.Open();
                using var transaction=connection.BeginTransaction();using var remove=connection.CreateCommand();remove.Transaction=transaction;
                remove.CommandText="""
                    DELETE FROM state_outbox WHERE job_id=$job;
                    DELETE FROM stage_dependency WHERE job_id=$job;
                    DELETE FROM item_stage WHERE job_id=$job;
                    DELETE FROM job_item WHERE job_id=$job;
                    DELETE FROM job WHERE id=$job;
                    DELETE FROM processor_meta WHERE key LIKE 'metadata-source-backup-v1:%' OR key LIKE 'metadata-source-hint-v1:%' OR key LIKE 'takeout-photo-observation-v1:%';
                    """;
                remove.Parameters.AddWithValue("$job",portable.ImportedMetadata!.JobId.ToString("D"));remove.ExecuteNonQuery();transaction.Commit();
                Assert.IsNull(new TakeoutMetadataJobRepository(db).ReadCompletedImported(review.Id,portable.ImportedMetadata.SidecarKey));return true;
            },deadline.Token).ConfigureAwait(false);
            var allocated=fixture.Data.Allocations;var created=fixture.Data.Creates;
            await using var recovery=new CurrentLibraryCatalogRuntime(state,setup.StateDirectory,setup.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
            Assert.AreEqual(activated,await recovery.InitializeHistoricalAsync(deadline.Token).ConfigureAwait(false));
            var raw=await recovery.ReadImportedMetadataSourceAsync(activated.StageId,portable.Id,new(portable.AccountId),deadline.Token).ConfigureAwait(false);
            Assert.IsNotNull(raw);Assert.AreEqual(portable.Id,raw.AssetId);Assert.AreEqual(expectedMetadata.SourceSha256,raw.Sha256);CollectionAssert.AreEqual(expectedMetadata.CopyRawSource(),raw.Bytes);
            Assert.IsFalse(Directory.Exists(metadataArea));Assert.AreEqual(allocated,fixture.Data.Allocations);Assert.AreEqual(created,fixture.Data.Creates);Assert.AreEqual(1,cloud.Allocations);Assert.AreEqual(1,metadataLaunches);
            CollectionAssert.AreEqual(selection.Payload,await File.ReadAllBytesAsync(selection.Path,deadline.Token).ConfigureAwait(false));
            Measure("raw-source-recovered");
        }
        var before=await state.ExecuteAsync(db=>new SourceDiscoveryRepository(db).ReadActivity().Jobs.Single(value=>value.JobId==scan.JobId).Scope,deadline.Token).ConfigureAwait(false);
        await state.ExecuteAsync(db=>
        {
            var sources=new LibrarySourceRepository(db);var snapshot=sources.Snapshot();var selected=snapshot.Sources.Single();sources.Update(selected.Id,false,selected.IncludeDescendants,selected.Revision,snapshot.Revision);
            var retained=new SourceDiscoveryRepository(db).EnsureImportedOriginalsScheduled(review.Id);Assert.AreEqual(scan.JobId,retained.JobId);Assert.IsFalse(retained.Created);
            Assert.AreEqual(before,new SourceDiscoveryRepository(db).ReadActivity().Jobs.Single(value=>value.JobId==scan.JobId).Scope);return true;
        },deadline.Token).ConfigureAwait(false);
        Measure("assertions-complete");
    }

    [TestMethod]
    public async Task DesktopFolderImportCannotRetargetDestinationWhileTheActualNativeChooserIsPending()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selection=await NativeTakeoutSelectionAsync(fixture,deadline.Token).ConfigureAwait(false);var state=fixture.Data.Setup.Accounts.State;
        var setup=await state.ExecuteAsync(db=>new{db.StateDirectory,db.InstallationId,Source=new LibrarySourceRepository(db).Snapshot().Sources.Single()},deadline.Token).ConfigureAwait(false);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var current=new CurrentLibraryCatalogRuntime(state,setup.StateDirectory,setup.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        await using var inventory=new TakeoutInventoryService(state,fixture.Data.Budget);
        await using var imports=new TakeoutDesktopImportRuntime(state,fixture.Data.Budget,inventory,current,async token=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);token.ThrowIfCancellationRequested();return Path.GetDirectoryName(selection.Path);});
        var count=(await imports.ListAsync(deadline.Token).ConfigureAwait(false)).Count;
        var choosing=imports.ChooseFolderAsync(setup.Source.Id,deadline.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            await state.ExecuteAsync(db=>{var sources=new LibrarySourceRepository(db);var snapshot=sources.Snapshot();sources.Update(setup.Source.Id,false,setup.Source.IncludeDescendants,setup.Source.Revision,snapshot.Revision);return true;},deadline.Token).ConfigureAwait(false);
        }
        finally {release.TrySetResult();}
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>choosing).ConfigureAwait(false);Assert.AreEqual(count,(await imports.ListAsync(deadline.Token).ConfigureAwait(false)).Count);
    }

    [TestMethod]
    public async Task DesktopImportPauseAndExitJoinActualOriginalRequestAndResumeOnlyTheSameAssignedObject()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selection=await NativeTakeoutSelectionAsync(fixture,deadline.Token).ConfigureAwait(false);var state=fixture.Data.Setup.Accounts.State;
        var setup=await state.ExecuteAsync(db=>new{db.StateDirectory,db.InstallationId,Source=new LibrarySourceRepository(db).Snapshot().Sources.Single().Id},deadline.Token).ConfigureAwait(false);
        var cloud=new OriginalCloud(selection.Payload){Hold=true};WireOriginal(fixture.Data.Setup.Accounts,cloud);
        await using var current=new CurrentLibraryCatalogRuntime(state,setup.StateDirectory,setup.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        await using var inventory=new TakeoutInventoryService(state,fixture.Data.Budget);
        await using var imports=new TakeoutDesktopImportRuntime(state,fixture.Data.Budget,inventory,current,_=>Task.FromResult<string?>(Path.GetDirectoryName(selection.Path)));
        await imports.ResumeAsync().ConfigureAwait(false);var review=(await imports.ChooseFolderAsync(setup.Source,deadline.Token).ConfigureAwait(false))!;await WaitImportAsync(imports,review.Id,"ready",deadline.Token).ConfigureAwait(false);
        await imports.StartAsync(review.Id,deadline.Token).ConfigureAwait(false);Task? pause=null;
        try
        {
            await cloud.Entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);pause=imports.PauseAsync();await cloud.Canceled.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            Assert.IsFalse(pause.IsCompleted);Assert.AreEqual(ProcessorWorkState.Pausing,imports.State);Assert.AreEqual(0L,(await imports.ListAsync(deadline.Token).ConfigureAwait(false)).Single(row=>row.Id==review.Id).Verified);
        }
        finally {cloud.Release.TrySetResult();}
        await pause!.ConfigureAwait(false);Assert.AreEqual(ProcessorWorkState.Paused,imports.State);cloud.Hold=false;
        await imports.ResumeAsync().ConfigureAwait(false);await WaitImportAsync(imports,review.Id,"originals-verified",deadline.Token).ConfigureAwait(false);Assert.AreEqual(1,cloud.Allocations);
        await imports.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(imports.ExitConfirmed);
    }

    [TestMethod]
    public async Task DesktopImportExitCannotAcknowledgeBeforeAnUncooperativeNativeChooserActuallyJoins()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var state=fixture.Data.Setup.Accounts.State;var setup=await state.ExecuteAsync(db=>new{db.StateDirectory,db.InstallationId,Source=new LibrarySourceRepository(db).Snapshot().Sources.Single().Id},deadline.Token).ConfigureAwait(false);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var canceled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var current=new CurrentLibraryCatalogRuntime(state,setup.StateDirectory,setup.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);await using var inventory=new TakeoutInventoryService(state,fixture.Data.Budget);
        await using var imports=new TakeoutDesktopImportRuntime(state,fixture.Data.Budget,inventory,current,async token=>{using var registration=token.Register(()=>canceled.TrySetResult());entered.TrySetResult();await release.Task.ConfigureAwait(false);return null;});
        var picking=imports.ChooseFolderAsync(setup.Source,deadline.Token);Task? closing=null;
        try {await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);closing=imports.DisposeAsync().AsTask();await canceled.Task.WaitAsync(deadline.Token).ConfigureAwait(false);Assert.IsFalse(closing.IsCompleted);Assert.IsFalse(imports.ExitConfirmed);}
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>picking).ConfigureAwait(false);await closing!.ConfigureAwait(false);Assert.IsTrue(imports.ExitConfirmed);
    }

    [TestMethod]
    public async Task DesktopImportMissingInputIsRetainedPerImportAndRetryUsesTheSameReviewWithoutStoppingTheEngine()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selection=await NativeTakeoutSelectionAsync(fixture,deadline.Token).ConfigureAwait(false);var state=fixture.Data.Setup.Accounts.State;
        var setup=await state.ExecuteAsync(db=>new{db.StateDirectory,db.InstallationId,Source=new LibrarySourceRepository(db).Snapshot().Sources.Single().Id},deadline.Token).ConfigureAwait(false);
        var cloud=new OriginalCloud(selection.Payload);WireOriginal(fixture.Data.Setup.Accounts,cloud);
        await using var current=new CurrentLibraryCatalogRuntime(state,setup.StateDirectory,setup.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        await using var inventory=new TakeoutInventoryService(state,fixture.Data.Budget);
        await using var imports=new TakeoutDesktopImportRuntime(state,fixture.Data.Budget,inventory,current,_=>Task.FromResult<string?>(Path.GetDirectoryName(selection.Path)));
        await imports.ResumeAsync().ConfigureAwait(false);var review=(await imports.ChooseFolderAsync(setup.Source,deadline.Token).ConfigureAwait(false))!;
        await WaitImportAsync(imports,review.Id,"ready",deadline.Token).ConfigureAwait(false);var stamp=File.GetLastWriteTimeUtc(selection.Path);File.Delete(selection.Path);
        await imports.StartAsync(review.Id,deadline.Token).ConfigureAwait(false);var attention=await WaitImportAsync(imports,review.Id,"needs-attention",deadline.Token).ConfigureAwait(false);
        Assert.AreEqual("input-unavailable",attention.Issue);Assert.IsTrue(attention.Requested);Assert.AreEqual(review.Target,attention.Target);Assert.AreEqual(0,cloud.Allocations);Assert.IsNull(imports.Failure);Assert.IsNull(inventory.Failure);
        await File.WriteAllBytesAsync(selection.Path,selection.Payload,deadline.Token).ConfigureAwait(false);File.SetLastWriteTimeUtc(selection.Path,stamp);
        await imports.RetryAsync(review.Id,deadline.Token).ConfigureAwait(false);var verified=await WaitImportAsync(imports,review.Id,"originals-verified",deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(review.Id,verified.Id);Assert.AreEqual(review.Target,verified.Target);Assert.AreEqual(1,cloud.Allocations);Assert.IsNull(verified.Issue);
    }

    private static async Task<TakeoutImportActivity> WaitImportMetadataAsync(TakeoutDesktopImportRuntime runtime,Guid id,CancellationToken token)
    {
        while(true)
        {
            token.ThrowIfCancellationRequested();if(runtime.Failure is{}failure)throw new InvalidOperationException("The actual import metadata owner failed.",failure);
            var item=(await runtime.ListAsync(token).ConfigureAwait(false)).Single(value=>value.Id==id);if(item.Metadata is{Finished:true})return item;
            await Task.Delay(20,token).ConfigureAwait(false);
        }
    }
    private static async Task<TakeoutImportActivity> WaitImportAsync(TakeoutDesktopImportRuntime runtime,Guid id,string expected,CancellationToken token)
    {
        var elapsed=Stopwatch.StartNew();
        while(true)
        {
            token.ThrowIfCancellationRequested();if(runtime.Failure is{}failure)throw new InvalidOperationException("The actual import owner failed.",failure);
            var item=(await runtime.ListAsync(token).ConfigureAwait(false)).Single(value=>value.Id==id);if(item.State==expected)return item;
            if(elapsed.Elapsed>TimeSpan.FromSeconds(30))throw new TimeoutException("Actual import did not reach "+expected+"; current state="+item.State);
            await Task.Delay(20,token).ConfigureAwait(false);
        }
    }
}
