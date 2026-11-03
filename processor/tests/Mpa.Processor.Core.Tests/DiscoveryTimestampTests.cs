using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class DiscoveryTimestampTests
{
    [TestMethod]
    public void ExplicitOffsetsAgreeWhileUnqualifiedExifRemainsAnUnmodifiedWallTime()
    {
        var utc = DiscoveryTimestampReader.Read("2026-10-20T12:30:00Z"); var offset = DiscoveryTimestampReader.Read("2026-10-20T15:30:00+03:00");
        Assert.IsNotNull(utc.UtcMilliseconds); Assert.AreEqual(utc.UtcMilliseconds, offset.UtcMilliseconds);
        foreach (var raw in new[] { "2026:10:20 15:30:00", "2026-10-20T15:30:00", "invalid date", "2026-02-30T12:30:00Z" })
        { var read = DiscoveryTimestampReader.Read(raw); Assert.AreEqual(raw, read.Raw); Assert.IsNull(read.UtcMilliseconds); }
        Assert.IsNull(DiscoveryTimestampReader.Read(null).Raw); Assert.IsNull(DiscoveryTimestampReader.Read(null).UtcMilliseconds);
    }
    [TestMethod]
    public void TimestampParsingDoesNotDependOnTheComputersCurrentCultureOrAssumeItsTimezone()
    {
        var prior = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            var raw = "1970-01-01T00:00:00.123Z";
            foreach (var culture in new[] { "en-US", "he-IL", "ar-SA" })
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
                Assert.AreEqual(123L, DiscoveryTimestampReader.Read(raw).UtcMilliseconds); Assert.IsNull(DiscoveryTimestampReader.Read("1970:01:01 00:00:00").UtcMilliseconds);
            }
            Assert.ThrowsExactly<ArgumentException>(() => DiscoveryTimestampReader.Read("2026-10-20T12:30:00Z\n"));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = prior; }
    }
}
