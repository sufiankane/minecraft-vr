using System;
using Cubeglass.Voxel;

namespace Cubeglass.Mesh
{
    /// <summary>
    /// The three-neighbour ambient-occlusion rule for one face vertex, pinned
    /// by ADR-0007 and the Task 3 tests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A face vertex is the shared corner of the 2x2x2 block of cells around
    /// it. Four of those cells lie behind the face plane (the meshed cell and
    /// its in-plane neighbours); the three cells on the outside of the face
    /// that share the vertex determine the occlusion: the two edge-adjacent
    /// <c>side</c> cells and the diagonal <c>corner</c> cell. Both side cells
    /// opaque means the vertex is fully occluded (<c>level 0</c>) regardless
    /// of the corner; otherwise
    /// <c>level = 3 - (side1 ? 1 : 0) - (side2 ? 1 : 0) - (corner ? 1 : 0)</c>,
    /// so each opaque neighbour costs one level: 3 open, 2 with one opaque,
    /// 1 with two, 0 with both sides opaque.
    /// </para>
    /// <para>
    /// Levels are quantised to the ADR-0007 bytes <c>0, 85, 170, 255</c>:
    /// <c>byte = level * 85</c>. The byte is written per vertex in the
    /// ADR-0007 winding corner order, so a merged quad whose cells share one
    /// per-cell pattern stores that pattern unchanged.
    /// </para>
    /// <para>
    /// The sample cells cross the chunk border by at most one cell per axis.
    /// Cells inside the centre chunk read from <see cref="ChunkSnapshot.Get"/>;
    /// cells outside it resolve through
    /// <see cref="NeighbourSnapshot.GetCell"/>, where an unloaded neighbour
    /// reads as <see cref="BlockId.Air"/>. Occlusion uses
    /// <see cref="BlockDefinition.Opaque"/>, exactly like face culling, so a
    /// non-opaque block never shades and two chunks computing the same vertex
    /// of a continuous surface store the same byte.
    /// </para>
    /// </remarks>
    public static class AmbientOcclusion
    {
        /// <summary>Byte step between two adjacent AO levels (255 / 3).</summary>
        public const byte LevelStep = 85;

        /// <summary>The fully open level's byte.</summary>
        public const byte FullyOpen = 255;

        /// <summary>
        /// Outward normal of each face in the ADR-0007 order
        /// <c>+X, -X, +Y, -Y, +Z, -Z</c>.
        /// </summary>
        private static readonly Int3[] FaceNormals =
        {
            new Int3(1, 0, 0),
            new Int3(-1, 0, 0),
            new Int3(0, 1, 0),
            new Int3(0, -1, 0),
            new Int3(0, 0, 1),
            new Int3(0, 0, -1),
        };

        /// <summary>Normal axis (0=X, 1=Y, 2=Z) of each face.</summary>
        private static readonly int[] FacePlaneAxis = { 0, 0, 1, 1, 2, 2 };

        /// <summary>
        /// The four cell-relative corners of each face in the ADR-0007
        /// winding order; each component is 0 or 1 and the component on the
        /// face's normal axis is the plane offset.
        /// </summary>
        private static readonly Int3[,] FaceCorners =
        {
            { new Int3(1, 0, 0), new Int3(1, 1, 0), new Int3(1, 1, 1), new Int3(1, 0, 1) }, // +X
            { new Int3(0, 0, 0), new Int3(0, 0, 1), new Int3(0, 1, 1), new Int3(0, 1, 0) }, // -X
            { new Int3(0, 1, 0), new Int3(0, 1, 1), new Int3(1, 1, 1), new Int3(1, 1, 0) }, // +Y
            { new Int3(0, 0, 0), new Int3(1, 0, 0), new Int3(1, 0, 1), new Int3(0, 0, 1) }, // -Y
            { new Int3(0, 0, 1), new Int3(1, 0, 1), new Int3(1, 1, 1), new Int3(0, 1, 1) }, // +Z
            { new Int3(0, 0, 0), new Int3(0, 1, 0), new Int3(1, 1, 0), new Int3(1, 0, 0) }, // -Z
        };

        /// <summary>
        /// The standard three-neighbour level: 3 fully open down to 0 when
        /// both sides are opaque (the corner is then irrelevant).
        /// </summary>
        public static int Level(bool side1, bool side2, bool corner)
        {
            return side1 && side2
                ? 0
                : 3 - (side1 ? 1 : 0) - (side2 ? 1 : 0) - (corner ? 1 : 0);
        }

        /// <summary>Quantises a 0..3 level to the ADR-0007 byte.</summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="level"/> lies outside <c>[0, 3]</c>.
        /// </exception>
        public static byte Encode(int level)
        {
            if (level < 0 || level > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(level), level, "An AO level must be in [0, 3].");
            }

            return (byte)(level * LevelStep);
        }

        /// <summary>Quantised AO byte for one face vertex.</summary>
        public static byte Compute(
            ChunkSnapshot chunk,
            NeighbourSnapshot neighbours,
            IBlockRegistry blocks,
            Int3 cell,
            int face,
            int corner)
        {
            return Encode(ComputeLevel(chunk, neighbours, blocks, cell, face, corner));
        }

        /// <summary>
        /// The 0..3 AO level of the corner <paramref name="corner"/> of face
        /// <paramref name="face"/> of <paramref name="cell"/>, sampling the
        /// two side cells and the diagonal corner cell on the outside of the
        /// face.
        /// </summary>
        public static int ComputeLevel(
            ChunkSnapshot chunk,
            NeighbourSnapshot neighbours,
            IBlockRegistry blocks,
            Int3 cell,
            int face,
            int corner)
        {
            if (chunk is null)
            {
                throw new ArgumentNullException(nameof(chunk));
            }

            if (neighbours is null)
            {
                throw new ArgumentNullException(nameof(neighbours));
            }

            if (blocks is null)
            {
                throw new ArgumentNullException(nameof(blocks));
            }

            int planeAxis = FacePlaneAxis[face];
            int aAxis = (planeAxis + 1) % 3;
            int bAxis = (planeAxis + 2) % 3;
            Int3 offset = FaceCorners[face, corner];
            int deltaA = Component(offset, aAxis) == 0 ? -1 : 1;
            int deltaB = Component(offset, bAxis) == 0 ? -1 : 1;
            Int3 outward = cell + FaceNormals[face];
            Int3 sideA = Step(outward, aAxis, deltaA);
            Int3 sideB = Step(outward, bAxis, deltaB);
            Int3 diagonal = Step(sideA, bAxis, deltaB);

            bool opaqueA = IsOpaque(chunk, neighbours, blocks, cell, sideA);
            bool opaqueB = IsOpaque(chunk, neighbours, blocks, cell, sideB);
            bool opaqueCorner = IsOpaque(chunk, neighbours, blocks, cell, diagonal);
            return Level(opaqueA, opaqueB, opaqueCorner);
        }

        private static bool IsOpaque(
            ChunkSnapshot chunk,
            NeighbourSnapshot neighbours,
            IBlockRegistry blocks,
            Int3 origin,
            Int3 target)
        {
            if (target.X >= 0 && target.X < ChunkMath.ChunkSize
                && target.Y >= 0 && target.Y < ChunkMath.ChunkSize
                && target.Z >= 0 && target.Z < ChunkMath.ChunkSize)
            {
                return blocks.Get(chunk.Get(target)).Opaque;
            }

            Int3 world = ChunkMath.ToWorld(chunk.Coord, origin) + (target - origin);
            return blocks.Get(neighbours.GetCell(world)).Opaque;
        }

        private static int Component(Int3 value, int axis)
        {
            switch (axis)
            {
                case 0:
                    return value.X;
                case 1:
                    return value.Y;
                default:
                    return value.Z;
            }
        }

        private static Int3 Step(Int3 cell, int axis, int delta)
        {
            switch (axis)
            {
                case 0:
                    return new Int3(cell.X + delta, cell.Y, cell.Z);
                case 1:
                    return new Int3(cell.X, cell.Y + delta, cell.Z);
                default:
                    return new Int3(cell.X, cell.Y, cell.Z + delta);
            }
        }
    }
}
