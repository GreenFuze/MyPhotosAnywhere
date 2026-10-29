using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void BorrowedPolicySelectionsHoldBothReservationsThroughTheSameCommit()
    {
        using var fixture=new Fixture();fixture.PrepareLocalPublication();
        using(var transaction=fixture.Database.Connection.BeginTransaction())
        {
            using var publisher=fixture.Store.SelectPublisherPolicyIn(fixture.Policy(3),transaction);
            var first=fixture.Budget.Status().ReservedBytes;Assert.IsGreaterThan(0L,first);
            using var sources=fixture.Store.SelectSourceConfigurationIn(fixture.SourcePolicy(4,2,sourceRevision:1),transaction);
            Assert.IsGreaterThan(first,fixture.Budget.Status().ReservedBytes);
            publisher.Recheck();sources.Recheck();transaction.Commit();
            Assert.IsGreaterThan(first,fixture.Budget.Status().ReservedBytes);
        }
        Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
        Assert.AreEqual(3L,fixture.Store.CurrentPublisherPolicy()!.Sequence);Assert.AreEqual(4L,fixture.Store.CurrentSourceConfiguration()!.Sequence);
    }

    [TestMethod]
    public void BorrowedPolicySelectionFailureRollsBackBothAndReleasesAdmission()
    {
        using var fixture=new Fixture();fixture.PrepareLocalPublication();
        fixture.Sql("CREATE TRIGGER fail_borrowed BEFORE UPDATE ON processor_meta WHEN NEW.key='current_source_configuration_v1' BEGIN SELECT RAISE(ABORT,'final borrowed pointer failed'); END");
        Assert.ThrowsExactly<SqliteException>(()=>
        {
            using var transaction=fixture.Database.Connection.BeginTransaction();
            using var publisher=fixture.Store.SelectPublisherPolicyIn(fixture.Policy(3,revoked:true),transaction);
            using var sources=fixture.Store.SelectSourceConfigurationIn(fixture.SourcePolicy(4,2,sourceRevision:1),transaction);
            transaction.Commit();
        });
        Assert.AreEqual(1L,fixture.Store.CurrentPublisherPolicy()!.Sequence);Assert.AreEqual(2L,fixture.Store.CurrentSourceConfiguration()!.Sequence);
        Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
        Assert.AreEqual(0L,fixture.Scalar("SELECT count(*) FROM processor_meta WHERE key IN ('controller_document_v1:3','controller_source_document_v1:4')"));
    }

    [TestMethod]
    public void HumanSourceApprovalRemainsReadableAfterRevocationWithoutGrantingPublication()
    {
        using var fixture=new Fixture();fixture.PrepareLocalPublication();fixture.Store.SelectPublisherPolicy(fixture.Policy(3,revoked:true));
        var configuration=fixture.Store.CurrentSourceConfiguration()!.Configuration;
        using(var transaction=fixture.Database.Connection.BeginTransaction())
        {Assert.AreEqual(2L,fixture.Store.AssertHumanSources(configuration,transaction));transaction.Commit();}
        Assert.ThrowsExactly<CatalogPublicationNotAuthorizedException>(()=>fixture.Store.CaptureLocalPublicationAuthority());
        fixture.DisablePublicationSource();
        using var changed=fixture.Database.Connection.BeginTransaction();
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(()=>fixture.Store.AssertHumanSources(configuration,changed));
    }
}
