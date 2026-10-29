using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    private sealed partial class LibraryFactory
    {
        private sealed partial class Connection : IGoogleWorkspaceAdmittedPublication
        {
            public async Task<DriveWorkspaceObject> CreateAdmittedWorkspaceObjectAsync(string folderId,string allocatedId,string mimeType,
                ReadOnlyMemory<byte> bytes,string sha256,Func<CancellationToken,Task> admit,CancellationToken cancellationToken)
            {
                await admit(cancellationToken).ConfigureAwait(false);
                return await CreateWorkspaceObjectAsync(folderId,allocatedId,mimeType,bytes,sha256,cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
