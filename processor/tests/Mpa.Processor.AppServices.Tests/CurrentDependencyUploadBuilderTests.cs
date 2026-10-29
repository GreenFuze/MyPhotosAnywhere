using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    private static readonly string[] CurrentUploadedRoleNames=["facts-chunk","facts-index","coverage-page","coverage-root"];

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentDependencyUploadActualFrozenBuilderPublishesAllFourRolesAndRetriesExactReferencesWithoutAdvancingNativeHead(bool selected)
    {
        IAsyncDisposable? lifetime=null;
        try
        {
            CurrentReaderFixture fixture;CurrentPreparedCatalogBasis basis;PublishedCatalogHead? head=null;
            if(selected)
            {
                var previous=await CurrentCompletionFixture.CreateAsync().ConfigureAwait(false);lifetime=previous;fixture=previous.Fixture;
                await using(var completion=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget))
                    head=await completion.FinishAsync(previous.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
                await using(var preparation=await NewCurrentBasisOwnerAsync(fixture).ConfigureAwait(false))
                    basis=await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(head.Commit,basis.Publication.Basis.Parent);Assert.AreEqual(head.Sequence,basis.Publication.Basis.ParentSequence);
            }
            else
            {
                var fresh=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);lifetime=fresh;fixture=fresh.Fixture;basis=fresh.Basis;
                Assert.IsNull(basis.Publication.Basis.Parent);
            }
            var scope=basis.Publication.Capture.Scope;var path=await CurrentSnapshotPathAsync(fixture,basis.Publication.Capture).ConfigureAwait(false);
            var snapshot=await File.ReadAllBytesAsync(path).ConfigureAwait(false);var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
            fixture.Data.Setup.Accounts.Readers.WorkspaceCreate=(folder,file,mime,payload,sha,token)=>
            {
                token.ThrowIfCancellationRequested();Assert.AreEqual(scope.FolderId,folder);Assert.IsTrue(mime is "application/json" or "application/gzip");
                Assert.AreEqual(CurrentHash(payload.ToArray()),sha);fixture.Data.Creates++;
                if(!fixture.Data.Cloud.TryGetValue(file,out var actual))
                {actual=new DriveWorkspaceObject(scope.AccountId,file,folder,7,payload.ToArray()){MimeType=mime};fixture.Data.Cloud.Add(file,actual);}
                CollectionAssert.AreEqual(payload.ToArray(),actual.Bytes.ToArray());Assert.AreEqual(mime,actual.MimeType);
                fixture.Data.MetadataMimeTypes[file]=mime;return Task.FromResult(actual);
            };
            var creates=fixture.Data.Creates;var allocations=fixture.Data.Allocations;var puts=fixture.Http.Puts;
            using(var store=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationSnapshotStore(database.StateDirectory,database,fixture.Data.Budget),CancellationToken.None).ConfigureAwait(false))
            using(var frozen=store.Open(basis.Publication.Capture,CancellationToken.None))
            {
                await using var sink=new CurrentCatalogGenerationObjectSink(scope.GenerationId,fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Budget,fixture.Reader);
                var builder=new CatalogGenerationDependencyBuilder(new(scope.AccountId,scope.FolderId),sink,WorkspaceObservationRepository.MaximumDocuments);
                var first=await builder.BuildAsync(frozen,basis.Sources,basis.Publication.Basis.Parent,CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(basis.Publication.Basis.Snapshot.Assets,first.Assets);Assert.AreEqual(basis.Publication.Basis.SourcePolicySha256,first.SourcePolicySha256);
                var rows=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
                {
                    var journal=new WorkspacePublicationRepository(database,fixture.Data.Budget);
                    return journal.Identities().Select(id=>journal.Read(id)!).Where(value=>value.Scope==scope).ToArray();
                },CancellationToken.None).ConfigureAwait(false);
                Assert.HasCount(4,rows);CollectionAssert.AreEquivalent(CurrentUploadedRoleNames,rows.Select(value=>value.Role).ToArray());
                Assert.AreEqual(4,rows.Select(value=>value.FileId).Distinct(StringComparer.Ordinal).Count());
                foreach(var row in rows)
                {
                    Assert.IsNotNull(row.FileId);Assert.IsNotNull(row.VerifiedVersion);
                    Assert.AreEqual(row.Role=="facts-chunk"?"application/gzip":"application/json",row.MimeType);
                    CollectionAssert.AreEqual(row.Payload.ToArray(),fixture.Data.Cloud[row.FileId].Bytes.ToArray());
                }
                Assert.AreEqual(creates+4,fixture.Data.Creates);Assert.AreEqual(allocations+4,fixture.Data.Allocations);
                var again=await builder.BuildAsync(frozen,basis.Sources,basis.Publication.Basis.Parent,CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(first,again);Assert.AreEqual(creates+4,fixture.Data.Creates);Assert.AreEqual(allocations+4,fixture.Data.Allocations);
                await sink.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(sink.ExitConfirmed);Assert.IsNull(sink.Failure);
            }
            var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Captures,after.Captures);
            Assert.AreEqual(before.Head,after.Head);Assert.AreEqual(before.Human,after.Human);Assert.AreEqual(head,await LineageHeadAsync(fixture).ConfigureAwait(false));
            var actual=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).Publication(scope.GenerationId),CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(basis.Publication,actual);CollectionAssert.AreEqual(snapshot,await File.ReadAllBytesAsync(path).ConfigureAwait(false));
            Assert.AreEqual(puts,fixture.Http.Puts);Assert.IsFalse(fixture.Http.BodyFiles.Contains(selected?"read-only-original":"current-dependency-original"));
            Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
        }
        finally {if(lifetime is not null) await lifetime.DisposeAsync().ConfigureAwait(false);}
    }

    [TestMethod]
    public async Task CurrentDependencyUploadSinkExitJoinsActualCreateAndKeepsQueuedCallerBytesPrivateWithoutAdmittingItsLateReference()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        _=await PrepareDependencyUploadAdmissionAsync(owned).ConfigureAwait(false);var create=fixture.Data.Setup.Accounts.Readers.WorkspaceCreate!;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Data.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,payload,sha,token)=>
        {token.ThrowIfCancellationRequested();entered.TrySetResult();await release.Task.ConfigureAwait(false);CollectionAssert.AreEqual(owned.Payload,payload.ToArray());return await create(folder,file,mime,payload,sha,CancellationToken.None).ConfigureAwait(false);};
        await using var sink=new CurrentCatalogGenerationObjectSink(owned.Generation,fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Budget,fixture.Reader);
        var mutable=owned.Payload.ToArray();var running=sink.StoreAsync("coverage-root","application/json",mutable,CancellationToken.None);mutable[^1]^=1;
        var stopping=Task.CompletedTask;var disposals=fixture.Data.Setup.Accounts.Readers.DisposalAttempts;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);stopping=sink.DisposeAsync().AsTask();
            Assert.IsFalse(running.IsCompleted);Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(sink.ExitConfirmed);
            Assert.AreEqual(disposals,fixture.Data.Setup.Accounts.Readers.DisposalAttempts);
        }
        finally {release.TrySetResult();try {await running.ConfigureAwait(false);} catch(OperationCanceledException) { }}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>running).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.IsTrue(sink.ExitConfirmed);
        var row=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var journal=new WorkspacePublicationRepository(database,fixture.Data.Budget);
            return journal.Identities().Select(id=>journal.Read(id)!).Single(value=>value.Scope.GenerationId==owned.Generation);
        },CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(row.FileId);Assert.IsNull(row.VerifiedVersion);CollectionAssert.AreEqual(owned.Payload,row.Payload.ToArray());
        Assert.IsTrue(File.Exists(owned.Path));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }
}
