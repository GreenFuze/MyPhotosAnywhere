using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Historical reservation/evidence only, not cloud availability or write
/// permission. The original account revision remains immutable across reconnects.</summary>
public sealed record LibraryCurrentControlSlot(Guid Id,string RootConnectionCode,long AccountRevision,string BootstrapReceiptSha256,
    string? FileId = null,long? AllocationAccountRevision = null)
{
    public LibraryConnectionDescriptor ConnectionDescriptor()
    {
        var root = LibraryConnectionDescriptor.Decode(RootConnectionCode);
        if (FileId is null) throw new InvalidOperationException("Capture the actual generated control file ID before exporting a v2 descriptor.");
        return new(root.LibraryId,root.ControllerId,root.ControllerEpoch,root.Workspace,Convert.FromBase64String(root.ControllerPublicKeySpki),FileId);
    }
}

/// <summary>One immutable local plan, then one immutable actual-ID receipt. No network,
/// ID generation, signing, upload, retargeting or original access occurs in this store.
/// Missing records stop recovery instead of silently creating another control identity.</summary>
public sealed class LibraryCurrentControlSlotRepository(ProcessorStateDatabase database,StorageBudgetManager budget)
{
    private readonly ProcessorStateDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly StorageBudgetManager _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    private const string PlanKey = "current_control_slot_v1";
    private const string PlanClaim = "current_control_slot_claim_v1";
    private const string FileKey = "current_control_file_v1";
    private const string FileClaim = "current_control_file_claim_v1";
    private const string CompletionHash = "initial_library_publication_complete_v1:sha256";

    public LibraryCurrentControlSlot? Current()
    {
        using var transaction = _database.Connection.BeginTransaction();var result = ReadIn(transaction);transaction.Commit();return result;
    }

    public LibraryCurrentControlSlot Prepare()
    {
        using var transaction = _database.Connection.BeginTransaction();var authority = new LibraryAuthorityRepository(_database,_budget);
        var pin = authority.Controller(transaction) ?? throw new InvalidOperationException("Approve the native controller before reserving a current file.");
        if (authority.InitialPublicationCompletion(transaction) is null) throw new InvalidOperationException("Complete both initial policy publications before reserving current control.");
        var actual = authority.ReadLocalPublicationAuthority(transaction);var old = ReadIn(transaction);
        if (old is not null)
        {
            if (old.AccountRevision > actual.AccountRevision) throw new StaleLibraryConfigurationException();
            transaction.Commit();return old;
        }
        var digest = Read(CompletionHash,64,transaction) ?? throw new InvalidDataException("Initial completion lost its immutable digest.");
        var plan = new LibraryCurrentControlSlot(Guid.NewGuid(),pin.ConnectionDescriptor().Encode(),actual.AccountRevision,digest);
        using var reservation = _budget.Reserve(65536);reservation.RecheckBeforeWrite(reservation.Bytes);
        Insert(PlanKey,Encode(plan),transaction);Insert(PlanKey+":sha256",Hash(Encode(plan)),transaction);Insert(PlanClaim,plan.Id.ToString("D"),transaction);
        transaction.Commit();return plan;
    }

    /// <summary>Persist an actual allocated-ID receipt even if its access owner was
    /// subsequently disconnected. This retains evidence only; publication still needs
    /// a fresh captured native/account/workspace/policy admission. A failed checkpoint
    /// must retry the same received ID while its allocation owner remains alive.</summary>
    public LibraryCurrentControlSlot CaptureAllocatedFile(LibraryCurrentControlSlot expected,string actualAccountId,long observedAccountRevision,string fileId)
    {
        ArgumentNullException.ThrowIfNull(expected);CheckFileId(fileId);
        using var transaction = _database.Connection.BeginTransaction();var old = ReadIn(transaction) ?? throw new InvalidDataException("The control reservation disappeared.");
        var root = LibraryConnectionDescriptor.Decode(old.RootConnectionCode);
        if (Base(old) != Base(expected) || actualAccountId != root.Workspace.AccountId || observedAccountRevision < old.AccountRevision ||
            fileId == "root" || fileId == root.Workspace.FolderId) throw new InvalidOperationException("The allocation receipt changed its captured control scope.");
        using (var alias = Command(transaction,"SELECT 1 FROM workspace_publication WHERE file_id=$file LIMIT 1",("$file",fileId)))
            if (alias.ExecuteScalar() is not null) throw new InvalidOperationException("A current file cannot alias an existing immutable publication.");
        if (old.FileId is not null)
        {
            if (old.FileId != fileId || old.AllocationAccountRevision != observedAccountRevision) throw new InvalidOperationException("An allocated control identity cannot be replaced or relabeled.");
            transaction.Commit();return old;
        }
        var receipt = new Allocation(old.Id,fileId,observedAccountRevision);var bytes = Encode(receipt);var digest = Hash(bytes);
        using var reservation = _budget.Reserve(65536);reservation.RecheckBeforeWrite(reservation.Bytes);
        Insert(FileKey,bytes,transaction);Insert(FileKey+":sha256",digest,transaction);Insert(FileClaim,digest,transaction);
        transaction.Commit();return old with { FileId=fileId,AllocationAccountRevision=observedAccountRevision };
    }

    // A verified completion may be borrowed only from this same serialized SQL
    // snapshot. This avoids repeating its full signing/outbox verification while
    // loading one current context; no proof is cached across transactions or I/O.
    internal LibraryCurrentControlSlot? ReadIn(SqliteTransaction transaction,InitialPolicyReadSnapshot? snapshot=null)
    {
        var planText = Read(PlanKey,4096,transaction);var planHash = Read(PlanKey+":sha256",64,transaction);var claim = Read(PlanClaim,36,transaction);
        var fileText = Read(FileKey,1024,transaction);var fileHash = Read(FileKey+":sha256",64,transaction);var fileClaim = Read(FileClaim,64,transaction);
        if (planText is null && planHash is null && claim is null)
        {
            using var later = Command(transaction,"SELECT 1 FROM signing_intent WHERE kind=$kind LIMIT 1",("$kind",LibraryCurrentControl.Kind));
            if (fileText is not null || fileHash is not null || fileClaim is not null || later.ExecuteScalar() is not null)
                throw new InvalidDataException("Control work lost its reserved identity; never allocate a replacement over it.");
            return null;
        }
        if (planText is null || planHash is null || Hash(planText) != planHash) throw new InvalidDataException("Current-control reservation is missing or corrupt.");
        try
        {
            var plan = JsonSerializer.Deserialize<LibraryCurrentControlSlot>(planText) ?? throw new InvalidDataException("Current-control reservation is missing.");
            var authority = new LibraryAuthorityRepository(_database,_budget);var pin = authority.Controller(transaction) ?? throw new InvalidDataException("Current-control reservation lost its controller.");
            if (plan.Id == Guid.Empty || claim != plan.Id.ToString("D") || plan.AccountRevision < 1 || plan.FileId is not null || plan.AllocationAccountRevision is not null ||
                Encode(plan) != planText || pin.ConnectionDescriptor().Encode() != plan.RootConnectionCode ||
                (snapshot?.Require(_database,transaction) ?? authority.InitialPublicationCompletion(transaction)) is null || Read(CompletionHash,64,transaction) != plan.BootstrapReceiptSha256)
                throw new InvalidDataException("Current-control reservation changed its immutable setup context.");
            if (fileText is null && fileHash is null && fileClaim is null)
            {
                using var later = Command(transaction,"SELECT 1 FROM signing_intent WHERE kind=$kind LIMIT 1",("$kind",LibraryCurrentControl.Kind));
                if (later.ExecuteScalar() is not null) throw new InvalidDataException("Signed control work lost its allocated file identity.");
                return plan;
            }
            if (fileText is null || fileHash is null || fileClaim != fileHash || Hash(fileText) != fileHash) throw new InvalidDataException("Actual control allocation receipt is missing or corrupt.");
            var allocation = JsonSerializer.Deserialize<Allocation>(fileText) ?? throw new InvalidDataException("Actual control allocation receipt is missing.");
            CheckFileId(allocation.FileId);
            if (allocation.SlotId != plan.Id || allocation.AccountRevision < plan.AccountRevision || allocation.FileId == "root" || allocation.FileId == pin.FolderId || Encode(allocation) != fileText)
                throw new InvalidDataException("Actual control allocation changed its captured identity or revision.");
            using (var alias = Command(transaction,"SELECT 1 FROM workspace_publication WHERE file_id=$file LIMIT 1",("$file",allocation.FileId)))
                if (alias.ExecuteScalar() is not null) throw new InvalidDataException("The reserved control identity was aliased by immutable publication work.");
            return plan with { FileId=allocation.FileId,AllocationAccountRevision=allocation.AccountRevision };
        }
        catch(Exception error) when(error is JsonException or ArgumentException or ProtocolFormatException)
        { throw new InvalidDataException("Current-control reservation cannot be decoded safely.",error); }
    }

    private static void CheckFileId(string value)
    {
        if (value is not { Length: > 0 and <= 256 } || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new ArgumentException("A concrete opaque generated Drive file identity is required.",nameof(value));
    }
    private sealed record Allocation(Guid SlotId,string FileId,long AccountRevision);
    private static LibraryCurrentControlSlot Base(LibraryCurrentControlSlot value) => value with { FileId=null,AllocationAccountRevision=null };
    private static string Encode<T>(T value) => Encoding.UTF8.GetString(new CanonicalJson(4096).Encode(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static string Hash(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private string? Read(string key,int maximum,SqliteTransaction? transaction)
    {
        using var command = Command(transaction,"SELECT length(value),value FROM processor_meta WHERE key=$key",("$key",key));using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetInt64(0) is < 1 || reader.GetInt64(0)>maximum) throw new InvalidDataException("Current-control reservation exceeds its bound.");
        return reader.GetString(1);
    }
    private void Insert(string key,string value,SqliteTransaction transaction)
    {
        using var command = Command(transaction,"INSERT INTO processor_meta(key,value) VALUES($key,$value)",("$key",key),("$value",value));
        if (command.ExecuteNonQuery()!=1) throw new InvalidOperationException("Control reservation did not persist exactly one record.");
    }
    private SqliteCommand Command(SqliteTransaction? transaction,string sql,params (string Key,object Value)[] values)
    {
        var command = _database.Connection.CreateCommand();command.Transaction=transaction;command.CommandText=sql;
        foreach(var pair in values) command.Parameters.AddWithValue(pair.Key,pair.Value);return command;
    }
}
