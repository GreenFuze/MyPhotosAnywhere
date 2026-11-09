using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

public sealed partial class GoogleWorkspaceTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task EveryImmutableReadRouteReturnsActualCorpusAndEncodingAndNormalizesParentOrder(int route)
    {
        var before=Changed(root=>{root["driveId"]="actual-shared-drive";root["parents"]=new JsonArray("workspace","extra-parent");});
        var after=Changed(root=>{root["driveId"]="actual-shared-drive";root["parents"]=new JsonArray("extra-parent","workspace");});
        using var fixture=new Fixture {Before=before,After=after};await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var result=await ReadScopeRouteAsync(connection,route).ConfigureAwait(false);
        Assert.AreEqual("actual-shared-drive",result.SharedDriveId);Assert.AreEqual("application/json",result.MimeType);
        CollectionAssert.AreEqual(Content,result.Bytes.ToArray());Assert.AreEqual(2,fixture.MetadataCalls);Assert.AreEqual(1,fixture.BlobCalls);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task EveryImmutableReadRouteRejectsChangedCorpusAndAdditionalParentEvenWithUnchangedVersionAndHash(int route)
    {
        var first=Changed(root=>root["driveId"]="original-corpus");
        var changedDrive=Changed(root=>root["driveId"]="moved-corpus");
        var changedParents=Changed(root=>{root["driveId"]="original-corpus";root["parents"]=new JsonArray("workspace","new-parent");});
        foreach(var after in new[]{changedDrive,changedParents,Metadata})
        {
            using var fixture=new Fixture {Before=first,After=after};await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(()=>ReadScopeRouteAsync(connection,route)).ConfigureAwait(false);
            Assert.AreEqual(2,fixture.MetadataCalls);Assert.AreEqual(1,fixture.BlobCalls);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task EveryImmutableReadRouteRejectsMalformedCorpusBeforeAnyBody(int route)
    {
        var invalid=new[]{Changed(root=>root["driveId"]=null),Changed(root=>root["driveId"]=""),
            Changed(root=>root["driveId"]=7),Changed(root=>root["driveId"]="root"),Changed(root=>root["driveId"]="not/a/drive")};
        foreach(var before in invalid)
        {
            using var fixture=new Fixture {Before=before};await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>ReadScopeRouteAsync(connection,route)).ConfigureAwait(false);
            Assert.AreEqual(1,fixture.MetadataCalls);Assert.AreEqual(0,fixture.BlobCalls);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BinaryMetadataReadsExposeActualEncodingAndCorpusWithoutGrantingGzipValidity(bool candidate)
    {
        var before=Changed(root=>{root["mimeType"]="application/gzip";root["driveId"]="actual-shared-drive";});
        using var fixture=new Fixture {Before=before};await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        // The fixture body is deliberately ordinary bytes: transport metadata is not
        // gzip validity, a signed graph, policy authority or canonical activation.
        var result=candidate
            ? await connection.ReadWorkspaceMetadataCandidateAsync("workspace",new("catalog-file",7,Content.Length,"application/gzip"),CancellationToken.None).ConfigureAwait(false)
            : await connection.ReadWorkspaceObjectAsync("workspace","catalog-file",Content.Length,Digest,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("application/gzip",result.MimeType);Assert.AreEqual("actual-shared-drive",result.SharedDriveId);
        CollectionAssert.AreEqual(Content,result.Bytes.ToArray());Assert.AreEqual(1,fixture.BlobCalls);
    }

    private static Task<DriveWorkspaceObject> ReadScopeRouteAsync(GoogleAccountConnection connection,int route)=>route switch
    {
        0=>connection.ReadWorkspaceObjectAsync("workspace","catalog-file",Content.Length,Digest,CancellationToken.None),
        1=>connection.ReadWorkspaceJsonByIdAsync("workspace","catalog-file",CancellationToken.None),
        2=>connection.ReadWorkspaceMetadataCandidateAsync("workspace",new("catalog-file",7,Content.Length,"application/json"),CancellationToken.None),
        _=>throw new ArgumentOutOfRangeException(nameof(route))
    };
}
