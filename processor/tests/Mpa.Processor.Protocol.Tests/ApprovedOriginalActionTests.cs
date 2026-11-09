using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class ApprovedOriginalActionTests
{
    private const string Actor = "11111111-1111-4111-8111-111111111111";
    private const string Asset = "94a81164-e980-4ec1-a566-cec36475b839";
    private const string Hash = "c477c29974484c04a2940ad6ed99ac6f7fd37318f24480d519c814da574c6995";

    private static JsonObject Payload(string action, string source) => new()
    {
        ["approvalId"] = Asset, ["action"] = action, ["source"] = JsonNode.Parse(source),
        ["destination"] = null, ["recipeSha256"] = null, ["tombstoneId"] = null
    };

    private static JsonObject Destination() => new() { ["accountPermissionId"] = "account-1", ["folderId"] = "selected-folder", ["workspaceFolderId"] = "workspace" };
    private static string DriveSource(bool trashed = false) => new JsonObject
    {
        ["type"] = "drive-original", ["assetId"] = Asset, ["accountPermissionId"] = "account-1", ["fileId"] = "original-file",
        ["bytes"] = "123", ["sha256"] = Hash, ["revision"] = "12", ["etag"] = "\"strong-revision-12\"", ["trashed"] = trashed
    }.ToJsonString();

    [TestMethod]
    public void TrashAndRestoreAreDifferentClosedActionsWithCurrentOriginalAndTombstoneScopes()
    {
        using var harness = new SignedActionHarness();
        var trash = Payload("trash", DriveSource());
        var approved = harness.Read(trash);
        Assert.AreEqual(OriginalActionKind.Trash, approved.Action);
        Assert.IsNull(approved.Destination);
        Assert.AreEqual("original-file", ((ApprovedDriveInput)approved.Source).FileId);
        var restore = Payload("restore", DriveSource(trashed: true));
        Assert.ThrowsExactly<ProtocolFormatException>(() => harness.Read(restore));
        restore["tombstoneId"] = Asset;
        Assert.AreEqual(OriginalActionKind.Restore, harness.Read(restore).Action);
        trash["destination"] = Destination();
        Assert.ThrowsExactly<ProtocolFormatException>(() => harness.Read(trash));
    }

    [TestMethod]
    public void BackupAndImportCaptureDestinationsAndEditedCopiesRequireARecipe()
    {
        using var harness = new SignedActionHarness();
        var device = new JsonObject { ["type"] = "device-original", ["assetId"] = Asset, ["actorId"] = Actor,
            ["mediaKey"] = Hash, ["bytes"] = "123", ["sha256"] = Hash };
        var backup = Payload("backup", device.ToJsonString());
        backup["destination"] = Destination();
        Assert.AreEqual(OriginalActionKind.Backup, harness.Read(backup).Action);
        backup["source"]!["actorId"] = Asset;
        Assert.ThrowsExactly<ProtocolFormatException>(() => harness.Read(backup));
        var import = Payload("import", new JsonObject { ["type"] = "import-manifest", ["manifestSha256"] = Hash, ["selectionSha256"] = Hash }.ToJsonString());
        import["destination"] = Destination();
        Assert.AreEqual(OriginalActionKind.Import, harness.Read(import).Action);
        var edit = Payload("save-edited-copy", DriveSource());
        edit["destination"] = Destination();
        Assert.ThrowsExactly<ProtocolFormatException>(() => harness.Read(edit));
        edit["recipeSha256"] = Hash;
        Assert.AreEqual(OriginalActionKind.SaveEditedCopy, harness.Read(edit).Action);
    }

    [TestMethod]
    public void SignedUnknownFieldsOverwriteActionsPathsAndWeakEtagsStillFail()
    {
        using var harness = new SignedActionHarness();
        foreach (var action in new[] { "delete-permanently", "overwrite-original", "trash-all", "move" })
            Assert.ThrowsExactly<ProtocolFormatException>(() => harness.Read(Payload(action, DriveSource())));
        var extra = Payload("trash", DriveSource());
        extra["includeNewFiles"] = true;
        Assert.ThrowsExactly<ProtocolFormatException>(() => harness.Read(extra));
        foreach (var etag in new[] { "", "W/\"weak\"", "\"header\r\ninjection\"" })
        {
            var malformed = Payload("trash", DriveSource());
            malformed["source"]!["etag"] = etag;
            Assert.ThrowsExactly<ProtocolFormatException>(() => harness.Read(malformed));
        }
        var path = Payload("trash", DriveSource());
        path["source"]!["fileId"] = "../original.jpg";
        Assert.ThrowsExactly<ProtocolFormatException>(() => harness.Read(path));
    }

    private sealed class SignedActionHarness : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public ApprovedOriginalAction Read(JsonObject payload)
        {
            var body = new JsonObject { ["format"] = "mpa", ["major"] = 1, ["minor"] = 0, ["libraryId"] = Asset,
                ["actorId"] = Actor, ["epoch"] = "1", ["sequence"] = "1", ["kind"] = "original-action",
                ["requiredCapabilities"] = new JsonArray(), ["payload"] = payload.DeepClone() };
            var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
            var authority = new TrustedProtocolActor(Guid.Parse(Asset), Guid.Parse(Actor), 1, _key.ExportSubjectPublicKeyInfo(), ["original-action"]);
            var verified = new ProtocolDocumentReader([]).VerifyAndRead(bytes, new ProtocolDocumentSigner(_key).Sign(bytes), authority);
            return new ApprovedOriginalActionReader(Guid.Parse(Actor)).Read(verified);
        }

        public void Dispose() => _key.Dispose();
    }
}
