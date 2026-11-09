using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void ObservedSourceHintsSurviveNativeReopenAndNeverSubstituteForAnActualObservationOrReceipt()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();WorkspaceObservationContext context;
        var source=MetadataSourceBytes.Capture("{\"title\":\"a.jpg\",\"unknown\":true}\r\n"u8);var opaque=MetadataSourceBytes.Capture([0xff,0,0x81]);var bundle=MetadataSourceBundle.Capture([opaque,source]);
        var observed=SourceObservation("raw-bundle-one",7,bundle);
        using(var fixture=new Fixture(root.Path))
        {
            fixture.Bootstrap();context=ObservationContext(fixture);var hints=new MetadataSourceHintRepository(fixture.Database,fixture.Budget);var observations=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
            var receipts=new DurableJobRepository(fixture.Database).PendingReceiptCount();
            Assert.ThrowsExactly<WorkspaceObservationConflictException>(()=>hints.Remember(context,observed,bundle));
            Assert.AreEqual(0,hints.Candidates(context,source.Fingerprint).Count);
            Assert.ThrowsExactly<OperationCanceledException>(()=>hints.Candidates(context,source.Fingerprint,new CancellationToken(canceled:true)));
            observations.RememberRead(context,observed);hints.Remember(context,observed,bundle);hints.Remember(context,observed,bundle);
            Assert.AreEqual(1,hints.Candidates(context,opaque.Fingerprint).Count);Assert.AreEqual(0,hints.Candidates(context,source.Fingerprint with{Bytes=source.Fingerprint.Bytes+1}).Count);
            Assert.AreEqual(receipts,new DurableJobRepository(fixture.Database).PendingReceiptCount());Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
        }
        using var database=new ProcessorStateDatabase(root.Path);var budget=new StorageBudgetManager(new Space());var recovered=new MetadataSourceHintRepository(database,budget);
        var candidate=recovered.Candidates(context,source.Fingerprint).Single();Assert.AreEqual(observed.FileId,candidate.Bundle.FileId);Assert.AreEqual(7L,candidate.MinimumVersion);Assert.AreEqual(observed.Sha256,candidate.Bundle.Sha256);
        new WorkspaceObservationRepository(database,budget).RememberRead(context,observed with{Version=8});Assert.AreEqual(8L,recovered.Candidates(context,source.Fingerprint).Single().MinimumVersion);
        recovered.Remember(context,observed with{Version=8},bundle);Assert.AreEqual(0L,budget.Status().ReservedBytes);
    }
    [TestMethod]
    public void SourceHintsRejectForeignAccessChangedBytesAndFailedWritesWhileKeepingObservedHistory()
    {
        using var fixture=new Fixture();fixture.Bootstrap();var context=ObservationContext(fixture);var observations=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);var hints=new MetadataSourceHintRepository(fixture.Database,fixture.Budget);
        var source=MetadataSourceBytes.Capture("{\"title\":\"a.jpg\"}"u8);var bundle=MetadataSourceBundle.Capture([source]);var observed=SourceObservation("raw-bundle-one",7,bundle);observations.RememberRead(context,observed);
        Assert.ThrowsExactly<InvalidDataException>(()=>hints.Remember(context,observed with{Sha256=new string('b',64)},bundle));
        Assert.ThrowsExactly<InvalidDataException>(()=>hints.Remember(context,observed with{MimeType="application/json"},bundle));
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(()=>hints.Candidates(context with{FolderId="foreign"},source.Fingerprint));
        fixture.Space.Available=1;Assert.ThrowsExactly<InsufficientStorageBudgetException>(()=>hints.Remember(context,observed,bundle));Assert.AreEqual(0,hints.Candidates(context,source.Fingerprint).Count);
        fixture.Space.Available=20*StorageBudgetManager.Gibibyte;
        fixture.Sql("CREATE TRIGGER fail_raw_hint BEFORE INSERT ON processor_meta WHEN NEW.key LIKE 'metadata-source-hint-v1:%' BEGIN SELECT RAISE(ABORT,'synthetic hint write failure'); END");
        Assert.ThrowsExactly<SqliteException>(()=>hints.Remember(context,observed,bundle));Assert.AreEqual(0,hints.Candidates(context,source.Fingerprint).Count);Assert.AreEqual(observed,observations.Known(context).Single());
        fixture.Sql("DROP TRIGGER fail_raw_hint");hints.Remember(context,observed,bundle);new DriveAccountRepository(fixture.Database).Disconnect(context.AccountId,1);
        Assert.ThrowsExactly<StaleDriveConnectionException>(()=>hints.Candidates(context,source.Fingerprint));Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }
    [TestMethod]
    [DataRow("UPDATE processor_meta SET value='{}' WHERE key LIKE 'metadata-source-hint-v1:%'")]
    [DataRow("UPDATE processor_meta SET value=json_set(value,'$.unknown',true) WHERE key LIKE 'metadata-source-hint-v1:%'")]
    [DataRow("UPDATE processor_meta SET value=json_set(value,'$.fileId','foreign-file') WHERE key LIKE 'metadata-source-hint-v1:%'")]
    [DataRow("UPDATE workspace_document_observation SET row_hash='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'")]
    public void DamagedSourceHintOrNativeObservationCannotReturnACandidate(string damage)
    {
        using var fixture=new Fixture();fixture.Bootstrap();var context=ObservationContext(fixture);var source=MetadataSourceBytes.Capture("{\"title\":\"a.jpg\"}"u8);var bundle=MetadataSourceBundle.Capture([source]);var observed=SourceObservation("raw-bundle-one",7,bundle);
        new WorkspaceObservationRepository(fixture.Database,fixture.Budget).RememberRead(context,observed);var hints=new MetadataSourceHintRepository(fixture.Database,fixture.Budget);hints.Remember(context,observed,bundle);
        fixture.Sql(damage);Assert.ThrowsExactly<InvalidDataException>(()=>hints.Candidates(context,source.Fingerprint));Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }
    private static WorkspaceDocumentObservation SourceObservation(string file,long version,MetadataSourceBundle bundle)
    {var payload=bundle.CopyPayload();return new(file,version,payload.LongLength,Convert.ToHexStringLower(SHA256.HashData(payload)),MetadataSourceBundle.MimeType);}
}
