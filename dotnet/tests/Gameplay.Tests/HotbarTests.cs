using System;
using Cubeglass.Voxel;
using NUnit.Framework;

namespace Cubeglass.Gameplay.Tests
{
    // The hotbar is the fixed nine-slot selection model from ADR-0008:
    // Stone/Dirt/Grass/Sand/Wood in slots 0..4 and air in the rest, with a
    // selection that wraps at both ends.
    [TestFixture]
    public sealed class HotbarTests
    {
        [Test]
        public void DefaultSlotsAreStoneDirtGrassSandWoodThenAir()
        {
            var hotbar = new Hotbar();

            Assert.That(BlockRegistry.Default.Get(hotbar.Get(0)).Name, Is.EqualTo("Stone"));
            Assert.That(BlockRegistry.Default.Get(hotbar.Get(1)).Name, Is.EqualTo("Dirt"));
            Assert.That(BlockRegistry.Default.Get(hotbar.Get(2)).Name, Is.EqualTo("Grass"));
            Assert.That(BlockRegistry.Default.Get(hotbar.Get(3)).Name, Is.EqualTo("Sand"));
            Assert.That(BlockRegistry.Default.Get(hotbar.Get(4)).Name, Is.EqualTo("Wood"));
            for (int slot = 5; slot < Hotbar.SlotCount; slot++)
            {
                Assert.That(hotbar.Get(slot), Is.EqualTo(BlockId.Air), $"slot {slot} must be empty");
            }
        }

        [Test]
        public void StartsOnStoneAtSlotZero()
        {
            var hotbar = new Hotbar();

            Assert.That(hotbar.SelectedIndex, Is.EqualTo(0));
            Assert.That(hotbar.Selected, Is.EqualTo(hotbar.Get(0)));
            Assert.That(BlockRegistry.Default.Get(hotbar.Selected).Name, Is.EqualTo("Stone"));
        }

        [Test]
        public void CycleAdvancesAndWrapsForward()
        {
            var hotbar = new Hotbar();

            hotbar.Cycle(1);
            Assert.That(hotbar.SelectedIndex, Is.EqualTo(1));

            hotbar.Cycle(8);
            Assert.That(hotbar.SelectedIndex, Is.EqualTo(0), "1 + 8 must wrap to slot 0");

            hotbar.Cycle(Hotbar.SlotCount);
            Assert.That(hotbar.SelectedIndex, Is.EqualTo(0), "a full turn is the identity");
        }

        [Test]
        public void CycleWrapsBackward()
        {
            var hotbar = new Hotbar();

            hotbar.Cycle(-1);
            Assert.That(hotbar.SelectedIndex, Is.EqualTo(Hotbar.SlotCount - 1));
            Assert.That(hotbar.Selected, Is.EqualTo(BlockId.Air));

            hotbar.Cycle(-1);
            Assert.That(hotbar.SelectedIndex, Is.EqualTo(Hotbar.SlotCount - 2));
        }

        [Test]
        public void CycleZeroKeepsTheSelection()
        {
            var hotbar = new Hotbar();
            hotbar.Cycle(1);
            hotbar.Cycle(0);

            Assert.That(hotbar.SelectedIndex, Is.EqualTo(1));
        }

        [Test]
        public void CycleWrapsLargeDeltas()
        {
            var hotbar = new Hotbar();

            hotbar.Cycle(-10);
            Assert.That(hotbar.SelectedIndex, Is.EqualTo(Hotbar.SlotCount - 1));

            hotbar.Cycle(19);
            Assert.That(hotbar.SelectedIndex, Is.EqualTo(0));
        }

        [Test]
        public void GetRejectsSlotsOutsideTheBar()
        {
            var hotbar = new Hotbar();

            Assert.Throws<ArgumentOutOfRangeException>(() => hotbar.Get(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => hotbar.Get(Hotbar.SlotCount));
        }

        [Test]
        public void WrapIndexFoldsIntoRange()
        {
            Assert.That(Hotbar.WrapIndex(0), Is.EqualTo(0));
            Assert.That(Hotbar.WrapIndex(Hotbar.SlotCount), Is.EqualTo(0));
            Assert.That(Hotbar.WrapIndex(-1), Is.EqualTo(Hotbar.SlotCount - 1));
            Assert.That(Hotbar.WrapIndex(-Hotbar.SlotCount - 1), Is.EqualTo(Hotbar.SlotCount - 1));
            Assert.That(Hotbar.WrapIndex(10), Is.EqualTo(1));
        }
    }
}
