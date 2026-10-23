using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task SigningDuringActualChangeInspectionStopsSetupAndRetainsItsUnsealedRecoveryWork()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false); var prepared = await fixture.PreparedAsync().ConfigureAwait(false);
        await using var runtime = PublicationRuntime(fixture); var changes = fixture.Setup.Accounts.Readers.DocumentChanges!;
        var pendingId = Guid.NewGuid(); var native = fixture.Setup.Keys.PublicIdentity; var changed = false; var fatal = false;
        runtime.Failed += _ => fatal = true;
        fixture.Setup.Accounts.Readers.DocumentChanges = async (cursor, token) =>
        {
            if (!changed)
            {
                changed = true;
                await fixture.Setup.Accounts.State.ExecuteAsync(database => new ProtocolSigningJournal(database, fixture.Budget)
                    .Reserve(pendingId, native.ControllerId, native.ControllerEpoch, "library-publisher-authorization", sequence =>
                    {
                        var envelope = System.Text.Json.Nodes.JsonNode.Parse(prepared.Publisher.Payload.Span)!;
                        var body = envelope["document"]!.DeepClone(); body["sequence"] = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        return new CanonicalJson().Encode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body));
                    }), token).ConfigureAwait(false);
            }
            return await changes(cursor, token).ConfigureAwait(false);
        };
        runtime.Request(CancellationToken.None); await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.NeedsAttention, runtime.Activity.Stage); Assert.IsTrue(runtime.Activity.CanRetry);
        Assert.IsInstanceOfType<ProtocolSigningInventoryChangedException>(runtime.LastAttemptFailure); Assert.IsNull(runtime.Failure); Assert.IsFalse(fatal);
        Assert.AreEqual(0, fixture.Creates); Assert.AreEqual(0, fixture.Allocations);
        var retained = await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var journal = new ProtocolSigningJournal(database, fixture.Budget); var intent = journal.Read(pendingId)!;
            Assert.IsNull(intent.Envelope); Assert.AreEqual(3L, intent.Sequence);
            Assert.IsNull(new WorkspaceObservationRepository(database, fixture.Budget).LastAttempt()!.Completed); return intent;
        }, CancellationToken.None).ConfigureAwait(false);
        runtime.Request(CancellationToken.None); await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        Assert.IsInstanceOfType<LibraryInitialPublicationCompletionMissingException>(runtime.LastAttemptFailure);
        var after = await fixture.Setup.Accounts.State.ExecuteAsync(database => new ProtocolSigningJournal(database, fixture.Budget).Read(pendingId), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(retained.BodySha256, after!.BodySha256); Assert.AreEqual(retained.Sequence, after.Sequence); Assert.IsNull(after.Envelope);
        Assert.AreEqual(0, fixture.Creates); Assert.AreEqual(0, fixture.Allocations); Assert.IsFalse(fatal);
    }

    [TestMethod]
    public async Task ConcurrentOutboxAllocationNeedsReconciliationAndRetryPreservesItsOriginalIdentity()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false); var prepared = await fixture.PreparedAsync().ConfigureAwait(false);
        await using var runtime = PublicationRuntime(fixture); var changes = fixture.Setup.Accounts.Readers.DocumentChanges!; var changed = false; var fatal = false;
        runtime.Failed += _ => fatal = true;
        fixture.Setup.Accounts.Readers.DocumentChanges = async (cursor, token) =>
        {
            if (!changed)
            {
                changed = true;
                await fixture.Setup.Accounts.State.ExecuteAsync(database => new WorkspacePublicationRepository(database, fixture.Budget)
                    .AssignFileId(prepared.Sources.Id, prepared.Sources.Sha256, "retained-id"), token).ConfigureAwait(false);
            }
            return await changes(cursor, token).ConfigureAwait(false);
        };
        runtime.Request(CancellationToken.None); await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.NeedsAttention, runtime.Activity.Stage); Assert.IsTrue(runtime.Activity.CanRetry);
        Assert.IsInstanceOfType<WorkspacePublicationInventoryChangedException>(runtime.LastAttemptFailure); Assert.IsNull(runtime.Failure); Assert.IsFalse(fatal);
        Assert.AreEqual(0, fixture.Creates); Assert.AreEqual(0, fixture.Allocations);
        runtime.Request(CancellationToken.None); await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.PoliciesPublished, runtime.Activity.Stage); Assert.AreEqual(1, fixture.Allocations); Assert.AreEqual(2, fixture.Creates);
        var recovered = await fixture.PreparedAsync().ConfigureAwait(false); Assert.AreEqual(prepared.Plan, recovered.Plan); Assert.AreEqual("retained-id", recovered.Sources.FileId);
    }

    [TestMethod]
    public async Task SetupRuntimeNeverInfersHumanApprovalAndCancelsBeforeAdmissionWithoutCreatingAJob()
    {
        await using var setup = await SetupFixture.CreateAsync().ConfigureAwait(false);
        await using var runtime = new InitialLibraryPublicationRuntime(setup.Accounts.State, setup.Accounts.Service, setup.Keys, PublicationBudget());
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => runtime.Request(canceled.Token));
        Assert.AreEqual(InitialLibraryPublicationStage.NeedsApproval, runtime.Activity.Stage);
        runtime.Request(CancellationToken.None); await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.NeedsApproval, runtime.Activity.Stage); Assert.IsFalse(runtime.Activity.CanRetry);
        Assert.IsNull(await setup.Setup.CurrentAsync(CancellationToken.None).ConfigureAwait(false)); Assert.AreEqual(0, setup.Accounts.Readers.DocumentCalls);
    }

    [TestMethod]
    public async Task BrowserCancellationAndRepeatedRequestsDoNotOwnOrDuplicateTheAdmittedSetupJob()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false); await using var runtime = PublicationRuntime(fixture);
        using var browser = new CancellationTokenSource(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var create = fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        fixture.Setup.Accounts.Readers.WorkspaceCreate = async (folder, file, mime, bytes, hash, token) =>
        { entered.TrySetResult(); await release.Task.ConfigureAwait(false); return await create(folder, file, mime, bytes, hash, token).ConfigureAwait(false); };
        runtime.Request(browser.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        try
        {
            browser.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => runtime.WaitForCurrentAttemptAsync(browser.Token)).ConfigureAwait(false);
            Assert.AreEqual(InitialLibraryPublicationStage.Running, runtime.Activity.Stage);
            Assert.AreEqual(InitialLibraryPublicationStage.Running, runtime.Request(CancellationToken.None).Stage); Assert.AreEqual(1, fixture.Allocations);
            Assert.IsFalse(runtime.ExitConfirmed); Assert.AreEqual(0, fixture.Setup.Accounts.Readers.DisposalAttempts);
        }
        finally { release.TrySetResult(); }
        await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.PoliciesPublished, runtime.Activity.Stage); Assert.AreEqual(2, fixture.Allocations); Assert.AreEqual(2, fixture.Creates);
        Assert.IsNull(runtime.Failure); Assert.IsNull(runtime.LastAttemptFailure);
    }

    [TestMethod]
    public async Task SetupRuntimeRecoversDurablePartialWorkAfterRestartWithoutTreatingAnOperationalFailureAsAnInvariant()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false); fixture.LoseFirstReply = true;
        var failed = false;
        await using (var runtime = PublicationRuntime(fixture))
        {
            runtime.Failed += _ => failed = true; runtime.Request(CancellationToken.None);
            await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            Assert.AreEqual(InitialLibraryPublicationStage.NeedsAttention, runtime.Activity.Stage); Assert.IsTrue(runtime.Activity.CanRetry);
            Assert.IsInstanceOfType<HttpRequestException>(runtime.LastAttemptFailure); Assert.IsNull(runtime.Failure); Assert.IsFalse(failed);
        }
        var pending = await fixture.PreparedAsync().ConfigureAwait(false);
        await using var recovered = PublicationRuntime(fixture); recovered.Request(CancellationToken.None);
        await recovered.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.PoliciesPublished, recovered.Activity.Stage); Assert.AreEqual(2, fixture.Allocations); Assert.AreEqual(3, fixture.Creates);
        var completion = await fixture.Setup.Accounts.State.ExecuteAsync(database => new LibraryAuthorityRepository(database, fixture.Budget).InitialPublicationCompletion(), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(completion); Assert.AreEqual(pending.Publisher.FileId, completion.Publisher.FileId);
    }

    [TestMethod]
    public async Task HistoricalCompletionAdvancesOnlyTheSetupStageWithoutRerunningBootstrapAgainstLaterCatalogObjects()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        var published = await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        await fixture.Setup.Accounts.State.ExecuteAsync(database => new WorkspacePublicationRepository(database, fixture.Budget)
            .Prepare(Guid.NewGuid(), published.Plan.Scope, "facts-index", "application/json", "{}"u8), CancellationToken.None).ConfigureAwait(false);
        var scans = fixture.InitialInventories;
        await using var runtime = PublicationRuntime(fixture); runtime.Request(CancellationToken.None);
        await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.PoliciesPublished, runtime.Activity.Stage); Assert.AreEqual(scans, fixture.InitialInventories);
        Assert.AreEqual(2, fixture.Creates); Assert.IsNull(runtime.Failure); // This status is not a catalog activation/publication grant.
    }

    [TestMethod]
    public async Task ExplicitRuntimeRetryJoinsItsPreviousAttemptAndReusesTheSavedPendingObject()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false); fixture.LoseFirstReply = true;
        await using var runtime = PublicationRuntime(fixture); runtime.Request(CancellationToken.None);
        await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.NeedsAttention, runtime.Activity.Stage); var pending = await fixture.PreparedAsync().ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.Running, runtime.Request(CancellationToken.None).Stage);
        await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var completed = await fixture.Setup.Accounts.State.ExecuteAsync(database => new LibraryAuthorityRepository(database, fixture.Budget).InitialPublicationCompletion(), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(completed); Assert.AreEqual(pending.Publisher.FileId, completed.Publisher.FileId);
        Assert.AreEqual(InitialLibraryPublicationStage.PoliciesPublished, runtime.Activity.Stage); Assert.IsNull(runtime.LastAttemptFailure);
        Assert.AreEqual(2, fixture.Allocations); Assert.AreEqual(3, fixture.Creates);
    }

    [TestMethod]
    public async Task MissingCompletionWithLaterWorkPausesRuntimeWithoutRecreatingOrUploadingSetup()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false); var published = await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        await fixture.Setup.Accounts.State.ExecuteAsync(database => new WorkspacePublicationRepository(database, fixture.Budget)
            .Prepare(Guid.NewGuid(), published.Plan.Scope, "facts-index", "application/json", "{}"u8), CancellationToken.None).ConfigureAwait(false);
        await InitialPublicationSqlAsync(fixture, "DELETE FROM processor_meta WHERE key IN('initial_library_publication_complete_v1','initial_library_publication_complete_v1:sha256')").ConfigureAwait(false);
        var scans = fixture.InitialInventories; await using var runtime = PublicationRuntime(fixture); runtime.Request(CancellationToken.None);
        await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.NeedsAttention, runtime.Activity.Stage); Assert.IsTrue(runtime.Activity.CanRetry);
        Assert.IsInstanceOfType<LibraryInitialPublicationCompletionMissingException>(runtime.LastAttemptFailure);
        Assert.AreEqual(scans, fixture.InitialInventories); Assert.AreEqual(2, fixture.Allocations); Assert.AreEqual(2, fixture.Creates); Assert.IsNull(runtime.Failure);
    }

    [TestMethod]
    public async Task SetupRuntimeReportsUnexpectedInvariantFailureAndCannotKeepRestartingIt()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false); await using var runtime = PublicationRuntime(fixture);
        var invariant = new InvalidOperationException("Synthetic invariant; do-not-echo-private-diagnostic"); var reported = 0;
        fixture.Setup.Accounts.Readers.WorkspaceAllocate = _ => throw invariant;
        runtime.Failed += error => { Assert.AreSame(invariant, error); reported++; };
        runtime.Request(CancellationToken.None); await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Assert.AreSame(invariant, runtime.Failure); Assert.AreEqual(1, reported); Assert.IsFalse(runtime.Activity.CanRetry);
        Assert.IsFalse(runtime.Activity.Detail!.Contains("do-not-echo", StringComparison.Ordinal));
        Assert.ThrowsExactly<InvalidOperationException>(() => runtime.Request(CancellationToken.None)); Assert.AreEqual(0, fixture.Creates);
    }

    [TestMethod]
    [DataRow("oauth")]
    [DataRow("vault")]
    public async Task SetupRuntimeKeepsExpiredAuthorizationOrLockedCredentialsActionableWithoutExposingPrivateErrors(string kind)
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false); await using var runtime = PublicationRuntime(fixture);
        Exception error = kind == "oauth" ? new Google.Apis.Auth.OAuth2.Responses.TokenResponseException(new() { Error = "invalid_grant", ErrorDescription = "synthetic-private-auth-detail" }) :
            new Mpa.Processor.Credentials.CredentialVaultException("synthetic-private-vault-detail");
        fixture.Setup.Accounts.Readers.WorkspaceInspect = (_, _) => throw error;
        var fatal = false; runtime.Failed += _ => fatal = true; runtime.Request(CancellationToken.None);
        await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.NeedsAttention, runtime.Activity.Stage); Assert.IsTrue(runtime.Activity.CanRetry);
        Assert.AreSame(error, runtime.LastAttemptFailure); Assert.IsNull(runtime.Failure); Assert.IsFalse(fatal);
        Assert.IsFalse(runtime.Activity.Detail!.Contains("synthetic-private", StringComparison.Ordinal));
        StringAssert.Contains(runtime.Activity.Detail, kind == "oauth" ? "Reconnect Google Drive" : "Unlock this computer");
        Assert.AreEqual(0, fixture.Allocations); Assert.AreEqual(0, fixture.Creates);
    }

    [TestMethod]
    public async Task SetupRuntimeExitJoinsUncooperativeCloudCreationBeforeReleasingItsBorrowedOwners()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false); await using var runtime = PublicationRuntime(fixture);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Setup.Accounts.Readers.WorkspaceCreate = async (folder, file, _, bytes, _, _) =>
        { entered.TrySetResult(); await release.Task.ConfigureAwait(false); return new DriveWorkspaceObject("permission-A", file, folder, 7, bytes.ToArray()); };
        runtime.Request(CancellationToken.None); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var stopping = runtime.DisposeAsync().AsTask();
        try
        {
            Assert.AreEqual(InitialLibraryPublicationStage.Stopping, runtime.Activity.Stage); Assert.IsFalse(stopping.IsCompleted); Assert.IsFalse(runtime.ExitConfirmed);
            Assert.AreEqual(0, fixture.Setup.Accounts.Readers.DisposalAttempts); Assert.IsNotNull(fixture.Setup.Keys.PublicIdentity);
        }
        finally { release.TrySetResult(); }
        await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.IsTrue(runtime.ExitConfirmed); Assert.AreEqual(InitialLibraryPublicationStage.Stopped, runtime.Activity.Stage); Assert.IsNull(runtime.Failure);
        Assert.ThrowsExactly<ObjectDisposedException>(() => runtime.Request(CancellationToken.None));
        Assert.IsNull(await fixture.Setup.Accounts.State.ExecuteAsync(database => new LibraryAuthorityRepository(database, fixture.Budget).InitialPublicationCompletion(), CancellationToken.None).ConfigureAwait(false));
        Assert.IsNull((await fixture.PreparedAsync().ConfigureAwait(false)).Publisher.VerifiedVersion);
    }

    private static InitialLibraryPublicationRuntime PublicationRuntime(InitialPublicationFixture fixture) => new(fixture.Setup.Accounts.State, fixture.Setup.Accounts.Service, fixture.Setup.Keys, fixture.Budget);
}
