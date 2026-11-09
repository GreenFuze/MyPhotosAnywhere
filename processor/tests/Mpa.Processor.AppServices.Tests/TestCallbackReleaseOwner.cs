namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    /// <summary>Release even if callback entry or an earlier assertion fails. Declare
    /// after the owning async fixture so its native/account retirement can always join.</summary>
    private sealed class TestCallbackReleaseOwner(TaskCompletionSource release) : IDisposable
    {
        private readonly TaskCompletionSource _release=release ?? throw new ArgumentNullException(nameof(release));
        public void Dispose()=>_release.TrySetResult();
    }
}
