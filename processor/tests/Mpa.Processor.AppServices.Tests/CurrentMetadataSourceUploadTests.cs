using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RawMetadataBundleUsesActualCurrentUploadReadbackAndKeepsItsFirstIdentityAfterLostReply(bool loseReply)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;var state=fixture.Data.Setup.Accounts.State;var scope=owned.Basis.Publication.Capture.Scope;
        var raw=" {\"title\":\"photo.jpg\",\"unknown\":{\"keep\":true},\"literal\":\"שלום <tag>\"}\r\n"u8.ToArray();var source=MetadataSourceBytes.Capture(raw);var payload=MetadataSourceBundle.Capture([source]).CopyPayload();
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var allocations=fixture.Data.Allocations;var creates=fixture.Data.Creates;var lost=false;
        fixture.Data.Setup.Accounts.Readers.WorkspaceCreate=(folder,file,mime,bytes,hash,token)=>
        {
            token.ThrowIfCancellationRequested();Assert.AreEqual(scope.FolderId,folder);Assert.AreEqual(MetadataSourceBundle.MimeType,mime);Assert.AreEqual(CurrentHash(payload),hash);CollectionAssert.AreEqual(payload,bytes.ToArray());
            fixture.Data.Creates++;if(!fixture.Data.Cloud.TryGetValue(file,out var stored)){stored=new(scope.AccountId,file,folder,7,bytes.ToArray()){MimeType=mime};fixture.Data.Cloud.Add(file,stored);}
            fixture.Data.MetadataMimeTypes[file]=mime;
            if(loseReply&&!lost){lost=true;throw new HttpRequestException("Synthetic lost raw metadata bundle reply after actual remote storage.");}return Task.FromResult(stored);
        };
        CatalogObjectReference reference;
        if(loseReply)
        {
            await using(var first=new CurrentCatalogGenerationObjectSink(owned.Generation,state,fixture.Data.Setup.Accounts.Service,fixture.Data.Budget,fixture.Reader))
                await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>first.StoreAsync(MetadataSourceBundle.Role,MetadataSourceBundle.MimeType,payload,CancellationToken.None)).ConfigureAwait(false);
            var pending=await state.ExecuteAsync(db=>new WorkspacePublicationRepository(db,fixture.Data.Budget).Read(CatalogDependencyPayload.ObjectId(scope,MetadataSourceBundle.Role,MetadataSourceBundle.MimeType,CurrentHash(payload)))!,CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(pending.FileId);Assert.IsNull(pending.VerifiedVersion);CollectionAssert.AreEqual(payload,pending.Payload.ToArray());
        }
        await using(var sink=new CurrentCatalogGenerationObjectSink(owned.Generation,state,fixture.Data.Setup.Accounts.Service,fixture.Data.Budget,fixture.Reader))
        {
            reference=await sink.StoreAsync(MetadataSourceBundle.Role,MetadataSourceBundle.MimeType,payload,CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(reference,await sink.StoreAsync(MetadataSourceBundle.Role,MetadataSourceBundle.MimeType,payload,CancellationToken.None).ConfigureAwait(false));
            CollectionAssert.AreEqual(raw,(await sink.ReadMetadataSourceBundleAsync(reference,7,CancellationToken.None).ConfigureAwait(false)).ReadSource(source.Fingerprint.Sha256));
            await Assert.ThrowsExactlyAsync<WorkspacePublicationConflictException>(()=>sink.ReadMetadataSourceBundleAsync(reference with{WorkspaceId="foreign-workspace"},7,CancellationToken.None)).ConfigureAwait(false);
            var original=fixture.Data.Cloud[reference.FileId];var changed=payload.ToArray();changed[^1]^=1;
            fixture.Data.Cloud[reference.FileId]=original with{Bytes=changed};
            try{await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(()=>sink.ReadMetadataSourceBundleAsync(reference,7,CancellationToken.None)).ConfigureAwait(false);}
            finally{fixture.Data.Cloud[reference.FileId]=original;}
            fixture.Data.Cloud[reference.FileId]=original with{DriveVersion=6};
            try{await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(()=>sink.ReadMetadataSourceBundleAsync(reference,7,CancellationToken.None)).ConfigureAwait(false);}
            finally{fixture.Data.Cloud[reference.FileId]=original;}
            CollectionAssert.AreEqual(raw,(await sink.ReadMetadataSourceBundleAsync(reference,7,CancellationToken.None).ConfigureAwait(false)).ReadSource(source.Fingerprint.Sha256));
            if(!loseReply)
            {
                var reader=fixture.Data.Setup.Accounts.Readers.WorkspaceRead!;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource<DriveWorkspaceObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Data.Setup.Accounts.Readers.WorkspaceRead=(folder,file,length,hash,token)=>
                {if(file!=reference.FileId)return reader(folder,file,length,hash,token);entered.TrySetResult();return release.Task;};
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var reading=sink.ReadMetadataSourceBundleAsync(reference,7,deadline.Token);
                try
                {
                    await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                    var stopping=sink.DisposeAsync().AsTask();Assert.IsFalse(stopping.IsCompleted,"Actual provider read must join before the sink releases borrowed owners.");Assert.IsFalse(sink.ExitConfirmed);
                    release.TrySetResult(original);await Assert.ThrowsAsync<OperationCanceledException>(()=>reading).ConfigureAwait(false);
                    await stopping.ConfigureAwait(false);Assert.IsTrue(sink.ExitConfirmed);
                }
                finally{release.TrySetResult(original);fixture.Data.Setup.Accounts.Readers.WorkspaceRead=reader;}
            }
        }
        Assert.AreEqual(allocations+1,fixture.Data.Allocations);Assert.AreEqual(creates+(loseReply?2:1),fixture.Data.Creates);
        var saved=await state.ExecuteAsync(db=>new WorkspacePublicationRepository(db,fixture.Data.Budget).Read(CatalogDependencyPayload.ObjectId(scope,MetadataSourceBundle.Role,MetadataSourceBundle.MimeType,CurrentHash(payload)))!,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(reference.FileId,saved.FileId);Assert.AreEqual(7L,saved.VerifiedVersion);Assert.AreEqual(scope,saved.Scope);
        CollectionAssert.AreEqual(raw,MetadataSourceBundle.Read(fixture.Data.Cloud[reference.FileId].Bytes.Span).ReadSource(source.Fingerprint.Sha256));
        var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Captures,after.Captures);Assert.AreEqual(before.Head,after.Head);Assert.AreEqual(before.Human,after.Human);
        Assert.IsFalse(fixture.Http.BodyFiles.Contains("current-dependency-original"));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }
    [TestMethod]
    public async Task RawMetadataBundleRejectsMalformedBytesBeforePreparingOrAllocatingAndLegacyAuthorityCannotUploadIt()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;var state=fixture.Data.Setup.Accounts.State;
        var payload=MetadataSourceBundle.Capture([MetadataSourceBytes.Capture("{\"unknown\":true}"u8)]).CopyPayload();var bad=payload.ToArray();bad[^1]^=1;var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var allocations=fixture.Data.Allocations;
        await using(var sink=new CurrentCatalogGenerationObjectSink(owned.Generation,state,fixture.Data.Setup.Accounts.Service,fixture.Data.Budget,fixture.Reader))
        {
            Assert.ThrowsExactly<ProtocolFormatException>(()=>sink.StoreAsync(MetadataSourceBundle.Role,MetadataSourceBundle.MimeType,bad,CancellationToken.None));
            Assert.ThrowsExactly<ArgumentException>(()=>sink.StoreAsync(MetadataSourceBundle.Role,"application/json",payload,CancellationToken.None));
        }
        Assert.AreEqual(before,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));Assert.AreEqual(allocations,fixture.Data.Allocations);
        var row=await fixture.Reader.PrepareCurrentDependencyAsync(owned.Generation,MetadataSourceBundle.Role,MetadataSourceBundle.MimeType,payload,CancellationToken.None).ConfigureAwait(false);
        var authority=new ForbiddenRawLegacyAuthority();var legacy=new WorkspacePublicationUploader(state,fixture.Data.Setup.Accounts.Service,fixture.Data.Budget,authority);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>legacy.UploadAsync(row.Id,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0,authority.Calls);Assert.AreEqual(allocations,fixture.Data.Allocations);Assert.IsNull((await state.ExecuteAsync(db=>new WorkspacePublicationRepository(db,fixture.Data.Budget).Read(row.Id),CancellationToken.None).ConfigureAwait(false))!.FileId);
    }
    private sealed class ForbiddenRawLegacyAuthority:IWorkspacePublicationAuthority
    {
        public int Calls{get;private set;}
        public Task ValidateAsync(WorkspacePublicationScope scope,CancellationToken cancellationToken)
        {cancellationToken.ThrowIfCancellationRequested();Calls++;throw new InvalidOperationException("Historical/generic authority cannot admit raw source uploads.");}
    }
}
