using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class LibraryConnectionDescriptorTests
{
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "library-connection.json")));

    [TestMethod]
    public void BothClientsReadTheIndependentPublicConnectionVectorsWithoutChangingIdentity()
    {
        using var fixture = Fixture(); var valid = fixture.RootElement.GetProperty("valid"); Assert.AreEqual(3, valid.GetArrayLength());
        foreach (var row in valid.EnumerateArray())
        {
            var code = row.GetProperty("code").GetString()!; var actual = LibraryConnectionDescriptor.Decode(code);
            Assert.AreEqual(code, actual.Encode()); Assert.AreEqual(row.GetProperty("sha256").GetString(), actual.DocumentSha256);
            Assert.AreEqual(Guid.Parse("94a81164-e980-4ec1-a566-cec36475b839"), actual.LibraryId);
            Assert.AreEqual(Guid.Parse("6c803c09-3b11-49cb-a3dc-1d42c3dc6f30"), actual.ControllerId);
            Assert.IsTrue(actual.ControllerEpoch > 0); Assert.AreNotEqual("root", actual.Workspace.FolderId);
            using var verifier = new ProtocolDocumentVerifier(Convert.FromBase64String(actual.ControllerPublicKeySpki));
            Assert.AreEqual(verifier.KeyId, actual.ControllerKeyId);
        }
        Assert.AreEqual(long.MaxValue, LibraryConnectionDescriptor.Decode(valid[1].GetProperty("code").GetString()!).ControllerEpoch);
        Assert.AreEqual(256, LibraryConnectionDescriptor.Decode(valid[2].GetProperty("code").GetString()!).Workspace.AccountId.Length);
    }

    [TestMethod]
    public void BothClientsRejectEveryMalformedOverbroadAndNoncanonicalConnectionVector()
    {
        using var fixture = Fixture(); var invalid = fixture.RootElement.GetProperty("invalid"); Assert.AreEqual(52, invalid.GetArrayLength());
        foreach (var row in invalid.EnumerateArray())
            Assert.ThrowsExactly<ProtocolFormatException>(() => LibraryConnectionDescriptor.Decode(row.GetProperty("code").GetString()!), row.GetProperty("name").GetString());
    }

    [TestMethod]
    public void ConnectionOwnsOnlyValidatedPublicKeyBytesAndCannotImportOtherKeyKinds()
    {
        using var fixture = Fixture();
        var original = LibraryConnectionDescriptor.Decode(fixture.RootElement.GetProperty("valid")[0].GetProperty("code").GetString()!);
        var bytes = Convert.FromBase64String(original.ControllerPublicKeySpki);
        var owned = new LibraryConnectionDescriptor(original.LibraryId, original.ControllerId, original.ControllerEpoch, original.Workspace, bytes);
        bytes[0] ^= 1;
        Assert.AreEqual(original.Encode(), owned.Encode()); Assert.AreEqual(original.DocumentSha256, owned.DocumentSha256);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(2048); using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var invalid = new[] { p384.ExportSubjectPublicKeyInfo(), rsa.ExportSubjectPublicKeyInfo(), p256.ExportPkcs8PrivateKey(), Array.Empty<byte>(), new byte[513] };
        foreach (var key in invalid)
            Assert.ThrowsExactly<ProtocolFormatException>(() => new LibraryConnectionDescriptor(original.LibraryId, original.ControllerId, 1, original.Workspace, key));
        Assert.ThrowsExactly<ProtocolFormatException>(() => new LibraryConnectionDescriptor(Guid.Empty, original.ControllerId, 1, original.Workspace, Convert.FromBase64String(original.ControllerPublicKeySpki)));
        Assert.ThrowsExactly<ProtocolFormatException>(() => new LibraryConnectionDescriptor(original.LibraryId, original.ControllerId, 1,
            new(original.Workspace.AccountId, "root"), Convert.FromBase64String(original.ControllerPublicKeySpki)));
    }
}
