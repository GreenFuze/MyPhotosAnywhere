using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;
using System.Text.Json;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class CausalFieldRegisterTests
{
    [TestMethod]
    public void SharedOperationFingerprintsMatchIndependentPythonReference()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "causality.json")));
        foreach (var item in fixture.RootElement.GetProperty("operations").EnumerateArray())
        {
            var body = item.GetProperty("body");
            var operation = new HumanFieldOperation(body.GetProperty("id").GetGuid(), body.GetProperty("actorId").GetGuid(),
                long.Parse(body.GetProperty("sequence").GetString()!, System.Globalization.CultureInfo.InvariantCulture), body.GetProperty("targetId").GetGuid(),
                body.GetProperty("field").GetString()!, body.GetProperty("parents").EnumerateArray().Select(parent => parent.GetGuid()),
                Encoding.UTF8.GetBytes(body.GetProperty("value").GetRawText()));
            Assert.AreEqual(item.GetProperty("fingerprint").GetString(), operation.Fingerprint);
        }
    }

    private static readonly Guid Target = Guid.Parse("94a81164-e980-4ec1-a566-cec36475b839");
    private static readonly Guid ActorA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid ActorB = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static HumanFieldOperation Edit(int id, Guid actor, long sequence, Guid[] parents, string value = "\"name\"") =>
        new(Guid.Parse($"00000000-0000-4000-8000-{id:000000000000}"), actor, sequence, Target, "person.name", parents, System.Text.Encoding.UTF8.GetBytes(value));

    [TestMethod]
    public void OutOfOrderDeliveryDoesNotDiscardEditsAndConcurrentEditsRequireResolution()
    {
        var root = Edit(1, ActorA, 1, [], "\"original\"");
        var left = Edit(2, ActorA, 2, [root.Id], "\"Alice\"");
        var right = Edit(3, ActorB, 1, [root.Id], "\"Alicia\"");
        var resolution = Edit(4, ActorA, 3, [left.Id, right.Id], "\"chosen\"");
        var register = new CausalFieldRegister(Target, "person.name");
        register.Apply(left);
        Assert.AreEqual(0, register.Snapshot.Alternatives.Count);
        Assert.AreEqual(1, register.Snapshot.PendingIds.Count);
        register.Apply(right);
        register.Apply(root);
        Assert.IsTrue(register.Snapshot.HasConflict);
        Assert.AreEqual(root.Id, register.Snapshot.CommonPriorValues.Single().Id);
        register.Apply(resolution);
        Assert.IsFalse(register.Snapshot.HasConflict);
        Assert.AreEqual("\"chosen\"", register.Snapshot.Alternatives.Single().CanonicalValue);
        var late = Edit(5, ActorB, 2, [right.Id], "\"late offline edit\"");
        register.Apply(late);
        Assert.IsTrue(register.Snapshot.HasConflict);
        Assert.AreEqual(0, register.Snapshot.PendingIds.Count);
        Assert.IsFalse(register.Apply(late));
        Assert.ThrowsExactly<ProtocolFormatException>(() => register.Apply(Edit(5, ActorB, 2, [right.Id], "\"altered replay\"")));
    }

    [TestMethod]
    public void AllDeliveryOrdersHaveTheSameFrontier()
    {
        var root = Edit(1, ActorA, 1, []);
        var left = Edit(2, ActorA, 2, [root.Id]);
        var right = Edit(3, ActorB, 1, [root.Id]);
        foreach (var order in new[] { new[] { root, left, right }, [root, right, left], [left, root, right], [left, right, root], [right, root, left], [right, left, root] })
        {
            var register = new CausalFieldRegister(Target, "person.name");
            foreach (var operation in order) register.Apply(operation);
            CollectionAssert.AreEqual(new[] { left.Id, right.Id }, register.Snapshot.Alternatives.Select(operation => operation.Id).ToArray());
            Assert.AreEqual(root.Id, register.Snapshot.CommonPriorValues.Single().Id);
        }
    }

    [TestMethod]
    public void CyclesSequenceReuseAndWrongFieldsFailWithoutChangingAcceptedState()
    {
        var root = Edit(1, ActorA, 1, []);
        var register = new CausalFieldRegister(Target, "person.name");
        register.Apply(root);
        Assert.ThrowsExactly<ProtocolFormatException>(() => register.Apply(Edit(2, ActorA, 1, [])));
        Assert.ThrowsExactly<ProtocolFormatException>(() => register.Apply(new HumanFieldOperation(Guid.NewGuid(), ActorA, 2, Target, "caption", [], "null"u8)));
        var a = Edit(3, ActorA, 3, [Edit(4, ActorB, 1, []).Id]);
        register.Apply(a);
        Assert.ThrowsExactly<ProtocolFormatException>(() => register.Apply(Edit(4, ActorB, 1, [a.Id])));
        Assert.AreEqual(root.Id, register.Snapshot.Alternatives.Single().Id);
        Assert.AreEqual(1, register.Snapshot.PendingIds.Count);
    }
}
