using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class StorageBudgetTests
{
    private const long Gib = StorageBudgetManager.Gibibyte;

    [TestMethod]
    public void ReservationsAccountForCacheAndOtherWritersAndReleaseExactlyOnce()
    {
        var reader = new MutableSpace(new(100 * Gib, 20 * Gib, 2 * Gib));
        var manager = new StorageBudgetManager(reader);
        Assert.AreEqual(5 * Gib, manager.Status().ReserveBytes);
        Assert.AreEqual(8 * Gib, manager.Status().ReservableBytes);
        using var first = manager.Reserve(3 * Gib);
        using var second = manager.Reserve(5 * Gib);
        Assert.AreEqual(0L, manager.Status().ReservableBytes);
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => manager.Reserve(1));
        first.Dispose(); first.Dispose();
        Assert.AreEqual(3 * Gib, manager.Status().ReservableBytes);
        Assert.ThrowsExactly<ObjectDisposedException>(() => first.RecheckBeforeWrite(1));
    }

    [TestMethod]
    public void ConcurrentExternalDiskUseBlocksTheWriteAndPreservesItsReservationForCheckpointRecovery()
    {
        var reader = new MutableSpace(new(100 * Gib, 20 * Gib, 0));
        var manager = new StorageBudgetManager(reader);
        using var reservation = manager.Reserve(4 * Gib);
        reservation.RecheckBeforeWrite(4 * Gib);
        reader.Value = new(100 * Gib, 6 * Gib, 0);
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => reservation.RecheckBeforeWrite(4 * Gib));
        Assert.AreEqual(4 * Gib, manager.Status().ReservedBytes);
        reader.Value = new(100 * Gib, 12 * Gib, 0);
        reservation.RecheckBeforeWrite(4 * Gib);
        reader.Value = new(100 * Gib, 12 * Gib, 8 * Gib);
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => reservation.RecheckBeforeWrite(4 * Gib));
    }

    [TestMethod]
    public void SmallOrFullVolumesAndUnknownMeasurementsNeverProduceNegativeOrUnlimitedBudgets()
    {
        var reader = new MutableSpace(new(Gib, Gib, 0));
        var manager = new StorageBudgetManager(reader);
        Assert.AreEqual(0L, manager.Status().ReservableBytes);
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => manager.Reserve(1));
        reader.Value = new(100 * Gib, 0, 0);
        Assert.AreEqual(0L, manager.Status().ReservableBytes);
        reader.Value = new(0, 0, 0);
        Assert.ThrowsExactly<InvalidDataException>(() => manager.Status());
    }

    [TestMethod]
    public void ExplicitPolicyCanLowerPercentageWhileRetainingTwoGibibyteSafetyFloor()
    {
        var reader = new MutableSpace(new(100 * Gib, 4 * Gib, 0));
        Assert.AreEqual(0L, new StorageBudgetManager(reader).Status().ReservableBytes);
        Assert.AreEqual(2 * Gib, new StorageBudgetManager(reader, reservePercent: 0).Status().ReservableBytes);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new StorageBudgetManager(reader, minimumReserveBytes: Gib));
    }

    private sealed class MutableSpace(StorageSpaceSnapshot value) : IStorageSpaceReader
    {
        internal StorageSpaceSnapshot Value { get; set; } = value;
        public StorageSpaceSnapshot Read() => Value;
    }
}
