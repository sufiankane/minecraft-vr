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
    /// extent on any axis) overlaps nothing.
    /// <para>
    /// Both queries compute cell indices in <see cref="double"/> space, so no
    /// <see cref="int"/> arithmetic can wrap: cell cubes are intersected with
    /// the representable <see cref="int"/> range and a box that lies entirely
    /// outside it overlaps no cell. <see cref="Overlaps"/> scans at most
    /// <see cref="MaxCellsScanned"/> cells: a non-finite bound, or a finite box
    /// covering more cells than that, throws <see cref="ArgumentException"/>
    /// instead of stalling the frame. An empty box returns false before the
    /// bounds are examined.
    /// </para>
    /// </remarks>
    public static class VoxelCollision
    {
        /// <summary>
        /// Upper bound on the number of unit cells one <see cref="Overlaps"/>
        /// query may scan (2^20, about a million): a box covering more throws
        /// <see cref="ArgumentException"/> rather than making the frame tick
        /// unbounded.
        /// </summary>
        public const long MaxCellsScanned = 1L << 20;

        /// <summary>
        /// Whether any solid cell cube overlaps <paramref name="box"/>.
        /// Unloaded chunks are treated as air.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// A bound of <paramref name="box"/> is NaN or infinite, or the box
        /// covers more than <see cref="MaxCellsScanned"/> representable cells.
        /// </exception>
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

            if (!HasFiniteBounds(box))
            {
                throw new ArgumentException("Collision box bounds must be finite.", nameof(box));
            }

            // Intersect the covered cell range with the representable Int3
            // range; anything outside it is not a cell and is clipped, so no
            // cast can wrap.
            double minX = Math.Max(Math.Floor(box.Min.X), int.MinValue);
            double maxX = Math.Min(Math.Floor(box.Max.X), int.MaxValue);
            double minY = Math.Max(Math.Floor(box.Min.Y), int.MinValue);
            double maxY = Math.Min(Math.Floor(box.Max.Y), int.MaxValue);
            double minZ = Math.Max(Math.Floor(box.Min.Z), int.MinValue);
            double maxZ = Math.Min(Math.Floor(box.Max.Z), int.MaxValue);

            if (minX > maxX || minY > maxY || minZ > maxZ)
            {
                return false;
            }

            double cells = (maxX - minX + 1.0) * (maxY - minY + 1.0) * (maxZ - minZ + 1.0);
            if (cells > MaxCellsScanned)
            {
                throw new ArgumentException(
                    $"Collision box covers {cells} cells, more than the {MaxCellsScanned}-cell budget.",
                    nameof(box));
            }

            int z0 = (int)minZ;
            int y0 = (int)minY;
            int x0 = (int)minX;
            int countX = (int)(maxX - minX + 1.0);
            int countY = (int)(maxY - minY + 1.0);
            int countZ = (int)(maxZ - minZ + 1.0);

            for (int iz = 0; iz < countZ; iz++)
            {
                int z = z0 + iz;
                for (int iy = 0; iy < countY; iy++)
                {
                    int y = y0 + iy;
                    for (int ix = 0; ix < countX; ix++)
                    {
                        int x = x0 + ix;
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
                && box.Min.X < x + 1.0 && x < box.Max.X
                && box.Min.Y < y + 1.0 && y < box.Max.Y
                && box.Min.Z < z + 1.0 && z < box.Max.Z;
        }

        private static bool HasVolume(Aabb box)
        {
            return box.Min.X < box.Max.X && box.Min.Y < box.Max.Y && box.Min.Z < box.Max.Z;
        }

        private static bool HasFiniteBounds(Aabb box)
        {
            return double.IsFinite(box.Min.X) && double.IsFinite(box.Min.Y) && double.IsFinite(box.Min.Z)
                && double.IsFinite(box.Max.X) && double.IsFinite(box.Max.Y) && double.IsFinite(box.Max.Z);
        }
    }
}
