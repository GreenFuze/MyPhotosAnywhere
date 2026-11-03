using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    private sealed partial class LibraryFactory
    {
        private sealed partial class Connection : IGoogleWorkspaceObservedAllocation
        {
            public async Task<string> ObserveWorkspaceObjectIdAllocationAsync(IGoogleWorkspaceAllocationObserver observer,
                Func<CancellationToken,Task> admit,CancellationToken cancellationToken)
            {
                await admit(cancellationToken).ConfigureAwait(false);
                var id=await AllocateWorkspaceObjectIdAsync(cancellationToken).ConfigureAwait(false);
                await observer.RememberAllocatedIdAsync(new("permission-A",id)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();return id;
            }
        }
    }
}
