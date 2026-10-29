using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

// Pure formatting of already verified inputs. Neither this descriptor nor its
// digest grants current authority. Keep the original v1 all-string wire values,
// including counters beyond JavaScript's exact integer range.
internal sealed record LocalPublicationAuthorityDescriptor(Guid LibraryId,Guid ControllerId,long ControllerEpoch,string ControllerKeyId,
    Guid PublisherId,long PublisherEpoch,string PublisherKeyId,long PublisherSequence,string PublisherPolicySha256,
    long SourceSequence,string SourcePolicySha256,string AccountId,string FolderId,long ConfigurationRevision)
{
    public string Sha256()
    {
        var bytes=new CanonicalJson(8192).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format="mpa-local-publication-authority",major=1,libraryId=LibraryId.ToString("D"),
            controllerId=ControllerId.ToString("D"),controllerEpoch=ControllerEpoch.ToString(CultureInfo.InvariantCulture),controllerKeyId=ControllerKeyId,
            publisherId=PublisherId.ToString("D"),publisherEpoch=PublisherEpoch.ToString(CultureInfo.InvariantCulture),publisherKeyId=PublisherKeyId,
            publisherSequence=PublisherSequence.ToString(CultureInfo.InvariantCulture),publisherPolicySha256=PublisherPolicySha256,
            sourceSequence=SourceSequence.ToString(CultureInfo.InvariantCulture),sourcePolicySha256=SourcePolicySha256,
            accountId=AccountId,folderId=FolderId,configurationRevision=ConfigurationRevision.ToString(CultureInfo.InvariantCulture)
        }));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
