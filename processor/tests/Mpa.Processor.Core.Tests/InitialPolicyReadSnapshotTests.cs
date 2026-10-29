using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void InitialPolicySnapshotRequiresCompleteEvidenceAndItsExactLiveTransaction()
    {
        using var fixture=new Fixture();
        using(var missing=fixture.Database.Connection.BeginTransaction())
            Assert.ThrowsExactly<InvalidOperationException>(()=>InitialPolicyReadSnapshot.Capture(fixture.Database,fixture.Budget,missing));
        var expected=CompleteControlBootstrap(fixture);var slots=Slots(fixture);var slot=slots.Prepare();
        InitialPolicyReadSnapshot snapshot;
        using(var transaction=fixture.Database.Connection.BeginTransaction())
        {
            snapshot=InitialPolicyReadSnapshot.Capture(fixture.Database,fixture.Budget,transaction);
            Assert.AreEqual(expected,snapshot.Require(fixture.Database,transaction));
            Assert.AreEqual(slot,slots.ReadIn(transaction,snapshot));transaction.Commit();
            Assert.ThrowsExactly<InvalidOperationException>(()=>snapshot.Require(fixture.Database,transaction));
        }
        using var newer=fixture.Database.Connection.BeginTransaction();
        Assert.ThrowsExactly<InvalidOperationException>(()=>slots.ReadIn(newer,snapshot));
        Assert.AreEqual(expected,InitialPolicyReadSnapshot.Capture(fixture.Database,fixture.Budget,newer).Require(fixture.Database,newer));
    }

    [TestMethod]
    public void InitialPolicySnapshotRejectsAnotherDatabaseAndForeignTransactionBeforeBorrowingEvidence()
    {
        using var first=new Fixture();using var second=new Fixture();CompleteControlBootstrap(first);CompleteControlBootstrap(second);
        using var transaction=first.Database.Connection.BeginTransaction();using var foreign=second.Database.Connection.BeginTransaction();
        var snapshot=InitialPolicyReadSnapshot.Capture(first.Database,first.Budget,transaction);
        Assert.ThrowsExactly<InvalidOperationException>(()=>InitialPolicyReadSnapshot.Capture(first.Database,first.Budget,foreign));
        Assert.ThrowsExactly<InvalidOperationException>(()=>snapshot.Require(second.Database,foreign));
        Assert.ThrowsExactly<InvalidOperationException>(()=>snapshot.Require(first.Database,foreign));
    }

    [TestMethod]
    public void InitialPolicySnapshotCannotConcealCorruptionAfterItsTransactionRetires()
    {
        using var fixture=new Fixture();CompleteControlBootstrap(fixture);var slots=Slots(fixture);slots.Prepare();InitialPolicyReadSnapshot snapshot;
        using(var transaction=fixture.Database.Connection.BeginTransaction())
        {snapshot=InitialPolicyReadSnapshot.Capture(fixture.Database,fixture.Budget,transaction);transaction.Rollback();}
        fixture.Sql("DELETE FROM processor_meta WHERE key='initial_library_publication_complete_v1:sha256'");
        using var fresh=fixture.Database.Connection.BeginTransaction();
        Assert.ThrowsExactly<InvalidOperationException>(()=>slots.ReadIn(fresh,snapshot));
        Assert.ThrowsExactly<InvalidDataException>(()=>InitialPolicyReadSnapshot.Capture(fixture.Database,fixture.Budget,fresh));
    }
}
