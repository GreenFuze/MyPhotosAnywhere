using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void CurrentInterruptedInventoryRetainsRecoveryWithoutAdvancingTheOriginalReplayCursor()
    {
        using var fixture=new Fixture();var input=CurrentInventoryInput(fixture);var repository=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
        var updated=repository.RememberInterruptedInventoryPage(input.Attempt,[new("file","interrupted-removal",true,new string('a',64))],[new("interrupted-unread",9,17)]);
        Assert.AreEqual(input.Attempt.FirstUnresolvedCursor,updated.FirstUnresolvedCursor);Assert.AreEqual(input.Attempt.InventoryStartCursor,updated.InventoryStartCursor);
        Assert.AreEqual(input.Attempt.HistoryReplay!.Cursor,updated.HistoryReplay!.Cursor);Assert.AreEqual(input.Attempt.HistoryReplay.Pages,updated.HistoryReplay.Pages);
        Assert.IsFalse(updated.HistoryReplay.Active);Assert.IsFalse(updated.HistoryReplay.AtEnd);Assert.IsNull(updated.Completed);
        Assert.IsTrue(repository.KnownHistory(input.Context).Single().RecoveryRequired);Assert.IsTrue(repository.KnownIdentities(input.Context).Contains("interrupted-unread"));
        Assert.AreEqual(input.Legacy,repository.LastCheckpoint());Assert.IsNull(repository.ReadCurrentInventory());
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(()=>repository.RememberInterruptedInventoryPage(input.Attempt,[new("file","other",true,new string('b',64))],[]));
        Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void CurrentInterruptedInventoryLastHashFailureRollsBackHistoryCandidatesAndAttemptTogether()
    {
        using var fixture=new Fixture();var input=CurrentInventoryInput(fixture);var repository=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
        var known=repository.KnownIdentities(input.Context).ToArray();
        fixture.Space.Available=1;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(()=>repository.RememberInterruptedInventoryPage(input.Attempt,[new("file","interrupted-removal",true,new string('a',64))],[new("interrupted-unread",9,17)]));
        fixture.Space.Available=20*StorageBudgetManager.Gibibyte;
        fixture.Sql("CREATE TRIGGER fail_interrupted_inventory BEFORE INSERT ON processor_meta WHEN NEW.key='workspace_observation_attempt_v1:sha256' BEGIN SELECT RAISE(ABORT,'interrupted final hash'); END");
        Assert.ThrowsExactly<SqliteException>(()=>repository.RememberInterruptedInventoryPage(input.Attempt,[new("file","interrupted-removal",true,new string('a',64))],[new("interrupted-unread",9,17)]));
        Assert.AreEqual(input.Attempt,repository.LastAttempt());Assert.AreEqual(0,repository.KnownHistory(input.Context).Count);
        CollectionAssert.AreEqual(known,repository.KnownIdentities(input.Context).ToArray());Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
        fixture.Sql("DROP TRIGGER fail_interrupted_inventory");
        _=repository.RememberInterruptedInventoryPage(input.Attempt,[new("file","interrupted-removal",true,new string('a',64))],[new("interrupted-unread",9,17)]);
        Assert.IsTrue(repository.KnownHistory(input.Context).Single().RecoveryRequired);
    }
}
