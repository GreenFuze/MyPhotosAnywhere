using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class DriveCatalogFactsTests
{
    private static readonly Guid Library = Guid.Parse("94a81164-e980-4ec1-a566-cec36475b839");
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "drive-catalog-facts.json")));
    private static byte[] Canonical(JsonElement value) => new CanonicalJson().Encode(Encoding.UTF8.GetBytes(value.GetRawText()));

    [TestMethod]
    public void BothConsumersPreserveAccountIdentityUnknownDatesAndLiteralMetadata()
    {
        using var fixture = Fixture(); var root = fixture.RootElement; var reader = new DriveCatalogFacts(Library);
        var bytes = Canonical(root.GetProperty("valid")); var prefix = root.GetProperty("prefix").GetString()!;
        var decoded = reader.Read(bytes, prefix); Assert.AreEqual(2, decoded.Assets.Count);
        var photo = decoded.Assets.Single(item => item.AccountId == "account-A");
        var video = decoded.Assets.Single(item => item.AccountId == "account-B");
        Assert.AreNotEqual(photo.Id, video.Id); Assert.AreEqual(photo.Name, video.Name);
        Assert.AreEqual("שלום.jpg", photo.Name); Assert.AreEqual(long.MaxValue, photo.DriveVersion);
        Assert.AreEqual(-1L, photo.EffectiveUtc); Assert.AreEqual(1L, photo.ModifiedUtc);
        Assert.AreEqual(2, photo.Sources.Count); Assert.AreEqual(3, photo.Sources[1].Depth);
        Assert.IsNull(video.CapturedUtc); Assert.IsNull(video.EffectiveUtc); Assert.IsNull(video.Width);
        Assert.AreEqual("2020:10:11 07:30:00", video.RawCapture);
        // The writer receives unordered rows/memberships but neither rewrites the DTOs
        // nor fabricates a timezone for an offsetless source string.
        var reversedSources = Array.AsReadOnly(photo.Sources.Reverse().ToArray());
        var unordered = new[] { photo with { Sources = reversedSources }, video };
        CollectionAssert.AreEqual(bytes, reader.Encode(prefix, unordered));
        Assert.AreEqual(photo.Sources[1], unordered[0].Sources[0]);
    }

    [TestMethod]
    public void BothConsumersRejectAllSharedMalformedObservedFacts()
    {
        using var fixture = Fixture(); var reader = new DriveCatalogFacts(Library);
        var cases = fixture.RootElement.GetProperty("invalid"); Assert.AreEqual(38, cases.GetArrayLength());
        foreach (var item in cases.EnumerateArray())
            Assert.ThrowsExactly<ProtocolFormatException>(() => reader.Read(Canonical(item.GetProperty("body")), item.GetProperty("prefix").GetString()!), item.GetProperty("name").GetString());
        var bytes = Canonical(fixture.RootElement.GetProperty("valid")); var prefix = fixture.RootElement.GetProperty("prefix").GetString()!;
        Assert.ThrowsExactly<ProtocolFormatException>(() => reader.Read(Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(bytes)), prefix));
        Assert.ThrowsExactly<ProtocolCompatibilityException>(() => reader.Read(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"major\":1", "\"major\":2", StringComparison.Ordinal)), prefix));
    }

    [TestMethod]
    public void WriterRejectsDuplicateAndInvalidTextRatherThanRewritingIt()
    {
        using var fixture = Fixture(); var root = fixture.RootElement; var reader = new DriveCatalogFacts(Library); var prefix = root.GetProperty("prefix").GetString()!;
        var asset = reader.Read(Canonical(root.GetProperty("valid")), prefix).Assets[0];
        Assert.ThrowsExactly<ProtocolFormatException>(() => reader.Encode(prefix, []));
        Assert.ThrowsExactly<ProtocolFormatException>(() => reader.Encode(prefix, [asset, asset]));
        Assert.ThrowsExactly<ProtocolFormatException>(() => reader.Encode(prefix, [asset with { Sources = Array.AsReadOnly(new[] { asset.Sources[0], asset.Sources[0] }) }]));
        foreach (var invalid in new[] { asset with { Name = "bad\ud800" }, asset with { RawCapture = "bad\udfff" }, asset with { MimeType = "image/\ud800" } })
            Assert.ThrowsExactly<ProtocolFormatException>(() => reader.Encode(prefix, [invalid]));
        Assert.ThrowsExactly<CatalogChunkCapacityException>(() => reader.Encode(prefix, Enumerable.Repeat(asset, DriveCatalogFacts.MaximumAssets + 1)));
    }

    [TestMethod]
    public void WriterRequestsPartitionSplitBeforeExceedingExpandedByteBudget()
    {
        var reader = new DriveCatalogFacts(Library); var identities = new DriveAssetIdentity(Library);
        var memberships = Array.AsReadOnly(Enumerable.Range(1, DriveCatalogFacts.MaximumSources)
            .Select(value => new DriveCatalogSource(Guid.Parse(value.ToString("x8", CultureInfo.InvariantCulture) + "-1111-4111-8111-111111111111"), long.MaxValue, 256)).ToArray());
        var assets = new List<DriveCatalogAsset>();
        for (var candidate = 0; candidate < 1_000_000 && assets.Count < 650; candidate++)
        {
            var file = "capacity-" + candidate.ToString(CultureInfo.InvariantCulture); var id = identities.Derive("account-A", file);
            if (id.ToString("N").StartsWith("56", StringComparison.Ordinal))
                assets.Add(new(id, "account-A", file, 1, new string('界', 4096), "image/jpeg", null, null, null, null, null, memberships));
        }
        Assert.AreEqual(650, assets.Count, "Bounded synthetic identity search must supply enough distinct records.");
        Assert.ThrowsExactly<CatalogChunkCapacityException>(() => reader.Encode("56", assets));
        Assert.AreEqual(650, assets.Count); Assert.AreEqual(4096, assets[0].Name.Length); Assert.AreSame(memberships, assets[^1].Sources);
    }
}
