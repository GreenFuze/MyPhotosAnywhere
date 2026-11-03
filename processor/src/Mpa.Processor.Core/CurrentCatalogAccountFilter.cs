namespace Mpa.Processor.Core;

/// <summary>Immutable historical query input only; never Drive access, source
/// approval, publication authority or a current-observation witness.</summary>
public sealed record CurrentCatalogAccountFilter
{
    public string AccountId {get;}
    public CurrentCatalogAccountFilter(string accountId)
    {
        if(accountId is not {Length:>=1 and <=256} || accountId.Any(c=>!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("A bounded opaque Drive account identity is required.",nameof(accountId));
        AccountId=accountId;
    }
}

public sealed class CurrentCatalogAccountPagingUnavailableException() : IOException(
    "This retained catalog needs its account index rebuilt. Refresh the library when online; All accounts and exact photo lookups remain available.");
