using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class CatalogPublisherAuthorizationTests
{
    private static readonly Guid Library = Guid.Parse("94a81164-e980-4ec1-a566-cec36475b839");
    private static readonly Guid Controller = Guid.Parse("6c803c09-3b11-49cb-a3dc-1d42c3dc6f30");
    private static readonly string[] ControllerKinds = ["library-publisher-authorization", "user-operation"];
    private static readonly CatalogWorkspace Workspace = new("workspace-account", "workspace-folder");
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog-publisher-authorization.json")));
    private static byte[] Bytes(JsonElement root, string name) => Encoding.UTF8.GetBytes(root.GetProperty(name).GetString()!);
    private static TrustedProtocolActor Authority(JsonElement root, Guid? library = null, Guid? actor = null, long epoch = 1, string[]? kinds = null) =>
        new(library ?? Library, actor ?? Controller, epoch, Convert.FromBase64String(root.GetProperty("controllerPublicKeySpki").GetString()!), kinds ?? ControllerKinds);

    [TestMethod]
    public void ControllerApprovalGrantsOnlyTheSeparatePublishersCapturedCatalogStream()
    {
        using var fixture = Fixture(); var root = fixture.RootElement; var bytes = Bytes(root, "active");
        var reader = new CatalogPublicationReader(Workspace); var policy = reader.ReadPublisherAuthorization(bytes, Authority(root));
        Assert.AreEqual(Library, policy.LibraryId); Assert.AreEqual(Controller, policy.ControllerId); Assert.AreEqual(1L, policy.ControllerEpoch); Assert.AreEqual(9L, policy.Sequence);
        Assert.AreEqual(Workspace, policy.Workspace); Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(bytes)), policy.EnvelopeSha256);
        var publisher = policy.Publisher!; Assert.AreEqual(Guid.Parse(root.GetProperty("publisherId").GetString()!), publisher.ActorId); Assert.AreEqual(2L, publisher.Epoch);
        var commit = new SignedProtocolDocuments([CatalogPublicationReader.Capability]).VerifyAndRead(Bytes(root, "publisherCommit"), publisher);
        Assert.IsNull(reader.ReadCommit(commit).Parent); Assert.AreEqual(1L, commit.Sequence);
        Assert.ThrowsExactly<ProtocolFormatException>(() => new SignedProtocolDocuments([]).VerifyAndRead(Bytes(root, "publisherOperation"), publisher));
    }

    [TestMethod]
    public void ExplicitRevocationCarriesNoPublisherAndDoesNotChooseAnOlderPolicy()
    {
        using var fixture = Fixture(); var root = fixture.RootElement; var reader = new CatalogPublicationReader(Workspace);
        var active = reader.ReadPublisherAuthorization(Bytes(root, "active"), Authority(root));
        var revoked = reader.ReadPublisherAuthorization(Bytes(root, "revoked"), Authority(root));
        Assert.AreEqual(10L, revoked.Sequence); Assert.IsNull(revoked.Publisher); Assert.IsNull(revoked.PublisherKeyId);
        Assert.AreEqual(Controller, revoked.ControllerId); Assert.AreNotEqual(active.EnvelopeSha256, revoked.EnvelopeSha256);
        Assert.IsNotNull(active.Publisher); // Pure decoding cannot select or rewrite the owner's current policy.
    }

    [TestMethod]
    public void BothConsumersRejectEverySignedButInvalidOrOverbroadPublisherPolicy()
    {
        using var fixture = Fixture(); var root = fixture.RootElement; var cases = root.GetProperty("invalid"); Assert.AreEqual(26, cases.GetArrayLength());
        var reader = new CatalogPublicationReader(Workspace); var authority = Authority(root);
        foreach (var item in cases.EnumerateArray()) Assert.ThrowsExactly<ProtocolFormatException>(() => reader.ReadPublisherAuthorization(Bytes(item, "envelope"), authority), item.GetProperty("name").GetString());
    }

    [TestMethod]
    public void ReceivedPoliciesCannotSelectControllerLibraryEpochOrWorkspaceAuthority()
    {
        using var fixture = Fixture(); var root = fixture.RootElement; var bytes = Bytes(root, "active"); var reader = new CatalogPublicationReader(Workspace);
        var denied = new[] { Authority(root, library: Guid.NewGuid()), Authority(root, actor: Guid.NewGuid()), Authority(root, epoch: 2), Authority(root, kinds: ["catalog-commit"]) };
        foreach (var authority in denied) Assert.ThrowsExactly<ProtocolFormatException>(() => reader.ReadPublisherAuthorization(bytes, authority));
        Assert.ThrowsExactly<ProtocolFormatException>(() => new CatalogPublicationReader(new("foreign-account", Workspace.FolderId)).ReadPublisherAuthorization(bytes, Authority(root)));
        Assert.ThrowsExactly<ProtocolFormatException>(() => new CatalogPublicationReader(new(Workspace.AccountId, "foreign-folder")).ReadPublisherAuthorization(bytes, Authority(root)));
        bytes[^2] ^= 1;
        Assert.ThrowsExactly<ProtocolFormatException>(() => reader.ReadPublisherAuthorization(bytes, Authority(root)));
    }
}
