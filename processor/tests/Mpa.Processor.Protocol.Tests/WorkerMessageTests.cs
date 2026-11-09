using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class WorkerMessageTests
{
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    [TestMethod]
    public void EnvelopeRoundTripKeepsIndependentCountersAndOwnedPayload()
    {
        var session = Guid.NewGuid();
        var core = new WorkerMessageCodec(session); var worker = new WorkerMessageCodec(session);
        var hello = worker.Read(core.Encode("hello", JsonSerializer.SerializeToElement(new { pluginId = "metadata" })));
        Assert.AreEqual("metadata", hello.Payload.GetProperty("pluginId").GetString());
        Assert.AreEqual("capabilities", core.Read(worker.Encode("capabilities", Empty)).Type);
        var attempt = new WorkerAttempt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var prepared = worker.Read(core.Encode("prepare", Empty, attempt));
        Assert.AreEqual(attempt, prepared.Attempt);
        Assert.AreEqual("prepared", core.Read(worker.Encode("prepared", Empty, attempt)).Type);
    }

    [TestMethod]
    public void WrongLaunchRepeatedFramesAndMissingScopePoisonTheirConversation()
    {
        var session = Guid.NewGuid();
        var sender = new WorkerMessageCodec(session); var reader = new WorkerMessageCodec(session);
        var body = sender.Encode("hello", Empty);
        reader.Read(body);
        Assert.ThrowsExactly<ProtocolFormatException>(() => reader.Read(body));
        Assert.ThrowsExactly<InvalidOperationException>(() => reader.Read(sender.Encode("hello", Empty)));
        Assert.ThrowsExactly<ProtocolFormatException>(() => new WorkerMessageCodec(Guid.NewGuid()).Read(body));
        Assert.ThrowsExactly<ProtocolFormatException>(() => new WorkerMessageCodec(session).Encode("run", Empty));
        Assert.ThrowsExactly<ProtocolFormatException>(() => new WorkerMessageCodec(session).Encode("hello", Empty, new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())));
        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("\"sequence\":\"1\"", "\"sequence\":\"01\"", StringComparison.Ordinal));
        Assert.ThrowsExactly<ProtocolFormatException>(() => new WorkerMessageCodec(session).Read(changed));
        Assert.ThrowsExactly<ProtocolFormatException>(() => new WorkerMessageCodec(session).Read("{}"u8));
    }

    [TestMethod]
    public void RolesAndAttemptIdentityAreRequiredBeforeAnyResult()
    {
        var state = Ready(); var attempt = Attempt();
        state.Observe(WorkerEndpoint.Core, Message("prepare", attempt));
        Assert.ThrowsExactly<ProtocolFormatException>(() => state.Observe(WorkerEndpoint.Worker, Message("result", attempt)));
        Assert.AreEqual(WorkerPhase.Faulted, state.Phase);
        state = Ready();
        state.Observe(WorkerEndpoint.Core, Message("prepare", attempt));
        state.Observe(WorkerEndpoint.Worker, Message("prepared", attempt));
        state.Observe(WorkerEndpoint.Core, Message("run", attempt));
        Assert.ThrowsExactly<ProtocolFormatException>(() => state.Observe(WorkerEndpoint.Worker, Message("result", Attempt())));
        Assert.ThrowsExactly<ProtocolFormatException>(() => new WorkerConversation().Observe(WorkerEndpoint.Worker, Message("hello")));
    }

    [TestMethod]
    public void CancellationFencesCrossingResultsUntilExactAcknowledgement()
    {
        var core = Running(out var attempt); var worker = Running(attempt);
        core.Observe(WorkerEndpoint.Core, Message("cancel", attempt));
        // Worker completed before seeing cancel; its result crosses the Core's cancel message.
        worker.Observe(WorkerEndpoint.Worker, Message("result", attempt));
        Assert.AreEqual(WorkerMessageDisposition.FencedAfterCancellation, core.Observe(WorkerEndpoint.Worker, Message("result", attempt)));
        Assert.AreEqual(WorkerPhase.Canceling, core.Phase);
        worker.Observe(WorkerEndpoint.Core, Message("cancel", attempt));
        worker.Observe(WorkerEndpoint.Worker, Message("canceled", attempt));
        core.Observe(WorkerEndpoint.Worker, Message("canceled", attempt));
        Assert.AreEqual(WorkerPhase.Ready, core.Phase);
        core.Observe(WorkerEndpoint.Core, Message("exit"));
        core.Observe(WorkerEndpoint.Worker, Message("exited"));
        Assert.AreEqual(WorkerPhase.Closed, core.Phase);
        Assert.ThrowsExactly<InvalidOperationException>(() => core.Observe(WorkerEndpoint.Core, Message("hello")));
    }

    private static WorkerConversation Ready()
    {
        var state = new WorkerConversation();
        state.Observe(WorkerEndpoint.Core, Message("hello"));
        state.Observe(WorkerEndpoint.Worker, Message("capabilities"));
        return state;
    }
    private static WorkerConversation Running(out WorkerAttempt attempt) { attempt = Attempt(); return Running(attempt); }
    private static WorkerConversation Running(WorkerAttempt attempt)
    {
        var state = Ready();
        state.Observe(WorkerEndpoint.Core, Message("prepare", attempt));
        state.Observe(WorkerEndpoint.Worker, Message("prepared", attempt));
        state.Observe(WorkerEndpoint.Core, Message("run", attempt));
        return state;
    }
    private static WorkerAttempt Attempt() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static WorkerMessage Message(string type, WorkerAttempt? attempt = null) => new(type, Empty, attempt);
}
