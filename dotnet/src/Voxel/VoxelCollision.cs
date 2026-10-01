using System;

namespace Cubeglass.Voxel
{
    /// <summary>
    /// Half-open AABB queries against a world: player collision and placement
    /// gating (dossier section 6/S2).
    /// </summary>
    /// <remarks>
    /// A cell is solid when its <see cref="BlockId"/> is not
    /// <see cref="BlockId.Air"/>; the S2 registry marks air as the only
    /// non-solid block and this query has no registry parameter. Unloaded
    /// cells read as air. Cell cubes and boxes are half-open, so touching on a
    /// face, edge or corner never counts as an overlap, and an empty box (zero
    /// extent on any axis) overlaps nothing. Both queries iterate only the
    /// cells covering the box and allocate nothing; boxes are expected to be
    /// finite and to cover a practical number of cells.
    /// </remarks>
    public static class VoxelCollision
    {
        /// <summary>
        /// Whether any solid cell cube overlaps <paramref name="box"/>.
        /// Unloaded chunks are treated as air.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
        public static bool Overlaps(IWorld world, Aabb box)
        {
            if (world is null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (!HasVolume(box))
            {
                return false;
            }

            int minX = FloorToInt(box.Min.X);
            int maxX = FloorToInt(box.Max.X);
            int minY = FloorToInt(box.Min.Y);
            int maxY = FloorToInt(box.Max.Y);
            int minZ = FloorToInt(box.Min.Z);
            int maxZ = FloorToInt(box.Max.Z);

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        if (world.Get(new Int3(x, y, z)) == BlockId.Air)
                        {
                            continue;
                        }

                        if (CellOverlaps(x, y, z, box))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a block may be placed at <paramref name="cell"/> without
        /// intersecting the player: false exactly when the cell's half-open
        /// unit cube overlaps <paramref name="playerBox"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
        public static bool CanPlace(IWorld world, Int3 cell, Aabb playerBox)
        {
            if (world is null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            return !CellOverlaps(cell.X, cell.Y, cell.Z, playerBox);
        }

        private static bool CellOverlaps(int x, int y, int z, Aabb box)
        {
            return HasVolume(box)
                && box.Min.X < x + 1 && x < box.Max.X
                && box.Min.Y < y + 1 && y < box.Max.Y
                && box.Min.Z < z + 1 && z < box.Max.Z;
        }

        private static bool HasVolume(Aabb box)
        {
            return box.Min.X < box.Max.X && box.Min.Y < box.Max.Y && box.Min.Z < box.Max.Z;
        }

        private static int FloorToInt(double value)
        {
            return (int)Math.Floor(value);
        }
    }
}
