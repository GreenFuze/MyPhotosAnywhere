using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.WorkerHost;

/// <summary>Fixed installed analyses only. An input lease is read-only; analyses
/// return bounded bytes, never paths, credentials, Drive actions or human edits.</summary>
internal abstract class InstalledSnapshotAnalysis
{
    public abstract string Id { get; }
    public abstract string Version { get; }
    public abstract string RecipeSha256 { get; }
    public abstract string[] Capabilities { get; }
    public abstract long MaximumInputBytes { get; }
    public abstract Task<byte[]> AnalyzeAsync(Stream input, ArtifactDescriptor source, CancellationToken cancellationToken);

    public static InstalledSnapshotAnalysis Select(string id, string version)
    {
        InstalledSnapshotAnalysis analysis = id switch
        {
            "mpa.byte-facts" => new VerifiedByteFactsAnalysis(),
            "mpa.takeout-metadata" => new TakeoutMetadataAnalysis(),
            _ => throw new ProtocolCompatibilityException("Requested analysis is not installed in this first-party worker.")
        };
        if (version != analysis.Version) throw new ProtocolCompatibilityException("Installed analysis version differs from the request.");
        return analysis;
    }

    private sealed class VerifiedByteFactsAnalysis : InstalledSnapshotAnalysis
    {
        private static readonly string Recipe = Convert.ToHexStringLower(SHA256.HashData("mpa.byte-facts:1:verified-snapshot"u8));
        public override string Id => "mpa.byte-facts";
        public override string Version => "0.6.0";
        public override string RecipeSha256 => Recipe;
        public override string[] Capabilities => ["verified-byte-facts"];
        public override long MaximumInputBytes => long.MaxValue;
        public override Task<byte[]> AnalyzeAsync(Stream input, ArtifactDescriptor source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CanonicalJson(64 * 1024).Encode(JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = "mpa-byte-facts", major = 1, sourceBytes = source.Bytes.ToString(CultureInfo.InvariantCulture),
                sourceSha256 = source.Sha256, recipeSha256 = Recipe
            })));
        }
    }
}

internal sealed class SnapshotMetadataIssueException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
