using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class ProtocolSigningJournalTests
{
    [TestMethod]
    public void FreshBootstrapRegistersBothActorsAtomicallyAndNeverReinitializesAMissingCounter()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); Assert.AreEqual(2L, fixture.Scalar("SELECT count(*) FROM signing_actor"));
        Assert.AreEqual(fixture.Pin, fixture.Bootstrap());
        fixture.Sql("DELETE FROM signing_actor WHERE role='publisher'");
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Bootstrap());
        Assert.AreEqual(1L, fixture.Scalar("SELECT count(*) FROM signing_actor"));
    }

    [TestMethod]
    public void ReservedBodySequenceAndExactSealedEnvelopeSurviveRestart()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); var id = Guid.NewGuid(); ProtocolSigningIntent sealedIntent;
        using (var fixture = new Fixture(root.Path))
        {
            fixture.Bootstrap(); var owned = fixture.Body(1); var prepared = fixture.Journal.Reserve(id, fixture.ControllerId, 1, Fixture.Kind, _ => owned);
            owned[0] = 0; Assert.AreEqual(1L, prepared.Sequence); Assert.IsNull(prepared.Envelope);
            sealedIntent = fixture.Journal.Seal(id, prepared.BodySha256, new ProtocolDocumentSigner(fixture.Controller).Sign(prepared.Body.Span));
            Assert.IsNotNull(sealedIntent.Envelope);
        }
        using var database = new ProcessorStateDatabase(root.Path); var read = new ProtocolSigningJournal(database, new(new Space())).Read(id)!;
        Assert.AreEqual(sealedIntent.Sequence, read.Sequence); Assert.AreEqual(sealedIntent.BodySha256, read.BodySha256);
        Assert.AreEqual(sealedIntent.EnvelopeSha256, read.EnvelopeSha256); CollectionAssert.AreEqual(sealedIntent.Envelope!.Value.ToArray(), read.Envelope!.Value.ToArray());
    }

    [TestMethod]
    public void DuplicateIntentCannotChangeBodyKindActorOrReservedSequence()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var id = Guid.NewGuid(); var first = fixture.Reserve(id);
        Assert.AreEqual(1L, fixture.Reserve(id).Sequence);
        Assert.ThrowsExactly<ProtocolSigningConflictException>(() => fixture.Journal.Reserve(id, fixture.ControllerId, 1, Fixture.Kind, n => fixture.Body(n, note: "changed")));
        Assert.ThrowsExactly<ProtocolSigningConflictException>(() => fixture.Journal.Reserve(id, fixture.ControllerId, 1, "library-publisher-authorization", n => fixture.Body(n, kind: "library-publisher-authorization")));
        Assert.ThrowsExactly<ProtocolSigningConflictException>(() => fixture.Journal.Reserve(id, fixture.PublisherId, 1, "catalog-commit", n => fixture.Body(n, controller: false)));
        Assert.AreEqual(first.BodySha256, fixture.Journal.Read(id)!.BodySha256); Assert.AreEqual(2L, fixture.Reserve(Guid.NewGuid()).Sequence);
    }

    [TestMethod]
    public void InvalidScopeKindSequenceAndNonCanonicalBodiesNeverBurnACounter()
    {
        using var fixture = new Fixture(); fixture.Bootstrap();
        Assert.ThrowsExactly<ProtocolFormatException>(() => fixture.Journal.Reserve(Guid.NewGuid(), fixture.ControllerId, 1, "user-operation", n => fixture.Body(n, kind: "user-operation")));
        Assert.ThrowsExactly<ProtocolFormatException>(() => fixture.Journal.Reserve(Guid.NewGuid(), fixture.ControllerId, 1, Fixture.Kind, n => fixture.Body(n + 1)));
        Assert.ThrowsExactly<ProtocolFormatException>(() => fixture.Journal.Reserve(Guid.NewGuid(), fixture.ControllerId, 1, Fixture.Kind, n => fixture.Body(n, library: Guid.NewGuid())));
        Assert.ThrowsExactly<ProtocolFormatException>(() => fixture.Journal.Reserve(Guid.NewGuid(), fixture.ControllerId, 1, Fixture.Kind, _ => " {\"format\":\"mpa\"}"u8.ToArray()));
        Assert.AreEqual(0L, fixture.Scalar("SELECT count(*) FROM signing_intent")); Assert.AreEqual(1L, fixture.Reserve(Guid.NewGuid()).Sequence);
    }

    [TestMethod]
    public void InterruptedUnsealedWorkResumesCapturedBodyWithoutRemintingIdentity()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var id = Guid.NewGuid(); var prepared = fixture.Reserve(id);
        var resumed = fixture.Reserve(id); Assert.AreEqual(prepared.Id, resumed.Id); Assert.AreEqual(prepared.Sequence, resumed.Sequence);
        CollectionAssert.AreEqual(prepared.Body.ToArray(), resumed.Body.ToArray()); Assert.IsNull(resumed.Envelope);
        var sealedIntent = fixture.Journal.Seal(id, resumed.BodySha256, new ProtocolDocumentSigner(fixture.Controller).Sign(resumed.Body.Span));
        Assert.AreEqual(1L, sealedIntent.Sequence); Assert.AreEqual(2L, fixture.Reserve(Guid.NewGuid()).Sequence);
    }

    [TestMethod]
    public void SealingRequiresTheRegisteredKeyAndExactReservedBodyAndPreservesFirstSignatureBytes()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var prepared = fixture.Reserve(Guid.NewGuid());
        Assert.ThrowsExactly<ProtocolFormatException>(() => fixture.Journal.Seal(prepared.Id, prepared.BodySha256, new ProtocolDocumentSigner(fixture.Publisher).Sign(prepared.Body.Span)));
        Assert.ThrowsExactly<ProtocolSigningConflictException>(() => fixture.Journal.Seal(prepared.Id, new string('0', 64), new ProtocolDocumentSigner(fixture.Controller).Sign(prepared.Body.Span)));
        Assert.ThrowsExactly<ProtocolFormatException>(() => fixture.Journal.Seal(prepared.Id, prepared.BodySha256, new ProtocolDocumentSigner(fixture.Controller).Sign(fixture.Body(1, note: "other"))));
        Assert.IsNull(fixture.Journal.Read(prepared.Id)!.Envelope);
        var signature = new ProtocolDocumentSigner(fixture.Controller).Sign(prepared.Body.Span);
        var sealedIntent = fixture.Journal.Seal(prepared.Id, prepared.BodySha256, signature);
        Assert.AreEqual(sealedIntent.EnvelopeSha256, fixture.Journal.Seal(prepared.Id, prepared.BodySha256, signature).EnvelopeSha256);
        var alternate = new ProtocolDocumentSigner(fixture.Controller).Sign(prepared.Body.Span);
        var alternateBytes = new SignedProtocolDocuments([]).Encode(prepared.Body.Span, alternate);
        if (!alternateBytes.AsSpan().SequenceEqual(sealedIntent.Envelope!.Value.Span))
            Assert.ThrowsExactly<ProtocolSigningConflictException>(() => fixture.Journal.Seal(prepared.Id, prepared.BodySha256, alternate));
        Assert.AreEqual(sealedIntent.EnvelopeSha256, fixture.Journal.Read(prepared.Id)!.EnvelopeSha256);
    }

    [TestMethod]
    public void DamagedBodiesEnvelopesAndCounterHistoryFailInsteadOfResettingOrResigning()
    {
        foreach (var sql in new[] { "UPDATE signing_intent SET body=X'00'", "UPDATE signing_intent SET body_hash='corrupt'", "UPDATE signing_intent SET envelope_hash='corrupt'",
            "UPDATE signing_actor SET sequence=0 WHERE role='controller'", "UPDATE signing_actor SET public_key=X'00' WHERE role='controller'" })
        {
            using var fixture = new Fixture(); fixture.Bootstrap(); var intent = fixture.Reserve(Guid.NewGuid());
            fixture.Journal.Seal(intent.Id, intent.BodySha256, new ProtocolDocumentSigner(fixture.Controller).Sign(intent.Body.Span)); fixture.Sql(sql);
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Journal.Read(intent.Id));
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Reserve(intent.Id));
            Assert.AreEqual(1L, fixture.Scalar("SELECT count(*) FROM signing_intent"));
        }
    }

    [TestMethod]
    public void LowStorageAndSqlFailureRollBackBothIntentAndCounterAndRetainUnsealedRetry()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var id = Guid.NewGuid(); fixture.Space.Available = 1;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => fixture.Reserve(id)); Assert.AreEqual(0L, fixture.Scalar("SELECT count(*) FROM signing_intent"));
        fixture.Space.Available = 20 * StorageBudgetManager.Gibibyte;
        fixture.Sql("CREATE TRIGGER fail_counter BEFORE UPDATE ON signing_actor BEGIN SELECT RAISE(ABORT,'synthetic failed counter checkpoint'); END");
        Assert.ThrowsExactly<SqliteException>(() => fixture.Reserve(id)); Assert.AreEqual(0L, fixture.Scalar("SELECT count(*) FROM signing_intent")); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        fixture.Sql("DROP TRIGGER fail_counter"); var intent = fixture.Reserve(id); Assert.AreEqual(1L, intent.Sequence);
        fixture.Sql("CREATE TRIGGER fail_seal BEFORE UPDATE ON signing_intent BEGIN SELECT RAISE(ABORT,'synthetic failed signature checkpoint'); END");
        var signature = new ProtocolDocumentSigner(fixture.Controller).Sign(intent.Body.Span);
        Assert.ThrowsExactly<SqliteException>(() => fixture.Journal.Seal(id, intent.BodySha256, signature)); Assert.IsNull(fixture.Journal.Read(id)!.Envelope);
        Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes); fixture.Sql("DROP TRIGGER fail_seal"); Assert.IsNotNull(fixture.Journal.Seal(id, intent.BodySha256, signature).Envelope);
    }

    [TestMethod]
    public void SchemaSevenUpgradePreservesOldAuthorityAndJobsWithoutGuessingSigningCounters()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); LibraryControllerPin pin; PublisherSigningRegistration publisher;
        using (var fixture = new Fixture(root.Path))
        {
            pin = fixture.Bootstrap(); publisher = fixture.PublisherRegistration;
            new DurableJobRepository(fixture.Database).Create(Guid.NewGuid(), "takeout-import", "{\"destination\":\"captured\",\"caption\":\"Human caption\"}"u8);
        }
        using (var connection = Open(root.Path))
        { using var sql = connection.CreateCommand(); sql.CommandText = "DROP TABLE signing_intent; DROP TABLE signing_actor; PRAGMA user_version=7"; sql.ExecuteNonQuery(); }
        using var database = new ProcessorStateDatabase(root.Path); var budget = new StorageBudgetManager(new Space()); var store = new LibraryAuthorityRepository(database, budget);
        Assert.AreEqual(pin, store.Controller());
        Assert.ThrowsExactly<InvalidDataException>(() => store.Bootstrap(pin.ActorId, pin.Epoch, new(pin.AccountId, pin.FolderId), 1, Convert.FromBase64String(pin.PublicKeySpki), publisher));
        using var read = Open(root.Path); using var query = read.CreateCommand(); query.CommandText = "SELECT captured_context FROM job";
        StringAssert.Contains((string)query.ExecuteScalar()!, "Human caption"); query.CommandText = "SELECT count(*) FROM signing_actor"; Assert.AreEqual(0L, query.ExecuteScalar());
        query.CommandText = "PRAGMA user_version"; Assert.AreEqual(8L, query.ExecuteScalar());
    }

    [TestMethod]
    public void BootstrapFailureCannotLeaveAControllerPinWithoutItsRegisteredActors()
    {
        using var fixture = new Fixture(); fixture.Sql("CREATE TRIGGER fail_actor BEFORE INSERT ON signing_actor WHEN NEW.role='publisher' BEGIN SELECT RAISE(ABORT,'synthetic failed actor checkpoint'); END");
        Assert.ThrowsExactly<SqliteException>(() => fixture.Bootstrap()); Assert.IsNull(new LibraryAuthorityRepository(fixture.Database, fixture.Budget).Controller());
        Assert.AreEqual(0L, fixture.Scalar("SELECT count(*) FROM signing_actor")); fixture.Sql("DROP TRIGGER fail_actor"); Assert.IsNotNull(fixture.Bootstrap());
    }

    [TestMethod]
    public void PublisherAndControllerHaveSeparateStreamsWhileControllerKindsShareOneSequence()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); Assert.AreEqual(1L, fixture.Reserve(Guid.NewGuid()).Sequence);
        Assert.AreEqual(1L, fixture.Journal.Reserve(Guid.NewGuid(), fixture.PublisherId, 1, "catalog-commit", n => fixture.Body(n, controller: false)).Sequence);
        Assert.AreEqual(2L, fixture.Journal.Reserve(Guid.NewGuid(), fixture.ControllerId, 1, "library-publisher-authorization", n => fixture.Body(n, kind: "library-publisher-authorization")).Sequence);
        Assert.AreEqual(2L, fixture.Journal.Reserve(Guid.NewGuid(), fixture.PublisherId, 1, "catalog-commit", n => fixture.Body(n, controller: false)).Sequence);
    }

    private static SqliteConnection Open(string path)
    { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(path, "processor.db"), Pooling = false }.ToString()); connection.Open(); return connection; }
    private sealed class Space : IStorageSpaceReader
    {
        public long Available { get; set; } = 20 * StorageBudgetManager.Gibibyte;
        public StorageSpaceSnapshot Read() => new(100 * StorageBudgetManager.Gibibyte, Available, 0);
    }
    private sealed class Fixture : IDisposable
    {
        public const string Kind = "library-source-configuration";
        private readonly WorkerProcessTests.WorkerTestDirectory? _root;
        private readonly string _path;
        private readonly Guid _library;
        public Guid ControllerId { get; } = Guid.NewGuid();
        public Guid PublisherId { get; } = Guid.NewGuid();
        public ECDsa Controller { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public ECDsa Publisher { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public ProcessorStateDatabase Database { get; }
        public ProtocolSigningJournal Journal { get; }
        public Space Space { get; } = new();
        public StorageBudgetManager Budget { get; }
        public LibraryControllerPin? Pin { get; private set; }
        public PublisherSigningRegistration PublisherRegistration => new(PublisherId, 1, Convert.ToBase64String(Publisher.ExportSubjectPublicKeyInfo()), Convert.ToHexStringLower(SHA256.HashData(Publisher.ExportSubjectPublicKeyInfo())));
        public Fixture(string? path = null)
        {
            if (path is null) { _root = new(); path = _root.Path; } _path = path; Database = new(path);
            _library = new ProcessorLibraryRepository(Database).GetOrCreateIdentity(); var accounts = new DriveAccountRepository(Database); var pending = accounts.Begin(Guid.NewGuid()); accounts.Promote(pending.Id, "account-A", "Synthetic", null);
            Budget = new(Space); Journal = new(Database, Budget);
        }
        public LibraryControllerPin Bootstrap() => Pin = new LibraryAuthorityRepository(Database, Budget).Bootstrap(ControllerId, 1, new("account-A", "metadata-workspace"), 1, Controller.ExportSubjectPublicKeyInfo(), PublisherRegistration);
        public ProtocolSigningIntent Reserve(Guid id) => Journal.Reserve(id, ControllerId, 1, Kind, n => Body(n));
        public byte[] Body(long sequence, bool controller = true, string? kind = null, Guid? library = null, string note = "captured") =>
            new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa", major = 1, minor = 0, libraryId = (library ?? _library).ToString("D"),
                actorId = (controller ? ControllerId : PublisherId).ToString("D"), epoch = "1", sequence = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                kind = kind ?? (controller ? Kind : "catalog-commit"), requiredCapabilities = Array.Empty<string>(), payload = new { note } }));
        public void Sql(string sql) { using var connection = Open(_path); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public object Scalar(string sql) { using var connection = Open(_path); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar()!; }
        public void Dispose() { Database.Dispose(); Controller.Dispose(); Publisher.Dispose(); _root?.Dispose(); }
    }
}
