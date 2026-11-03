using Mpa.Processor.Protocol;

namespace Mpa.Processor.WorkerHost;

internal sealed class TakeoutMetadataAnalysis : InstalledSnapshotAnalysis
{
    public override string Id => "mpa.takeout-metadata";
    public override string Version => "0.6.0";
    public override string RecipeSha256 => TakeoutMetadataDocument.RecipeSha256;
    public override string[] Capabilities => ["takeout-media-metadata", "exact-sidecar-provenance"];
    public override long MaximumInputBytes => TakeoutMetadataDocument.MaximumSourceBytes;
    public override async Task<byte[]> AnalyzeAsync(Stream input,ArtifactDescriptor source,CancellationToken cancellationToken)
    {
        if(source.Bytes>MaximumInputBytes) throw new SnapshotMetadataIssueException("metadata-too-large","Takeout sidecar exceeds its bounded metadata stage.");
        var raw=new byte[checked((int)source.Bytes)];await input.ReadExactlyAsync(raw,cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result=TakeoutMetadataDocument.ParseSource(raw).EncodeArtifact();cancellationToken.ThrowIfCancellationRequested();return result;
        }
        catch(TakeoutMetadataIssueException error) {throw new SnapshotMetadataIssueException(error.Code,error.Message);}
    }
}
