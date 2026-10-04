using System;
using Cubeglass.CoreMath;

namespace Cubeglass.Voxel
{
    /// <summary>
    /// Amanatides–Woo grid traversal over unit cells (dossier section 5.9).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Direction and distance.</b> <see cref="Ray.Direction"/> need not be
    /// unit: it is normalised once at the start, and every reported distance is
    /// in world units along that normalised direction.
    /// </para>
    /// <para>
    /// <b>Inside and face-origin hits.</b> When the ray origin's floor cell is
    /// loaded and solid, that cell is returned immediately with
    /// <see cref="Int3.Zero"/> normal and distance zero. An origin lying
    /// exactly on a cell face belongs to the floor cell: moving away from that
    /// face into an adjacent solid cell is an entry hit at distance zero with
    /// the adjacent cell's non-zero entry normal, while moving along the
    /// positive axis direction enters the cell beyond the face after one full
    /// unit. The normal distinguishes the two zero-distance cases: only an
    /// inside hit has <see cref="Int3.Zero"/>.
    /// </para>
    /// <para>
    /// <b>Range.</b> <paramref name="maxDistance"/> is inclusive: a hit exactly
    /// at <paramref name="maxDistance"/> is returned, so every accepted
    /// <see cref="RayHit.Distance"/> lies in <c>[0, maxDistance]</c>.
    /// </para>
    /// <para>
    /// <b>Tie rule.</b> When the next boundary crossing is exactly shared by
    /// several axes (an edge or corner crossing), the traversal advances the
    /// smallest axis index first: X before Y before Z. The rule makes ties
    /// deterministic and is pinned by tests.
    /// </para>
    /// <para>
    /// <b>Unloaded cells.</b> Cells whose chunk is not loaded are treated as
    /// non-solid and the traversal continues through them, bounded by
    /// <paramref name="maxDistance"/>; loadedness is only observable per cell
    /// through <see cref="IWorld.IsLoaded"/>, so there is no separate
    /// "exited loaded space" test. A ray that never enters a solid loaded cell
    /// within <paramref name="maxDistance"/> returns null.
    /// </para>
    /// <para>
    /// <b>Solidity.</b> Every non-air block is treated as solid: the frozen
    /// <see cref="IRaycaster"/> signature carries no block registry, and the
    /// shipped block set (ADR-0006) defines all non-air blocks as solid.
    /// </para>
    /// <para>
    /// <b>Degenerate inputs.</b> A zero direction, a non-finite origin or
    /// direction returns null. A negative, NaN or infinite
    /// <paramref name="maxDistance"/> throws
    /// <see cref="ArgumentOutOfRangeException"/>: the accepted range is
    /// <c>[0, float.MaxValue]</c> (zero is valid and inclusive).
    /// </para>
    /// <para>
    /// <b>Termination.</b> The traversal advances at least one cell per
    /// iteration and is bounded by a step cap derived from the distance and the
    /// normalised direction
    /// (<c>floor(maxDistance * (|dx| + |dy| + |dz|)) + 4</c>, truncated to a
    /// 64-bit integer); reaching the cap returns null. A step that would
    /// overflow a cell coordinate also returns
    /// null, so every accepted input terminates without coordinate wrap-around.
    /// </para>
    /// <para>
    /// The implementation is allocation-free: it uses only value types plus the
    /// <see cref="IWorld"/> lookups.
    /// </para>
    /// </remarks>
    public sealed class DdaRaycaster : IRaycaster
    {
        /// <exception cref="ArgumentNullException">
        /// <paramref name="w"/> is null.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="maxDistance"/> is negative, NaN or infinite.
        /// </exception>
        public RayHit? Cast(IWorld w, Ray ray, float maxDistance)
        {
            if (w is null)
            {
                throw new ArgumentNullException(nameof(w));
            }

            if (!(maxDistance >= 0f) || float.IsInfinity(maxDistance))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxDistance),
                    maxDistance,
                    "maxDistance must be finite and non-negative.");
            }

            if (!IsFinite(ray.Origin.X) || !IsFinite(ray.Origin.Y) || !IsFinite(ray.Origin.Z))
            {
                return null;
            }

            Vec3 direction = Vec3.Normalized(ray.Direction);
            if (!IsFinite(direction.X) || !IsFinite(direction.Y) || !IsFinite(direction.Z)
                || (direction.X == 0.0 && direction.Y == 0.0 && direction.Z == 0.0))
            {
                return null;
            }

            int cellX = FloorToInt(ray.Origin.X);
            int cellY = FloorToInt(ray.Origin.Y);
            int cellZ = FloorToInt(ray.Origin.Z);

            RayHit? inside = Probe(w, cellX, cellY, cellZ, Int3.Zero, 0f);
            if (inside.HasValue)
            {
                return inside;
            }

            int stepX = Math.Sign(direction.X);
            int stepY = Math.Sign(direction.Y);
            int stepZ = Math.Sign(direction.Z);

            double tMaxX = InitialTMax(ray.Origin.X, cellX, direction.X, stepX);
            double tMaxY = InitialTMax(ray.Origin.Y, cellY, direction.Y, stepY);
            double tMaxZ = InitialTMax(ray.Origin.Z, cellZ, direction.Z, stepZ);

            double tDeltaX = Delta(direction.X);
            double tDeltaY = Delta(direction.Y);
            double tDeltaZ = Delta(direction.Z);

            // Every iteration advances exactly one axis by one cell, so the
            // number of crossings within the distance is bounded by
            // maxDistance * (|dx| + |dy| + |dz|) plus one initial crossing per
            // axis. The cap is defence-in-depth: it can only trip if the
            // distance test would otherwise never fire.
            long maxSteps = StepCap(
                maxDistance,
                Math.Abs(direction.X) + Math.Abs(direction.Y) + Math.Abs(direction.Z));
            long steps = 0;

            while (true)
            {
                if (steps >= maxSteps)
                {
                    return null;
                }

                steps++;

                int axis;
                double t;
                if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
                {
                    axis = 0;
                    t = tMaxX;
                }
                else if (tMaxY <= tMaxZ)
                {
                    axis = 1;
                    t = tMaxY;
                }
                else
                {
                    axis = 2;
                    t = tMaxZ;
                }

                if (t > maxDistance)
                {
                    return null;
                }

                Int3 normal;
                if (axis == 0)
                {
                    if (WouldOverflow(cellX, stepX))
                    {
                        return null;
                    }

                    cellX += stepX;
                    normal = new Int3(-stepX, 0, 0);
                    tMaxX = t + tDeltaX;
                }
                else if (axis == 1)
                {
                    if (WouldOverflow(cellY, stepY))
                    {
                        return null;
                    }

                    cellY += stepY;
                    normal = new Int3(0, -stepY, 0);
                    tMaxY = t + tDeltaY;
                }
                else
                {
                    if (WouldOverflow(cellZ, stepZ))
                    {
                        return null;
                    }

                    cellZ += stepZ;
                    normal = new Int3(0, 0, -stepZ);
                    tMaxZ = t + tDeltaZ;
                }

                RayHit? hit = Probe(w, cellX, cellY, cellZ, normal, t > 0.0 ? (float)t : 0f);
                if (hit.HasValue)
                {
                    return hit;
                }
            }
        }

        private static RayHit? Probe(IWorld w, int x, int y, int z, Int3 normal, float distance)
        {
            var cell = new Int3(x, y, z);
            if (!w.IsLoaded(cell))
            {
                return null;
            }

            BlockId block = w.Get(cell);
            if (block == BlockId.Air)
            {
                return null;
            }

            return new RayHit(cell, normal, distance, block);
        }

        private static bool WouldOverflow(int cell, int step)
        {
            return (step > 0 && cell == int.MaxValue) || (step < 0 && cell == int.MinValue);
        }

        private static long StepCap(float maxDistance, double directionLengthSum)
        {
            double bound = (maxDistance * directionLengthSum) + 4.0;
            return bound >= long.MaxValue ? long.MaxValue : (long)bound;
        }

        private static double InitialTMax(double origin, int cell, double direction, int step)
        {
            if (direction == 0.0)
            {
                return double.PositiveInfinity;
            }

            double boundary = step > 0 ? cell + 1.0 : cell;
            return (boundary - origin) / direction;
        }

        private static double Delta(double direction)
        {
            return direction == 0.0 ? double.PositiveInfinity : Math.Abs(1.0 / direction);
        }

        private static int FloorToInt(double value)
        {
            if (value <= int.MinValue)
            {
                return int.MinValue;
            }

            if (value >= int.MaxValue)
            {
                return int.MaxValue;
            }

            return (int)Math.Floor(value);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
