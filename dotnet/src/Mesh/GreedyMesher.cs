using System;
using Cubeglass.CoreMath;
using Cubeglass.Voxel;

namespace Cubeglass.Mesh
{
    /// <summary>
    /// The greedy mesher (S3 WI2): merges coplanar, same-block, same-face-
    /// orientation exposed faces into maximal rectangles per chunk slice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Traversal and merge order are pinned so the same input always yields
    /// byte-identical streams. Faces are emitted orientation-major in the
    /// reference order <c>+X, -X, +Y, -Y, +Z, -Z</c> (ADR-0007); each
    /// orientation is sliced along its normal axis from <c>0</c> upward, and
    /// every slice mask is scanned with <c>u</c> ascending in the inner loop
    /// and <c>v</c> ascending in the outer loop along the face's U and V axes.
    /// The first unmatched mask cell starts a rectangle that extends along
    /// <c>u</c> to the maximal equal run and then along <c>v</c> while whole
    /// rows stay equal, so equal exposed faces merge into maximal axis-aligned
    /// rectangles. No LINQ, closures or unordered iteration is used.
    /// </para>
    /// <para>
    /// Culling matches <see cref="CulledMesher"/>: a face is exposed unless
    /// its neighbour (centre chunk first, then <see cref="NeighbourSnapshot"/>,
    /// where an absent neighbour reads as air) holds an opaque block, so
    /// non-opaque neighbours never cull and there is never a face between two
    /// opaque cells. The mask carries the block id, so different blocks never
    /// merge; non-opaque and opaque faces stay separate. The mask and the five
    /// streams are rented from the <see cref="MeshBufferPool"/> this mesher was
    /// constructed with, so a warmed-up build and release allocates nothing. A
    /// mesher is single-threaded: an instance is safe on one thread at a time,
    /// and parallel callers must construct a distinct pool (and mesher) per
    /// worker (ADR-0007).
    /// </para>
    /// <para>
    /// Ambient occlusion (ADR-0007): every exposed cell's four per-cell AO
    /// levels are computed by <see cref="AmbientOcclusion"/> and packed, two
    /// bits per corner in the ADR-0007 corner order, into the mask value next
    /// to the block id. Because the merge scan compares whole packed values,
    /// two cells merge only when their block id and all four per-cell AO bytes
    /// are equal — the conservative merge rule (R22) — so a merged <c>W x H</c>
    /// quad stores exactly the per-cell AO pattern of every cell it covers,
    /// and geometrically mergeable cells whose AO differs stay separate quads.
    /// </para>
    /// <para>
    /// UV rule: a merged <c>W x H</c> quad (W cells along the face's U axis,
    /// H along V) spans <c>W</c> tiles in U and <c>H</c> tiles in V. Its four
    /// corners use the reference pattern in tile units,
    /// <c>(0,0), (W,0), (W,H), (0,H)</c>, so
    /// <c>uv = TileMin + TileUvSize * (cornerU * W, cornerV * H)</c> from
    /// <see cref="AtlasMap"/>; a 1x1 quad reproduces the reference UVs exactly
    /// and larger quads repeat the tile once per covered cell (UVs may leave
    /// <c>[0,1]</c> and rely on texture wrap). The U/V axes follow the
    /// ADR-0007 corner order: <c>+X</c> U=+Y,V=+Z; <c>-X</c> U=+Z,V=+Y;
    /// <c>+Y</c> U=+Z,V=+X; <c>-Y</c> U=+X,V=+Z; <c>+Z</c> U=+X,V=+Y;
    /// <c>-Z</c> U=+Y,V=+X.
    /// </para>
    /// </remarks>
    public sealed class GreedyMesher : IChunkMesher, IMeshEmitter
    {
        private const int FaceCount = 6;
        private const int CornersPerQuad = 4;
        private const int DefaultTilesPerRow = 16;
        private const int DefaultTileSize = 16;
        private const int SliceSize = ChunkMath.ChunkSize;
        private const int MaskSize = SliceSize * SliceSize;
        private const int AoBitsPerCorner = 2;
        private const int AoLevelMask = (1 << AoBitsPerCorner) - 1;
        private const int AoBitsMask = (1 << (CornersPerQuad * AoBitsPerCorner)) - 1;
        private const int IdShift = CornersPerQuad * AoBitsPerCorner;

        /// <summary>Face order <c>+X, -X, +Y, -Y, +Z, -Z</c> (ADR-0007).</summary>
        private static readonly Int3[] FaceDirections =
        {
            new Int3(1, 0, 0),
            new Int3(-1, 0, 0),
            new Int3(0, 1, 0),
            new Int3(0, -1, 0),
            new Int3(0, 0, 1),
            new Int3(0, 0, -1),
        };

        private static readonly Vector3f[] FaceNormals =
        {
            new Vector3f(1.0F, 0.0F, 0.0F),
            new Vector3f(-1.0F, 0.0F, 0.0F),
            new Vector3f(0.0F, 1.0F, 0.0F),
            new Vector3f(0.0F, -1.0F, 0.0F),
            new Vector3f(0.0F, 0.0F, 1.0F),
            new Vector3f(0.0F, 0.0F, -1.0F),
        };

        /// <summary>Normal axis of each face: 0=X, 1=Y, 2=Z.</summary>
        private static readonly int[] FacePlaneAxis = { 0, 0, 1, 1, 2, 2 };

        /// <summary>Whether each face points along its positive normal axis.</summary>
        private static readonly bool[] FacePositive = { true, false, true, false, true, false };

        /// <summary>The world axis that maps to the face's U tile axis.</summary>
        private static readonly int[] FaceUAxis = { 1, 2, 2, 0, 0, 1 };

        /// <summary>The world axis that maps to the face's V tile axis.</summary>
        private static readonly int[] FaceVAxis = { 2, 1, 0, 2, 1, 0 };

        /// <summary>
        /// For each face, which source value (0=plane, 1=u, 2=v) feeds each
        /// world axis (X, Y, Z) in the ADR-0007 corner order.
        /// </summary>
        private static readonly int[][] FacePositionMap =
        {
            new[] { 0, 1, 2 },
            new[] { 0, 2, 1 },
            new[] { 2, 0, 1 },
            new[] { 1, 0, 2 },
            new[] { 1, 2, 0 },
            new[] { 2, 1, 0 },
        };

        /// <summary>Corner tile offsets in U for the ADR-0007 corner order.</summary>
        private static readonly int[] CornerU = { 0, 1, 1, 0 };

        /// <summary>Corner tile offsets in V for the ADR-0007 corner order.</summary>
        private static readonly int[] CornerV = { 0, 0, 1, 1 };

        private readonly AtlasLayout _layout;
        private readonly MeshBufferPool _pool;

        /// <summary>Creates a mesher over the default 16x16 atlas tiles.</summary>
        public GreedyMesher()
            : this(new AtlasLayout(DefaultTilesPerRow, DefaultTileSize))
        {
        }

        /// <summary>
        /// Creates a mesher over <paramref name="layout"/> that rents from
        /// <see cref="MeshBufferPool.Shared"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="layout"/> is null.
        /// </exception>
        public GreedyMesher(AtlasLayout layout)
            : this(layout, MeshBufferPool.Shared)
        {
        }

        /// <summary>
        /// Creates a mesher over <paramref name="layout"/> that rents from
        /// <paramref name="pool"/>. Pass a pool owned by the calling worker to
        /// mesh on several threads (ADR-0007).
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="layout"/> or <paramref name="pool"/> is null.
        /// </exception>
        public GreedyMesher(AtlasLayout layout, MeshBufferPool pool)
        {
            _layout = layout ?? throw new ArgumentNullException(nameof(layout));
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        }

        public MeshData Build(ChunkSnapshot chunk, NeighbourSnapshot neighbours, IBlockRegistry blocks)
        {
            return MesherBuild.Run(_pool, this, chunk, neighbours, blocks);
        }

        /// <summary>
        /// Builds and merges the slice masks in orientation-major order,
        /// renting the scratch mask from this mesher's pool.
        /// </summary>
        void IMeshEmitter.Emit(
            ChunkSnapshot chunk,
            NeighbourSnapshot neighbours,
            IBlockRegistry blocks,
            ref MeshBuffers buffers)
        {
            int[] mask = _pool.Rent<int>(MaskSize);
            try
            {
                for (int face = 0; face < FaceCount; face++)
                {
                    for (int slice = 0; slice < ChunkMath.ChunkSize; slice++)
                    {
                        BuildMask(mask, chunk, neighbours, blocks, face, slice);
                        MergeMask(mask, ref buffers, blocks, face, slice);
                    }
                }
            }
            finally
            {
                _pool.Return(mask);
            }
        }

        /// <summary>
        /// Fills the slice mask for one face orientation: a cell carries its
        /// block id and packed per-cell AO levels when the face is exposed,
        /// and zero when the cell is air or an opaque neighbour hides the
        /// face.
        /// </summary>
        private static void BuildMask(
            int[] mask,
            ChunkSnapshot chunk,
            NeighbourSnapshot neighbours,
            IBlockRegistry blocks,
            int face,
            int slice)
        {
            int planeAxis = FacePlaneAxis[face];
            int uAxis = FaceUAxis[face];
            int vAxis = FaceVAxis[face];

            for (int v = 0; v < SliceSize; v++)
            {
                for (int u = 0; u < SliceSize; u++)
                {
                    Int3 cell = CellAt(planeAxis, slice, uAxis, u, vAxis, v);
                    BlockId id = chunk.Get(cell);
                    if (id == BlockId.Air || IsFaceHidden(chunk, neighbours, blocks, cell, FaceDirections[face]))
                    {
                        mask[(v * SliceSize) + u] = 0;
                    }
                    else
                    {
                        int aoBits = 0;
                        for (int corner = 0; corner < CornersPerQuad; corner++)
                        {
                            aoBits |= AmbientOcclusion.ComputeLevel(chunk, neighbours, blocks, cell, face, corner)
                                << (corner * AoBitsPerCorner);
                        }

                        mask[(v * SliceSize) + u] = (id.Value << IdShift) | aoBits;
                    }
                }
            }
        }

        /// <summary>
        /// Merges the non-zero mask rectangles and appends one quad each, then
        /// clears every covered cell so the scan covers each face once.
        /// </summary>
        private void MergeMask(
            int[] mask,
            ref MeshBuffers buffers,
            IBlockRegistry blocks,
            int face,
            int slice)
        {
            for (int v = 0; v < SliceSize; v++)
            {
                for (int u = 0; u < SliceSize; u++)
                {
                    int value = mask[(v * SliceSize) + u];
                    if (value == 0)
                    {
                        continue;
                    }

                    int width = 1;
                    while (u + width < SliceSize && mask[(v * SliceSize) + u + width] == value)
                    {
                        width++;
                    }

                    int height = 1;
                    while (v + height < SliceSize && RowMatches(mask, u, width, v + height, value))
                    {
                        height++;
                    }

                    BlockDefinition definition = blocks.Get(new BlockId((ushort)(value >> IdShift)));
                    AppendQuad(
                        ref buffers,
                        face,
                        AtlasIndex(definition, face),
                        value & AoBitsMask,
                        slice,
                        u,
                        v,
                        width,
                        height);
                    ClearMask(mask, u, v, width, height);
                }
            }
        }

        private static bool RowMatches(int[] mask, int u, int width, int v, int value)
        {
            int row = v * SliceSize;
            for (int i = 0; i < width; i++)
            {
                if (mask[row + u + i] != value)
                {
                    return false;
                }
            }

            return true;
        }

        private static void ClearMask(int[] mask, int u, int v, int width, int height)
        {
            for (int row = v; row < v + height; row++)
            {
                for (int column = u; column < u + width; column++)
                {
                    mask[(row * SliceSize) + column] = 0;
                }
            }
        }

        private void AppendQuad(
            ref MeshBuffers buffers,
            int face,
            int atlasIndex,
            int aoBits,
            int slice,
            int u0,
            int v0,
            int width,
            int height)
        {
            Vector2f tileMin = AtlasMap.TileMin(_layout, atlasIndex);
            Vector2f tileSize = AtlasMap.TileUvSize(_layout);
            Vector3f normal = FaceNormals[face];
            int plane = slice + (FacePositive[face] ? 1 : 0);
            int[] map = FacePositionMap[face];
            int first = buffers.Positions.Count;

            for (int corner = 0; corner < CornersPerQuad; corner++)
            {
                int u = u0 + (CornerU[corner] * width);
                int v = v0 + (CornerV[corner] * height);
                buffers.Positions.Add(new Vector3f(
                    Component(map[0], plane, u, v),
                    Component(map[1], plane, u, v),
                    Component(map[2], plane, u, v)));
                buffers.Normals.Add(normal);
                buffers.Uvs.Add(new Vector2f(
                    tileMin.X + (CornerU[corner] * width * tileSize.X),
                    tileMin.Y + (CornerV[corner] * height * tileSize.Y)));
                buffers.Ao.Add(AmbientOcclusion.Encode((aoBits >> (corner * AoBitsPerCorner)) & AoLevelMask));
            }

            buffers.Indices.Add(first + 0);
            buffers.Indices.Add(first + 1);
            buffers.Indices.Add(first + 2);
            buffers.Indices.Add(first + 0);
            buffers.Indices.Add(first + 2);
            buffers.Indices.Add(first + 3);
        }

        private static float Component(int source, int plane, int u, int v)
        {
            switch (source)
            {
                case 0:
                    return plane;
                case 1:
                    return u;
                default:
                    return v;
            }
        }

        /// <summary>
        /// Builds the chunk-local cell at <paramref name="plane"/> on
        /// <paramref name="planeAxis"/> with components <paramref name="u"/>
        /// and <paramref name="v"/> on the face's U and V axes.
        /// </summary>
        private static Int3 CellAt(int planeAxis, int plane, int uAxis, int u, int vAxis, int v)
        {
            switch (planeAxis)
            {
                case 0:
                    return uAxis == 1 ? new Int3(plane, u, v) : new Int3(plane, v, u);
                case 1:
                    return uAxis == 0 ? new Int3(u, plane, v) : new Int3(v, plane, u);
                default:
                    return uAxis == 0 ? new Int3(u, v, plane) : new Int3(v, u, plane);
            }
        }

        private static bool IsFaceHidden(
            ChunkSnapshot chunk,
            NeighbourSnapshot neighbours,
            IBlockRegistry blocks,
            Int3 local,
            Int3 direction)
        {
            int x = local.X + direction.X;
            int y = local.Y + direction.Y;
            int z = local.Z + direction.Z;

            BlockId neighbour;
            if (x >= 0 && x < ChunkMath.ChunkSize
                && y >= 0 && y < ChunkMath.ChunkSize
                && z >= 0 && z < ChunkMath.ChunkSize)
            {
                neighbour = chunk.Get(new Int3(x, y, z));
            }
            else
            {
                Int3 world = ChunkMath.ToWorld(chunk.Coord, local) + direction;
                neighbour = neighbours.GetCell(world);
            }

            return blocks.Get(neighbour).Opaque;
        }

        private static int AtlasIndex(BlockDefinition definition, int face)
        {
            switch (face)
            {
                case 2: // +Y
                case 3: // -Y, which maps to the top texture in S3.
                    return definition.AtlasIndexTop;
                case 4: // +Z
                case 5: // -Z
                    return definition.AtlasIndexFront;
                default: // +X / -X
                    return definition.AtlasIndexSide;
            }
        }
    }
}
