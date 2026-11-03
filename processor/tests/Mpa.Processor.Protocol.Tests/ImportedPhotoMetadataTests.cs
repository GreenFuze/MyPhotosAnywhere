using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class ImportedPhotoMetadataTests
{
    private static readonly Guid Library=Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Import=Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Job=Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid Asset=new DriveAssetIdentity(Library).Derive("account-one","original-one");
    private static TakeoutMetadataDocument Document(string description="Imported <tag> שלום")=>TakeoutMetadataDocument.ParseSource(JsonSerializer.SerializeToUtf8Bytes(new
    {title="original.jpg",description,photoTakenTime=new{timestamp="1"},geoData=new{latitude=31.777,longitude=-35.123},people=new[]{new{name="Imported hint"}}}));
    private static ImportedPhotoMetadata Metadata(TakeoutMetadataDocument document)
    {
        var raw=document.CopyRawSource();var artifact=document.EncodeArtifact();
        return ImportedPhotoMetadata.FromTakeout(Asset,Import,Job,new string('a',64),7,new string('b',64),new(raw.Length,document.SourceSha256,"input.bin"),new(artifact.Length,Hash(artifact),"output.bin"),document);
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static DriveCatalogAsset Photo()=>new(Asset,"account-one","original-one",7,"Human filename.jpg","image/jpeg",40,30,null,null,9000,Array.AsReadOnly(new[]{new DriveCatalogSource(Guid.Parse("44444444-4444-4444-8444-444444444444"),1,0)}));

    [TestMethod]
    public void ImportedProfileKeepsDriveAndHumanFieldsSeparateAndCarriesExactBoundedProvenance()
    {
        var metadata=Metadata(Document());var restored=ImportedPhotoMetadata.Read(metadata.Encode());
        Assert.AreEqual(1000L,restored.CapturedUtc);Assert.AreEqual(31.777,restored.Latitude);Assert.AreEqual(-35.123,restored.Longitude);
        Assert.AreEqual("Imported <tag> שלום",restored.Description);Assert.AreEqual("Imported hint",restored.ImportedNames.Single());
        Assert.AreEqual(metadata.Source,restored.Source);Assert.AreEqual(metadata.Artifact,restored.Artifact);Assert.AreEqual(Asset,restored.AssetId);
        var observed=Photo();var reader=new DriveCatalogFacts(Library);var prefix=Asset.ToString("N")[..2];var legacy=reader.Encode(prefix,[observed]);
        var projected=observed with{ImportedMetadata=restored};var bytes=reader.Encode(prefix,[projected]);using var root=JsonDocument.Parse(bytes);
        Assert.AreEqual("mpa-photo-facts",root.RootElement.GetProperty("format").GetString());Assert.AreEqual(2,root.RootElement.GetProperty("major").GetInt32());
        var decoded=reader.Read(bytes,prefix);Assert.IsTrue(decoded.RequiresImportedMetadata);var photo=decoded.Assets.Single();
        Assert.AreEqual("Human filename.jpg",photo.Name);Assert.AreEqual(7L,photo.DriveVersion);Assert.IsNull(photo.CapturedUtc);Assert.AreEqual(9000L,photo.ModifiedUtc);Assert.AreEqual(1000L,photo.EffectiveUtc);
        Assert.AreEqual(2000L,(photo with{CapturedUtc=2000}).EffectiveUtc,"Observed/human Drive capture is never replaced by an imported date.");
        CollectionAssert.AreEqual(bytes,reader.Encode(prefix,decoded.Assets));CollectionAssert.AreEqual(legacy,reader.Encode(prefix,[photo with{ImportedMetadata=null}]));
    }

    [TestMethod]
    [DataRow("asset")]
    [DataRow("original-version")]
    [DataRow("source-length")]
    [DataRow("hash")]
    [DataRow("coordinate-type")]
    [DataRow("coordinate-range")]
    [DataRow("time")]
    [DataRow("name-hints")]
    [DataRow("unknown-field")]
    [DataRow("unadvertised-profile")]
    public void ImportedFactsRejectMalformedOrCrossAssetProvenanceRatherThanRewritingIt(string change)
    {
        var reader=new DriveCatalogFacts(Library);var prefix=Asset.ToString("N")[..2];var body=JsonNode.Parse(reader.Encode(prefix,[Photo() with{ImportedMetadata=Metadata(Document())}]))!;
        var imported=body["assets"]![0]!["importedMetadata"]!;
        switch(change)
        {
            case "asset":imported["assetId"]=Guid.NewGuid().ToString("D");break;
            case "original-version":imported["originalVersion"]="8";break;
            case "source-length":imported["sourceBytes"]="0";break;
            case "hash":imported["artifactSha256"]=new string('A',64);break;
            case "coordinate-type":imported["latitude"]=31;break;
            case "coordinate-range":imported["latitude"]="91";break;
            case "time":imported["capturedUtc"]="253402300800000";break;
            case "name-hints":imported["importedNames"]="person-identity";break;
            case "unknown-field":imported["personId"]=Guid.NewGuid().ToString("D");break;
            case "unadvertised-profile":body["format"]="mpa-drive-facts";body["major"]=1;break;
            default:throw new InvalidOperationException("Unknown test case.");
        }
        var bytes=new CanonicalJson().Encode(Encoding.UTF8.GetBytes(body.ToJsonString()));Assert.ThrowsExactly<ProtocolFormatException>(()=>reader.Read(bytes,prefix));
    }

    [TestMethod]
    public void ImportedProjectionRequiresTheExactAnalyzedBytesAndKeepsExistingPortableBounds()
    {
        var document=Document();var metadata=Metadata(document);var other=Document("Changed raw input");
        Assert.ThrowsExactly<ProtocolFormatException>(()=>ImportedPhotoMetadata.FromTakeout(Asset,Import,Job,metadata.OriginalSha256,metadata.OriginalVersion,metadata.SidecarKey,
            new(metadata.Source.Bytes,metadata.Source.Sha256,"input.bin"),new(metadata.Artifact.Bytes,metadata.Artifact.Sha256,"output.bin"),other));
        var large=Metadata(Document(new string('x',65500)));Assert.ThrowsExactly<CatalogChunkCapacityException>(()=>large.Encode());
        var valid=metadata.Encode();Assert.ThrowsExactly<ProtocolFormatException>(()=>ImportedPhotoMetadata.Read(Encoding.UTF8.GetBytes(" "+Encoding.UTF8.GetString(valid))));
        var root=JsonNode.Parse(valid)!;root["recipeSha256"]=new string('c',64);var unknown=new CanonicalJson().Encode(Encoding.UTF8.GetBytes(root.ToJsonString()));
        Assert.ThrowsExactly<ProtocolCompatibilityException>(()=>ImportedPhotoMetadata.Read(unknown));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ActualSignedCatalogMustAdvertiseImportedObservationsBeforeAReaderCanYieldThem(bool attention)
    {
        var photo=attention?Photo() with{ImportedAttention=new(2,new string('d',64),"conflicting-metadata")}:Photo() with{ImportedMetadata=Metadata(Document())};var prefix=Asset.ToString("N")[..2];var expanded=new DriveCatalogFacts(Library).Encode(prefix,[photo]);
        using var output=new MemoryStream();using(var gzip=new GZipStream(output,CompressionLevel.Optimal,true))gzip.Write(expanded);var encoded=output.ToArray();
        object Reference(string file,byte[] bytes)=>new{accountId="workspace-account",fileId=file,workspaceId="workspace-folder",bytes=bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),sha256=Hash(bytes)};
        var root=new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(new{format="mpa-catalog-index",major=1,prefix="",children=new[]{new{edge=prefix,kind="chunk",@object=Reference("leaf",encoded),expanded=new{bytes=expanded.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),sha256=Hash(expanded)}}}}));
        var payload=new{generationId=Guid.NewGuid().ToString("D"),parent=(object?)null,rootIndex=Reference("index",root),sourceCoverage=Reference("coverage","{}"u8.ToArray()),settingsRevision="1",minimumHistoryGeneration="1"};
        var workspace=new CatalogWorkspace("workspace-account","workspace-folder");using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var signer=new ProtocolDocumentSigner(key);var actor=Guid.NewGuid();var trusted=new TrustedProtocolActor(Library,actor,1,key.ExportSubjectPublicKeyInfo(),["catalog-commit"]);
        foreach(var advertise in new[]{false,true})
        {
            var capabilities=advertise?new[]{DriveCatalogFacts.Capability,CatalogPublicationReader.Capability,ImportedPhotoMetadata.Capability}:new[]{DriveCatalogFacts.Capability,CatalogPublicationReader.Capability};
            var body=JsonSerializer.SerializeToUtf8Bytes(new{format="mpa",major=1,minor=0,libraryId=Library.ToString("D"),actorId=actor.ToString("D"),epoch="1",sequence="1",kind="catalog-commit",requiredCapabilities=capabilities,payload});
            var signature=signer.Sign(body);var source=new ImportedSource(root,encoded);
            if(advertise)
            {
                Assert.ThrowsExactly<ProtocolCompatibilityException>(()=>new ProtocolDocumentReader([DriveCatalogFacts.Capability,CatalogPublicationReader.Capability]).VerifyAndRead(body,signature,trusted));Assert.AreEqual(0,source.Reads);
            }
            var verified=new ProtocolDocumentReader([DriveCatalogFacts.Capability,CatalogPublicationReader.Capability,ImportedPhotoMetadata.Capability]).VerifyAndRead(body,signature,trusted);
            var returned=new List<DriveCatalogAsset>();async Task Read(){await foreach(var leaf in new CatalogFactsTraversal(Library,workspace,source).ReadLeavesAsync(verified).ConfigureAwait(false))returned.AddRange(leaf.Facts.Assets);}
            if(advertise){await Read().ConfigureAwait(false);Assert.AreEqual(1,returned.Count);Assert.AreEqual("Human filename.jpg",returned[0].Name);Assert.AreEqual(attention?9000L:1000L,returned[0].EffectiveUtc);}
            else{await Assert.ThrowsExactlyAsync<ProtocolFormatException>(Read).ConfigureAwait(false);Assert.AreEqual(0,returned.Count);}
        }
    }
    private sealed class ImportedSource(byte[] root,byte[] leaf):ICatalogObjectSource
    {
        public int Reads{get;private set;}
        public Task<ReadOnlyMemory<byte>> ReadAsync(CatalogObjectReference reference,CancellationToken token)
        {token.ThrowIfCancellationRequested();Reads++;return Task.FromResult<ReadOnlyMemory<byte>>(reference.FileId switch{"index"=>root,"leaf"=>leaf,_=>throw new InvalidOperationException("Unexpected catalog read.")});}
    }
}
