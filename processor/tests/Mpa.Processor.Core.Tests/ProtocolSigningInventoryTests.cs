using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class ProtocolSigningJournalTests
{
    [TestMethod]
    public void CompleteSigningInventoryIncludesIdleActorsAndUnsealedWorkWithoutRetainingPayloads()
    {
        using var fixture = new Fixture(); var library = fixture.Bootstrap().LibraryId;
        var first = fixture.Reserve(Guid.NewGuid()); var second = fixture.Reserve(Guid.NewGuid());
        first = fixture.Journal.Seal(first.Id, first.BodySha256, new ProtocolDocumentSigner(fixture.Controller).Sign(first.Body.Span));
        fixture.Space.Available = 0; var inventory = fixture.Journal.Inventory(library);
        Assert.AreEqual(2, inventory.Actors.Count); Assert.AreEqual(2, inventory.Intents.Count);
        Assert.AreEqual(0L, inventory.Actors.Single(actor => actor.ActorId == fixture.PublisherId).Sequence);
        Assert.AreEqual(2L, inventory.Actors.Single(actor => actor.ActorId == fixture.ControllerId).Sequence);
        Assert.AreEqual(first.EnvelopeSha256, inventory.Intents.Single(intent => intent.Id == first.Id).EnvelopeSha256);
        var pending = inventory.Intents.Single(intent => intent.Id == second.Id);
        Assert.IsNull(pending.EnvelopeBytes); Assert.IsNull(pending.EnvelopeSha256); Assert.AreEqual(second.Body.Length, pending.BodyBytes);
        fixture.Journal.AssertInventoryUnchanged(inventory); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void ReservingOrSealingEitherStreamInvalidatesTheExactEarlierSigningInventory()
    {
        using var fixture = new Fixture(); var library = fixture.Bootstrap().LibraryId; var before = fixture.Journal.Inventory(library);
        var intent = fixture.Journal.Reserve(Guid.NewGuid(), fixture.PublisherId, 1, "catalog-commit", n => fixture.Body(n, controller: false));
        Assert.ThrowsExactly<ProtocolSigningInventoryChangedException>(() => fixture.Journal.AssertInventoryUnchanged(before));
        var reserved = fixture.Journal.Inventory(library);
        fixture.Journal.Seal(intent.Id, intent.BodySha256, new ProtocolDocumentSigner(fixture.Publisher).Sign(intent.Body.Span));
        Assert.ThrowsExactly<ProtocolSigningInventoryChangedException>(() => fixture.Journal.AssertInventoryUnchanged(reserved));
        var sealedInventory = fixture.Journal.Inventory(library); fixture.Reserve(Guid.NewGuid());
        Assert.ThrowsExactly<ProtocolSigningInventoryChangedException>(() => fixture.Journal.AssertInventoryUnchanged(sealedInventory));
        Assert.AreEqual(intent.Id, fixture.Journal.Inventory(library).Intents.Single(item => item.ActorId == fixture.PublisherId).Id);
    }

    [TestMethod]
    public void AlteredInventoriesAndCorruptOrOrphanHistoryCannotBecomeCompleteEvidence()
    {
        using (var fixture = new Fixture())
        {
            var library = fixture.Bootstrap().LibraryId; fixture.Reserve(Guid.NewGuid()); var snapshot = fixture.Journal.Inventory(library);
            Assert.ThrowsExactly<ProtocolSigningConflictException>(() => fixture.Journal.AssertInventoryUnchanged(snapshot with { Intents = new([]) }));
            Assert.ThrowsExactly<ProtocolSigningConflictException>(() => fixture.Journal.AssertInventoryUnchanged(snapshot with { Actors = new(snapshot.Actors.Take(1).ToList()) }));
            Assert.ThrowsExactly<ProtocolSigningConflictException>(() => fixture.Journal.Inventory(Guid.NewGuid()));
            fixture.Sql("UPDATE signing_intent SET body_hash='corrupt'");
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Journal.Inventory(library));
        }
        foreach (var sql in new[] { "DELETE FROM signing_actor WHERE role='publisher'", "PRAGMA foreign_keys=OFF; DELETE FROM signing_actor", "DELETE FROM signing_intent" })
        {
            using var fixture = new Fixture(); var library = fixture.Bootstrap().LibraryId; fixture.Reserve(Guid.NewGuid()); fixture.Sql(sql);
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Journal.Inventory(library));
            Assert.AreEqual(0L, fixture.Scalar("SELECT count(*) FROM signing_intent WHERE envelope IS NOT NULL"));
        }
    }

    [TestMethod]
    public void SigningInventoryIsStableAcrossRestartAndDoesNotAdoptCloudHighWater()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); ProtocolSigningInventory captured;
        using (var fixture = new Fixture(root.Path))
        {
            var library = fixture.Bootstrap().LibraryId; var first = fixture.Reserve(Guid.NewGuid());
            fixture.Journal.Seal(first.Id, first.BodySha256, new ProtocolDocumentSigner(fixture.Controller).Sign(first.Body.Span));
            captured = fixture.Journal.Inventory(library);
            Assert.ThrowsExactly<SigningHistoryRecoveryRequiredException>(() => fixture.Journal.AssertRetainedCloudEnvelope(fixture.ControllerId, 1, Envelope(fixture, fixture.Body(10))));
            fixture.Journal.AssertInventoryUnchanged(captured);
        }
        using var database = new ProcessorStateDatabase(root.Path); var journal = new ProtocolSigningJournal(database, new(new Space()));
        var reopened = journal.Inventory(captured.LibraryId); Assert.AreEqual(captured.Sha256, reopened.Sha256);
        CollectionAssert.AreEqual(captured.Actors.ToList(), reopened.Actors.ToList()); CollectionAssert.AreEqual(captured.Intents.ToList(), reopened.Intents.ToList());
        journal.AssertInventoryUnchanged(captured);
    }

    [TestMethod]
    public void SigningInventoryFramesMoreThanTheProtocolArrayBoundAndRefusesTruncation()
    {
        using var fixture = new Fixture(); var library = fixture.Bootstrap().LibraryId;
        for (var i = 0; i < 1001; i++) fixture.Reserve(Guid.NewGuid());
        var inventory = fixture.Journal.Inventory(library); Assert.AreEqual(1001, inventory.Intents.Count);
        Assert.AreEqual(1001L, inventory.Actors.Single(actor => actor.ActorId == fixture.ControllerId).Sequence);
        fixture.Journal.AssertInventoryUnchanged(inventory);
        // Corrupt bodies above the complete bound must never be silently ignored by
        // taking a prefix. The bound is checked before any such body is consumed.
        fixture.Sql("""
            WITH RECURSIVE more(n) AS (SELECT 1002 UNION ALL SELECT n+1 FROM more WHERE n<4097)
            INSERT INTO signing_intent(id,actor_id,epoch,sequence,kind,body,body_hash)
            SELECT printf('00000000-0000-0000-0000-%012x',n),actor_id,epoch,n,kind,body,body_hash
              FROM more JOIN signing_intent ON sequence=1;
            UPDATE signing_actor SET sequence=4097 WHERE role='controller';
            """);
        var failure = Assert.ThrowsExactly<InvalidDataException>(() => fixture.Journal.Inventory(library));
        StringAssert.Contains(failure.Message, "complete reconciliation bound");
        Assert.AreEqual(4097L, fixture.Scalar("SELECT count(*) FROM signing_intent"));
    }

    [TestMethod]
    public void AnUnconfiguredLibraryCanHaveNoSigningHistoryButDanglingRowsAreNeverIgnored()
    {
        using var fixture = new Fixture();
        var library = new ProcessorLibraryRepository(fixture.Database).GetOrCreateIdentity(); var empty = fixture.Journal.Inventory(library);
        Assert.HasCount(0, empty.Actors); Assert.HasCount(0, empty.Intents); fixture.Journal.AssertInventoryUnchanged(empty);
        fixture.Bootstrap(); Assert.ThrowsExactly<ProtocolSigningInventoryChangedException>(() => fixture.Journal.AssertInventoryUnchanged(empty));
        Assert.HasCount(2, fixture.Journal.Inventory(library).Actors);
    }
}
