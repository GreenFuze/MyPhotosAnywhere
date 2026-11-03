using System.Text.Json;

namespace Mpa.Processor.Core;

public sealed partial class SourceDiscoveryRepository
{
    public bool TryExcludeManagedWorkspace(SourceDiscoveryWork work)
    {
        ValidateCurrent(work);
        if (!ManagedWorkspaceBoundary.Capture(_database).ContainsRoot(work.Folder.FolderId)) return false;
        if (Checkpoint(work).ObservedItems != 0)
            throw new InvalidDataException("The newly reserved metadata workspace already has source observations. Preserve them and reconcile before publication.");
        CompleteWorkspaceExclusion(work.Lease, work.Scope, work.Folder); return true;
    }
    public bool TryExcludeManagedWorkspace(SourceDiscoveryRepair repair)
    {
        ValidateCurrent(repair); var boundary = ManagedWorkspaceBoundary.Capture(_database);
        if (!boundary.ContainsRoot(repair.Folder.FolderId) && (repair.FileId is null || !boundary.ContainsRoot(repair.FileId))) return false;
        if (boundary.ContainsRoot(repair.Folder.FolderId))
        {
            var parent = Item(repair.Scope, repair.Folder);
            using var command = _database.Connection.CreateCommand();
            command.CommandText = "SELECT length(checkpoint),checkpoint FROM item_stage WHERE job_id=$job AND item_id=$item AND name=$stage";
            command.Parameters.AddWithValue("$job", repair.Lease.JobId.ToString("D")); command.Parameters.AddWithValue("$item", parent.Id.ToString("D")); command.Parameters.AddWithValue("$stage", Stage);
            using var reader = command.ExecuteReader();
            if (!reader.Read() || !reader.IsDBNull(0) && reader.GetInt64(0) > 32768) throw new InvalidDataException("The workspace repair lost its bounded source-folder checkpoint.");
            if (_journal.Checkpoint(reader.IsDBNull(1) ? null : reader.GetString(1)).ObservedItems != 0)
                throw new InvalidDataException("The workspace repair overlaps prior source observations. Preserve them and reconcile before publication.");
        }
        CompleteWorkspaceExclusion(repair.Lease, repair.Scope, repair.Folder); return true;
    }
    private void CompleteWorkspaceExclusion(StageLease lease, SourceDiscoveryScope scope, SourceDiscoveryFolder folder) =>
        _jobs.Complete(lease, _codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa-source-excluded-workspace-1",
            sourceId = scope.SourceId.ToString("D"), folderId = folder.FolderId, reason = "managed-metadata-workspace" })));
}
