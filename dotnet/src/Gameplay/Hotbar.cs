using System;
using Cubeglass.Voxel;

namespace Cubeglass.Gameplay
{
    /// <summary>
    /// The fixed nine-slot block selection model (ADR-0008, R26).
    /// </summary>
    /// <remarks>
    /// Slots 0..4 default to the ADR-0006 blocks Stone, Dirt, Grass, Sand and
    /// Wood (ids 1..5); slots 5..8 default to air. <see cref="Cycle"/> wraps
    /// at both ends; <see cref="WrapIndex"/> exposes the same fold for
    /// <see cref="PlayerState.HotbarIndex"/>.
    /// </remarks>
    public sealed class Hotbar
    {
        /// <summary>Number of hotbar slots.</summary>
        public const int SlotCount = 9;

        private static readonly BlockId[] DefaultSlots =
        {
            new BlockId(1),
            new BlockId(2),
            new BlockId(3),
            new BlockId(4),
            new BlockId(5),
            BlockId.Air,
            BlockId.Air,
            BlockId.Air,
            BlockId.Air,
        };

        private readonly BlockId[] _slots;
        private int _selectedIndex;

        /// <summary>Creates a hotbar with the ADR-0008 default contents.</summary>
        public Hotbar()
        {
            _slots = (BlockId[])DefaultSlots.Clone();
        }

        /// <summary>The selected slot index.</summary>
        public int SelectedIndex => _selectedIndex;

        /// <summary>The block in the selected slot.</summary>
        public BlockId Selected => _slots[_selectedIndex];

        /// <summary>
        /// Moves the selection by <paramref name="delta"/> slots, wrapping at
        /// both ends. Zero keeps the selection.
        /// </summary>
        public void Cycle(int delta)
        {
            _selectedIndex = WrapIndex(_selectedIndex + delta);
        }

        /// <summary>
        /// Returns the block in <paramref name="slot"/>.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="slot"/> is outside <c>[0, SlotCount)</c>.
        /// </exception>
        public BlockId Get(int slot)
        {
            if (slot < 0 || slot >= SlotCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(slot), slot, "A hotbar slot must lie in [0, SlotCount).");
            }

            return _slots[slot];
        }

        /// <summary>Folds any slot index into <c>[0, SlotCount)</c>.</summary>
        public static int WrapIndex(int index)
        {
            int wrapped = index % SlotCount;
            return wrapped < 0 ? wrapped + SlotCount : wrapped;
        }
    }
}
