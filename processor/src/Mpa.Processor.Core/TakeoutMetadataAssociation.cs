using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Association minted by the held, verified native input index. A parsed
/// title remains a metadata hint; it cannot mint a photo/person or merge originals.</summary>
public sealed class TakeoutMetadataAssociation
{
    private readonly TakeoutAssociationCatalog _owner;
    public string Status{get;}
    public TakeoutCapturedEntry Sidecar{get;}
    public TakeoutCapturedEntry? Media{get;}
    internal string Sha256{get;}
    internal long Bytes{get;}
    internal TakeoutMetadataAssociation(TakeoutAssociationCatalog owner,TakeoutCapturedEntry sidecar,TakeoutAssociation result,TakeoutMetadataDocument document)
    {_owner=owner;Sidecar=sidecar;Status=result.Status;Media=result.Media;Sha256=document.SourceSha256;Bytes=document.CopyRawSource().LongLength;}
    internal void AssertSelection(TakeoutCompletedInventory completed)=>_owner.AssertCompletedSelection(completed);
}

public sealed partial class TakeoutAssociationCatalog
{
    public TakeoutMetadataAssociation AssociateMetadata(string sidecarKey,TakeoutMetadataDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);var result=Associate(sidecarKey,document.Title);
        Enter();try
        {
            RequireSealed();using var query=Command("SELECT payload,sha256,folder,name,fold_folder,fold_name,kind,key,part_id FROM entry WHERE key=$key",("$key",sidecarKey));
            using var row=query.ExecuteReader();if(!row.Read())throw new InvalidDataException("The associated sidecar disappeared from its held index.");
            var entry=ReadRow(row);if(entry.Kind!=TakeoutEntryKind.JsonSidecar)throw new InvalidDataException("Metadata association requires its actual sidecar entry.");
            return new(this,entry,result,document);
        }finally{Exit();}
    }
}
