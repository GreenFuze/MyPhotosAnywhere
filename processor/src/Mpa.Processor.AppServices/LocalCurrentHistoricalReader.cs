using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentStagePool
{
    /// <summary>Reopen independently verified historical canonical content without
    /// reading Drive or manufacturing a fresh Root capture. A changed cloud Root or
    /// disconnected account cannot turn old content into current authority.</summary>
    public Task<LocalHistoricalCatalog?> OpenHistoricalAsync(CancellationToken cancellationToken)=>OperationAsync(async token=>
    {
        SweepClosed();
        var captured=await _state.ExecuteAsync(database=>
        {
            token.ThrowIfCancellationRequested();var selected=new CurrentCatalogActivationRepository(database,_budget).Read();
            if(selected is null) return ((CurrentCatalogActivation Selection,Prepared Prepared)?)null;
            var claims=new CurrentCatalogStageJournal(database,_budget).ReadAll();
            var claim=claims.SingleOrDefault(value=>value.Id==selected.StageId);
            if(claim is null || claim.Phase!="sealed" || claim.Descriptor!=selected.Index) throw new InvalidDataException("Historical selection lost its exact sealed claim.");
            return (selected,new Prepared(claim,claims,database.StateDirectory,database.InstallationId));
        },token).ConfigureAwait(false);
        if(captured is null) return null;
        var (selection,prepared)=captured.Value;
        if(_stages.ContainsKey(selection.StageId)) throw new InvalidOperationException("Close the same canonical index before reopening its historical reader.");
        await RefreshAreaAsync(prepared,token).ConfigureAwait(false);
        var index=await Task.Run(()=>_area!.Open(prepared.Claim,_budget,token),CancellationToken.None).ConfigureAwait(false);
        var historical=new LocalHistoricalCatalog(selection,index);_stages.Add(selection.StageId,historical);
        try
        {
            await _state.ExecuteAsync(database=>
            {
                token.ThrowIfCancellationRequested();var actual=new CurrentCatalogStageJournal(database,_budget).ReadAll().SingleOrDefault(claim=>claim.Id==selection.StageId);
                if(actual!=prepared.Claim) throw new InvalidDataException("Historical claim changed while its native lease was opening.");return true;
            },token).ConfigureAwait(false);
            Check(token);return historical;
        }
        catch {await historical.DisposeAsync().ConfigureAwait(false);throw;}
    },cancellationToken);
}
