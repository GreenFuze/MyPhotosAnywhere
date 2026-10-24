using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class DriveAssetIdentityTests
{
    [TestMethod]
    public void SharedIdentityCasesMatchIndependentReferenceBytes()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "identities.json")));
        foreach (var item in fixtures.RootElement.EnumerateArray())
        {
            var identity = new DriveAssetIdentity(Guid.Parse(item.GetProperty("libraryId").GetString()!));
            var actual = identity.Derive(item.GetProperty("accountPermissionId").GetString()!, item.GetProperty("fileId").GetString()!);
            Assert.AreEqual(item.GetProperty("assetId").GetString(), actual.ToString("D"));
        }
    }

    [TestMethod]
    public void NamesPathsEmailsAndMissingLibrariesCannotBecomeIdentity()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new DriveAssetIdentity(Guid.Empty));
        var identity = new DriveAssetIdentity(Guid.Parse("94a81164-e980-4ec1-a566-cec36475b839"));
        foreach (var value in new[] { "", "person@example.com", "folder/photo.jpg", "../path", new string('x', 257) })
            Assert.ThrowsExactly<ProtocolFormatException>(() => identity.Derive(value, "file"));
        Assert.AreNotEqual(identity.Derive("account-A", "file"), identity.Derive("account-B", "file"));
        Assert.AreNotEqual(identity.Derive("account", "File"), identity.Derive("account", "file"));
    }
}
