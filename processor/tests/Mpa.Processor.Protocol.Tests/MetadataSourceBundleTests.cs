using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class MetadataSourceBundleTests
{
    private static readonly int MagicLength=Encoding.ASCII.GetByteCount("MPA-RAW-METADATA-1\n");
    private static byte[] SourceA()=>Encoding.UTF8.GetBytes("\uFEFF { \"unknown\": {\"literal\":\"שלום <tag>\"}, \"title\":\"photo.jpg\" }\r\n");
    [TestMethod]
    public void BundlePreservesOpaqueSourceBytesAndOwnsBothCallerInputsAndReturnedBuffers()
    {
        var raw=SourceA();var expected=raw.ToArray();var first=MetadataSourceBytes.Capture(raw);raw[0]^=1;
        var second=MetadataSourceBytes.Capture(new byte[]{255,0,13,10,170});
        var bundle=MetadataSourceBundle.Capture([first,second]);var payload=bundle.CopyPayload();var decoded=MetadataSourceBundle.Read(payload);payload[^1]^=1;
        CollectionAssert.AreEqual(expected,bundle.ReadSource(first.Fingerprint.Sha256));CollectionAssert.AreEqual(expected,decoded.ReadSource(first.Fingerprint.Sha256));
        CollectionAssert.AreEqual(second.CopyBytes(),decoded.ReadSource(second.Fingerprint.Sha256));
        var copy=decoded.ReadSource(first.Fingerprint.Sha256);copy[0]^=1;CollectionAssert.AreEqual(expected,decoded.ReadSource(first.Fingerprint.Sha256));
        CollectionAssert.AreEqual(bundle.CopyPayload(),MetadataSourceBundle.Capture([second,first]).CopyPayload());
        CollectionAssert.AreEqual(bundle.Sources.ToArray(),decoded.Sources.ToArray());Assert.AreEqual(2,bundle.Sources.Count);
        Assert.ThrowsExactly<ProtocolFormatException>(()=>bundle.ReadSource(new string('a',64)));
    }
    [TestMethod]
    public void BundleEnforcesExistingSourceAndImmutableObjectLimitsWithoutExpandingThem()
    {
        Assert.ThrowsExactly<ProtocolFormatException>(()=>MetadataSourceBytes.Capture([]));Assert.ThrowsExactly<ProtocolFormatException>(()=>MetadataSourceBytes.Capture(new byte[TakeoutMetadataDocument.MaximumSourceBytes+1]));
        Assert.ThrowsExactly<ProtocolFormatException>(()=>MetadataSourceBundle.Capture([]));var one=MetadataSourceBytes.Capture(SourceA());Assert.ThrowsExactly<ProtocolFormatException>(()=>MetadataSourceBundle.Capture([one,one]));
        var small=Enumerable.Range(0,128).Select(i=>MetadataSourceBytes.Capture(new[]{checked((byte)i)})).ToArray();Assert.AreEqual(128,MetadataSourceBundle.Read(MetadataSourceBundle.Capture(small).CopyPayload()).Sources.Count);
        Assert.ThrowsExactly<ProtocolFormatException>(()=>MetadataSourceBundle.Capture(small.Append(MetadataSourceBytes.Capture(new byte[]{128}))));
        var large=Enumerable.Range(0,32).Select(i=>{var bytes=new byte[TakeoutMetadataDocument.MaximumSourceBytes];bytes[0]=checked((byte)i);return MetadataSourceBytes.Capture(bytes);}).ToArray();
        Assert.IsTrue(MetadataSourceBundle.Capture(large.Take(31)).CopyPayload().Length<MetadataSourceBundle.MaximumBytes);
        Assert.ThrowsExactly<ProtocolFormatException>(()=>MetadataSourceBundle.Capture(large));
    }
    [TestMethod]
    [DataRow("raw")]
    [DataRow("truncated")]
    [DataRow("tail")]
    [DataRow("magic")]
    [DataRow("negative-header")]
    [DataRow("oversized-header")]
    public void BundleRejectsChangedBytesAndUnexplainedFramingBeforeReturningAnySource(string change)
    {
        var bytes=MetadataSourceBundle.Capture([MetadataSourceBytes.Capture(SourceA())]).CopyPayload();
        switch(change)
        {
            case "raw":bytes[^1]^=1;break;
            case "truncated":bytes=bytes[..^1];break;
            case "tail":bytes=[..bytes,0];break;
            case "magic":bytes[0]^=1;break;
            case "negative-header":BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(MagicLength,4),-1);break;
            case "oversized-header":BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(MagicLength,4),int.MaxValue);break;
            default:throw new InvalidOperationException("Unknown fixture.");
        }
        Assert.ThrowsExactly<ProtocolFormatException>(()=>MetadataSourceBundle.Read(bytes));
    }
    [TestMethod]
    [DataRow("unknown-root")]
    [DataRow("unknown-entry")]
    [DataRow("noncanonical-size")]
    [DataRow("upper-hash")]
    [DataRow("duplicate")]
    [DataRow("missing-size")]
    public void BundleRejectsChangedClosedIndexAndCannotTreatHeaderClaimsAsVerifiedPayload(string change)
    {
        var bytes=MetadataSourceBundle.Capture([MetadataSourceBytes.Capture(SourceA())]).CopyPayload();
        var altered=Rewrite(bytes,root=>
        {
            var sources=root["sources"]!.AsArray();var entry=sources[0]!.AsObject();
            switch(change)
            {
                case "unknown-root":root["grantsPublication"]=true;break;
                case "unknown-entry":entry["path"]="../outside";break;
                case "noncanonical-size":entry["bytes"]="0"+entry["bytes"]!.GetValue<string>();break;
                case "upper-hash":entry["sha256"]=entry["sha256"]!.GetValue<string>().ToUpperInvariant();break;
                case "duplicate":sources.Add(entry.DeepClone());break;
                case "missing-size":entry.Remove("bytes");break;
                default:throw new InvalidOperationException("Unknown fixture.");
            }
        });
        Assert.ThrowsExactly<ProtocolFormatException>(()=>MetadataSourceBundle.Read(altered));
    }
    [TestMethod]
    public void BundleCompatibilityAndDeterministicContentIdentityRemainSeparateFromAnyProviderReceipt()
    {
        var source=MetadataSourceBytes.Capture(SourceA());var bytes=MetadataSourceBundle.Capture([source]).CopyPayload();
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(SourceA())),source.Fingerprint.Sha256);
        Assert.ThrowsExactly<ProtocolCompatibilityException>(()=>MetadataSourceBundle.Read(Rewrite(bytes,root=>root["major"]=2)));
    }
    private static byte[] Rewrite(byte[] bytes,Action<JsonObject> change)
    {
        var length=BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(MagicLength,4));var root=JsonNode.Parse(bytes.AsSpan(MagicLength+4,length))!.AsObject();change(root);
        var header=new CanonicalJson(32*1024).Encode(Encoding.UTF8.GetBytes(root.ToJsonString()));var output=new byte[MagicLength+4+header.Length+bytes.Length-MagicLength-4-length];
        bytes.AsSpan(0,MagicLength).CopyTo(output);BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(MagicLength,4),header.Length);header.CopyTo(output,MagicLength+4);bytes.AsSpan(MagicLength+4+length).CopyTo(output.AsSpan(MagicLength+4+header.Length));return output;
    }
}
