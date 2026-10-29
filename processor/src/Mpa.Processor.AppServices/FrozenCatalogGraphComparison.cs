using System.Security.Cryptography;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Serial bounded comparison against the actual borrowed frozen file.
/// This is not graph decoding, cloud evidence, a signing plan or current authority.
/// Its caller must normally enumerate the actual graph and retain all read/fence
/// and native-file owners through the eventual atomic dependency checkpoint.</summary>
internal sealed class FrozenCatalogGraphComparison(FrozenCatalogSnapshot snapshot)
{
    private readonly FrozenCatalogSnapshot _snapshot=snapshot??throw new ArgumentNullException(nameof(snapshot));
    private readonly HashSet<string> _prefixes=new(StringComparer.Ordinal);
    private long _assets;
    private bool _complete;

    internal void Add(CatalogFactsLeaf leaf,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(leaf);token.ThrowIfCancellationRequested();
        if(_complete || leaf.Prefix!=leaf.Facts.Prefix || leaf.Prefix.Length is <2 or >8 || leaf.Prefix.Length%2!=0 ||
            leaf.Prefix.Any(c=>c is not(>='0' and <='9') and not(>='a' and <='f')) ||
            leaf.Facts.Assets.Count is <1 or >DriveCatalogFacts.MaximumAssets ||
            _prefixes.Count>=WorkspaceObservationRepository.MaximumDocuments ||
            _prefixes.Any(value=>value.StartsWith(leaf.Prefix,StringComparison.Ordinal)||leaf.Prefix.StartsWith(value,StringComparison.Ordinal)))
            throw new CatalogGenerationCaptureConflictException();
        var size=_snapshot.PartitionSize(leaf.Prefix,token);
        if(size.Count!=leaf.Facts.Assets.Count || size.Count>DriveCatalogFacts.MaximumAssets)
            throw new CatalogGenerationCaptureConflictException();
        var expected=new List<DriveCatalogAsset>(leaf.Facts.Assets.Count);Guid? after=null;
        while(true)
        {
            var page=_snapshot.ReadPage(leaf.Prefix,after,250,token);expected.AddRange(page.Assets);
            if(expected.Count>size.Count) throw new CatalogGenerationCaptureConflictException();
            if(!page.HasMore) break;
            if(page.Assets.Count==0) throw new InvalidDataException("Frozen graph comparison did not advance its exact partition.");
            after=page.Assets[^1].Id;
        }
        if(expected.Count!=size.Count) throw new CatalogGenerationCaptureConflictException();
        var codec=new DriveCatalogFacts(_snapshot.LibraryId);
        var original=codec.Encode(leaf.Prefix,expected);var actual=codec.Encode(leaf.Prefix,leaf.Facts.Assets);
        if(!CryptographicOperations.FixedTimeEquals(original,actual)) throw new CatalogGenerationCaptureConflictException();
        token.ThrowIfCancellationRequested();_prefixes.Add(leaf.Prefix);_assets=checked(_assets+expected.Count);
        if(_assets>_snapshot.Count) throw new CatalogGenerationCaptureConflictException();
    }

    internal void Complete(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(_complete || _assets!=_snapshot.Count) throw new CatalogGenerationCaptureConflictException();
        _complete=true;
    }
}
