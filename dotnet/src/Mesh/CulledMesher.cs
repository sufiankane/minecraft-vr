using System;
using Cubeglass.CoreMath;
using Cubeglass.Voxel;

namespace Cubeglass.Mesh
{
    /// <summary>
    /// The reference face-culling mesher (S3 WI1): one quad per exposed face
    /// with no greedy merging, and real per-vertex ambient occlusion.
    /// </summary>
    /// <remarks>
    /// Determinism (ADR-0007): cells are visited <c>z</c>, then <c>y</c>, then
    /// <c>x</c>, and each cell's faces in the fixed order <c>+X, -X, +Y, -Y,
    /// +Z, -Z</c>, so the same input always yields byte-identical streams. A
    /// face is emitted unless the neighbouring cell holds an opaque block
    /// (<see cref="BlockDefinition.Opaque"/>); non-opaque neighbours never
    /// cull. Border faces read the neighbour through
    /// <see cref="NeighbourSnapshot"/>, where an absent neighbour reads as air.
    /// Quad corners are ordered counter-clockwise seen from outside in the
    /// internal right-handed frame, so <c>cross(v1 - v0, v2 - v0)</c> equals
    /// the stored outward normal. UVs use the block's atlas tile for the face
    /// orientation: <c>+Y</c>/<c>-Y</c> use Top, <c>+Z</c>/<c>-Z</c> use
    /// Front and <c>+X</c>/<c>-X</c> use Side. Each vertex's <c>Ao</c> byte is
    /// the ADR-0007 quantised three-neighbour level computed by
    /// <see cref="AmbientOcclusion"/>, whose samples also cross the chunk
    /// border through the neighbour snapshot. The five streams are rented from
    /// the <see cref="MeshBufferPool"/> this mesher was constructed with, so a
    /// warmed-up build and release allocates nothing. A mesher is
    /// single-threaded: an instance is safe on one thread at a time, and
    /// parallel callers must construct a distinct pool (and mesher) per worker
    /// (ADR-0007).
    /// </remarks>
    public sealed class CulledMesher : IChunkMesher, IMeshEmitter
    {
        private const int FaceCount = 6;
        private const int CornersPerQuad = 4;
        private const int DefaultTilesPerRow = 16;
        private const int DefaultTileSize = 16;

        /// <summary>
        /// Face order <c>+X, -X, +Y, -Y, +Z, -Z</c> (ADR-0007).
        /// </summary>
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

        /// <summary>
        /// The four cell-relative corners of each face in the order that winds
        /// counter-clockwise seen from outside (ADR-0007).
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

        private readonly AtlasLayout _layout;
        private readonly MeshBufferPool _pool;

        /// <summary>Creates a mesher over the default 16x16 atlas tiles.</summary>
        public CulledMesher()
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
        public CulledMesher(AtlasLayout layout)
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
        public CulledMesher(AtlasLayout layout, MeshBufferPool pool)
        {
            _layout = layout ?? throw new ArgumentNullException(nameof(layout));
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        }

        public MeshData Build(ChunkSnapshot chunk, NeighbourSnapshot neighbours, IBlockRegistry blocks)
        {
            return MesherBuild.Run(_pool, this, chunk, neighbours, blocks);
        }

        /// <summary>
        /// Appends one quad per exposed face, cells in <c>z, y, x</c> order
        /// and faces in the fixed ADR-0007 order.
        /// </summary>
        void IMeshEmitter.Emit(
            ChunkSnapshot chunk,
            NeighbourSnapshot neighbours,
            IBlockRegistry blocks,
            ref MeshBuffers buffers)
        {
            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int y = 0; y < ChunkMath.ChunkSize; y++)
                {
                    for (int x = 0; x < ChunkMath.ChunkSize; x++)
                    {
                        var local = new Int3(x, y, z);
                        BlockId id = chunk.Get(local);
                        if (id == BlockId.Air)
                        {
                            continue;
                        }

                        BlockDefinition definition = blocks.Get(id);
                        for (int face = 0; face < FaceCount; face++)
                        {
                            if (IsFaceHidden(chunk, neighbours, blocks, local, FaceDirections[face]))
                            {
                                continue;
                            }

                            AppendQuad(
                                ref buffers,
                                chunk,
                                neighbours,
                                blocks,
                                face,
                                AtlasIndex(definition, face),
                                local);
                        }
                    }
                }
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

        private void AppendQuad(
            ref MeshBuffers buffers,
            ChunkSnapshot chunk,
            NeighbourSnapshot neighbours,
            IBlockRegistry blocks,
            int face,
            int atlasIndex,
            Int3 cell)
        {
            Vector2f tileMin = AtlasMap.TileMin(_layout, atlasIndex);
            float tileSize = AtlasMap.TileUvSize(_layout).X;
            Vector3f normal = FaceNormals[face];
            int first = buffers.Positions.Count;

            for (int corner = 0; corner < CornersPerQuad; corner++)
            {
                Int3 offset = FaceCorners[face, corner];
                buffers.Positions.Add(new Vector3f(cell.X + offset.X, cell.Y + offset.Y, cell.Z + offset.Z));
                buffers.Normals.Add(normal);
                buffers.Uvs.Add(new Vector2f(
                    (corner == 1 || corner == 2) ? tileMin.X + tileSize : tileMin.X,
                    (corner >= 2) ? tileMin.Y + tileSize : tileMin.Y));
                buffers.Ao.Add(AmbientOcclusion.Compute(chunk, neighbours, blocks, cell, face, corner));
            }

            buffers.Indices.Add(first + 0);
            buffers.Indices.Add(first + 1);
            buffers.Indices.Add(first + 2);
            buffers.Indices.Add(first + 0);
            buffers.Indices.Add(first + 2);
            buffers.Indices.Add(first + 3);
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
