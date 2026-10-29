using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.AppServices;
using Mpa.Processor.Core;

namespace Mpa.Processor.Web.Tests;

public sealed partial class DesktopLibraryViewerReaderTests
{
    [TestMethod]
    public async Task LibraryPreviewBindsTheActualProviderReadToTheExactCanonicalAccountFileAndVersion()
    {
        var selected=Selection();var photo=Photo();var queries=0;var reads=0;var query=new ExactQueries((stage,account,id)=>{queries++;Assert.AreEqual(selected.StageId,stage);Assert.AreEqual(photo.AccountId,account);Assert.AreEqual(photo.Id,id);return new(selected,photo);});
        var transport=new PreviewTransport((account,file,version,_)=>{reads++;Assert.AreEqual(photo.AccountId,account);Assert.AreEqual(photo.FileId,file);Assert.AreEqual(photo.DriveVersion,version);return Task.FromResult<DesktopPreview?>(new([255,216,255,42],"image/jpeg"));});
        var result=await new DesktopLibraryPreviewReader(query,transport).ReadAsync(selected.StageId,photo.Id,photo.AccountId,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(result);CollectionAssert.AreEqual(new byte[]{255,216,255,42},result.Bytes);Assert.AreEqual(1,reads);Assert.AreEqual(2,queries);
    }

    [TestMethod]
    public async Task LibraryPreviewRejectsLateBytesAfterCanonicalStageOrVersionReplacementAndPreservesMissingPhotos()
    {
        var selected=Selection();var photo=Photo();
        foreach(var mode in new[]{"stage","version","missing"})
        {
            var read=false;var query=new ExactQueries((_,_,_)=>new(read && mode=="stage"?Selection():selected,read && mode=="missing"?null:read && mode=="version"?photo with {DriveVersion=2}:photo));
            var transport=new PreviewTransport((_,_,_,_)=>{read=true;return Task.FromResult<DesktopPreview?>(new([255,216,255],"image/jpeg"));});var reader=new DesktopLibraryPreviewReader(query,transport);
            if(mode=="stage") await Assert.ThrowsExactlyAsync<CurrentCanonicalPositionChangedException>(()=>reader.ReadAsync(selected.StageId,photo.Id,null,CancellationToken.None)).ConfigureAwait(false);
            else Assert.IsNull(await reader.ReadAsync(selected.StageId,photo.Id,null,CancellationToken.None).ConfigureAwait(false));
            Assert.IsTrue(read);
        }
    }

    [TestMethod]
    public async Task LibraryPreviewMissingOrForeignPhotosCannotStartAProviderReadAndOperationalFailuresPropagate()
    {
        var selected=Selection();var photo=Photo();var reads=0;var transport=new PreviewTransport((_,_,_,_)=>{reads++;throw new IOException("Synthetic provider outage.");});
        var missing=new DesktopLibraryPreviewReader(new ExactQueries((_,_,_)=>new(selected,null)),transport);Assert.IsNull(await missing.ReadAsync(selected.StageId,photo.Id,null,CancellationToken.None).ConfigureAwait(false));Assert.AreEqual(0,reads);
        var foreign=new DesktopLibraryPreviewReader(new ExactQueries((_,_,_)=>new(selected,photo with {AccountId="account-B"})),transport);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>foreign.ReadAsync(selected.StageId,photo.Id,"account-A",CancellationToken.None)).ConfigureAwait(false);Assert.AreEqual(0,reads);
        var actual=new DesktopLibraryPreviewReader(new ExactQueries((_,_,_)=>new(selected,photo)),transport);await Assert.ThrowsExactlyAsync<IOException>(()=>actual.ReadAsync(selected.StageId,photo.Id,null,CancellationToken.None)).ConfigureAwait(false);Assert.AreEqual(1,reads);
    }

    [TestMethod]
    public async Task LibraryPreviewCallerCancellationRejectsUncooperativeLateTransportBytes()
    {
        var selected=Selection();var photo=Photo();using var canceled=new CancellationTokenSource();var queries=0;
        var query=new ExactQueries((_,_,_)=>{queries++;return new(selected,photo);});var transport=new PreviewTransport((_,_,_,_)=>{canceled.Cancel();return Task.FromResult<DesktopPreview?>(new([255,216,255],"image/jpeg"));});
        await Assert.ThrowsAsync<OperationCanceledException>(()=>new DesktopLibraryPreviewReader(query,transport).ReadAsync(selected.StageId,photo.Id,null,canceled.Token)).ConfigureAwait(false);Assert.AreEqual(1,queries);
    }

    private sealed class PreviewTransport(Func<string,string,long,CancellationToken,Task<DesktopPreview?>> read) : IDesktopLibraryPreviewTransport
    {
        public Task<DesktopPreview?> ReadAsync(string accountId,string fileId,long expectedVersion,CancellationToken cancellationToken)=>read(accountId,fileId,expectedVersion,cancellationToken);
    }
}
