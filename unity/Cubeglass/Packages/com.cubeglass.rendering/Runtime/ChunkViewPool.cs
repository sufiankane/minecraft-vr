using System;
using System.Collections.Generic;
using Cubeglass.CoreMath;
using Cubeglass.Mesh;
using Cubeglass.Voxel;
using UnityEngine;

namespace Cubeglass.Unity.Rendering
{
    /// <summary>
    /// One pooled chunk renderer: a GameObject with a <see cref="MeshFilter"/>,
    /// a <see cref="MeshRenderer"/> and a reusable <see cref="UnityEngine.Mesh"/>.
    /// </summary>
    /// <remarks>
    /// Instances are created and owned by <see cref="ChunkViewPool"/>; the
    /// <see cref="Coord"/> and <see cref="IsActive"/> state is managed by the
    /// pool and its <see cref="ChunkViewManager"/>.
    /// </remarks>
    public sealed class ChunkView
    {
        internal ChunkView(GameObject gameObject, MeshFilter filter, MeshRenderer renderer, UnityEngine.Mesh mesh)
        {
            GameObject = gameObject;
            Filter = filter;
            Renderer = renderer;
            Mesh = mesh;
        }

        /// <summary>The view GameObject (parented under the pool's parent).</summary>
        public GameObject GameObject { get; }

        /// <summary>The mesh filter holding <see cref="Mesh"/>.</summary>
        public MeshFilter Filter { get; }

        /// <summary>The renderer drawing <see cref="Mesh"/>.</summary>
        public MeshRenderer Renderer { get; }

        /// <summary>The reused mesh; rewritten in place on every upload.</summary>
        public UnityEngine.Mesh Mesh { get; }

        /// <summary>The chunk currently shown by this view.</summary>
        public ChunkCoord Coord { get; internal set; }

        /// <summary>True while the view is checked out of the pool.</summary>
        public bool IsActive { get; internal set; }
    }

    /// <summary>
    /// Pools chunk views and copies <see cref="MeshData"/> into their
    /// <see cref="UnityEngine.Mesh"/> objects (S7 Task 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Views are created on demand up to <see cref="Capacity"/>; a chunk that
    /// unloads returns its view to the pool and the next load reuses it. The
    /// mesh is rebuilt with <c>Mesh.Clear()</c> plus the <c>Set*</c> stream
    /// APIs, fed from reused <see cref="List{T}"/> buffers (their backing
    /// arrays survive across uploads), so a warmed pool performs no managed
    /// allocation per upload beyond the mesh data itself.
    /// </para>
    /// <para>
    /// Winding: <see cref="GreedyMesher"/> emits every quad counter-clockwise
    /// as seen from outside in the internal right-handed frame (ADR-0007), so
    /// <c>cross(v1 - v0, v2 - v0)</c> equals the stored outward normal.
    /// Positions and normals are copied component-for-component with no axis
    /// mirroring, and Unity's rule ("the corners go around clockwise as you
    /// look down on the visible outer surface") makes exactly those triangles
    /// front-facing whose <c>cross(v1 - v0, v2 - v0)</c> points toward the
    /// viewer. The ADR-0007 index order is therefore already the Unity
    /// front-face order and must NOT be reversed. This equivalence holds only
    /// because the voxel lattice coordinates are used as Unity coordinates;
    /// if mesh positions are ever routed through the ADR-0004
    /// <c>UnityConvert</c> Z mirror, the mirror flips the winding and the
    /// indices have to be reversed in the same change.
    /// </para>
    /// <para>
    /// <see cref="Upload"/> takes ownership of the <see cref="MeshData"/> and
    /// releases it exactly once, even when the mesh copy throws; the
    /// <see cref="MeshDataBuilds"/> / <see cref="MeshDataReleases"/> counters
    /// pin that contract for tests.
    /// </para>
    /// </remarks>
    public sealed class ChunkViewPool : IDisposable
    {
        private readonly Transform parent;
        private readonly int capacity;
        private readonly Stack<ChunkView> pooled = new Stack<ChunkView>();
        private readonly List<ChunkView> created = new List<ChunkView>();
        private readonly List<Vector3> positions = new List<Vector3>();
        private readonly List<Vector3> normals = new List<Vector3>();
        private readonly List<Vector2> uvs = new List<Vector2>();
        private readonly List<Color32> colors = new List<Color32>();
        private readonly List<int> indices = new List<int>();
        private Material material;
        private long meshDataBuilds;
        private long meshDataReleases;

        /// <summary>
        /// Creates a pool whose views are parented under <paramref name="parent"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="parent"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
        public ChunkViewPool(Transform parent, int capacity)
        {
            if (parent == null)
            {
                throw new ArgumentNullException(nameof(parent));
            }

            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
            }

            this.parent = parent;
            this.capacity = capacity;
        }

        /// <summary>The maximum number of views the pool may create.</summary>
        public int Capacity
        {
            get { return capacity; }
        }

        /// <summary>The number of views currently checked out.</summary>
        public int ActiveViews
        {
            get { return created.Count - pooled.Count; }
        }

        /// <summary>The number of views waiting for reuse.</summary>
        public int PooledViews
        {
            get { return pooled.Count; }
        }

        /// <summary>The high-water mark: views created over the pool's lifetime.</summary>
        public int CreatedViews
        {
            get { return created.Count; }
        }

        /// <summary>The number of mesh data copies handed to <see cref="Upload"/>.</summary>
        public long MeshDataBuilds
        {
            get { return meshDataBuilds; }
        }

        /// <summary>The number of mesh data copies released; equal to builds when nothing leaked.</summary>
        public long MeshDataReleases
        {
            get { return meshDataReleases; }
        }

        /// <summary>Mesh data built but not yet released; zero outside an upload.</summary>
        public long OutstandingMeshData
        {
            get { return meshDataBuilds - meshDataReleases; }
        }

        /// <summary>The material assigned to newly created views; may be null.</summary>
        public Material Material
        {
            get { return material; }
            set
            {
                material = value;
                for (int i = 0; i < created.Count; i++)
                {
                    created[i].Renderer.sharedMaterial = material;
                }
            }
        }

        /// <summary>
        /// Checks out a view: a pooled one when available, otherwise a newly
        /// created one up to <see cref="Capacity"/>. False when the cap is
        /// reached and no view is pooled.
        /// </summary>
        public bool TryAcquire(out ChunkView view)
        {
            if (pooled.Count > 0)
            {
                view = pooled.Pop();
            }
            else if (created.Count < capacity)
            {
                view = Create();
                created.Add(view);
            }
            else
            {
                view = null;
                return false;
            }

            view.IsActive = true;
            view.GameObject.SetActive(true);
            return true;
        }

        /// <summary>
        /// Returns an active view to the pool and hides it.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="view"/> is null.</exception>
        /// <exception cref="InvalidOperationException">The view is not active (already released or foreign).</exception>
        public void Release(ChunkView view)
        {
            if (view == null)
            {
                throw new ArgumentNullException(nameof(view));
            }

            if (!view.IsActive)
            {
                throw new InvalidOperationException("The chunk view is already released.");
            }

            view.IsActive = false;
            view.Coord = default;
            view.GameObject.SetActive(false);
            pooled.Push(view);
        }

        /// <summary>
        /// Copies <paramref name="meshData"/> into the view's mesh and releases
        /// the mesh data exactly once (see the type remarks for the winding
        /// contract). The caller must not use the mesh data afterwards.
        /// </summary>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        /// <exception cref="InvalidOperationException">The view is not active.</exception>
        public void Upload(ChunkView view, MeshData meshData)
        {
            if (view == null)
            {
                throw new ArgumentNullException(nameof(view));
            }

            if (meshData == null)
            {
                throw new ArgumentNullException(nameof(meshData));
            }

            if (!view.IsActive)
            {
                throw new InvalidOperationException("Cannot upload into a released chunk view.");
            }

            meshDataBuilds++;
            try
            {
                CopyToMesh(view.Mesh, meshData);
            }
            finally
            {
                meshData.Release();
                meshDataReleases++;
            }
        }

        /// <summary>Destroys every view and its mesh.</summary>
        public void Dispose()
        {
            for (int i = 0; i < created.Count; i++)
            {
                ChunkView view = created[i];
                if (view.GameObject != null)
                {
                    DestroyObject(view.GameObject);
                }

                if (view.Mesh != null)
                {
                    DestroyObject(view.Mesh);
                }
            }

            created.Clear();
            pooled.Clear();
        }

        private ChunkView Create()
        {
            var gameObject = new GameObject("ChunkView");
            gameObject.transform.SetParent(parent, false);
            MeshFilter filter = gameObject.AddComponent<MeshFilter>();
            MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
            var mesh = new UnityEngine.Mesh { name = "ChunkViewMesh" };
            mesh.MarkDynamic();
            filter.sharedMesh = mesh;
            renderer.sharedMaterial = material;
            gameObject.SetActive(false);
            return new ChunkView(gameObject, filter, renderer, mesh);
        }

        private void CopyToMesh(UnityEngine.Mesh mesh, MeshData data)
        {
            positions.Clear();
            normals.Clear();
            uvs.Clear();
            colors.Clear();
            indices.Clear();

            ReadOnlySpan<Vector3f> sourcePositions = data.Positions.Span;
            ReadOnlySpan<Vector3f> sourceNormals = data.Normals.Span;
            ReadOnlySpan<Vector2f> sourceUvs = data.Uvs.Span;
            ReadOnlySpan<byte> sourceAo = data.Ao.Span;
            for (int i = 0; i < data.VertexCount; i++)
            {
                Vector3f position = sourcePositions[i];
                positions.Add(new Vector3(position.X, position.Y, position.Z));

                Vector3f normal = sourceNormals[i];
                normals.Add(new Vector3(normal.X, normal.Y, normal.Z));

                Vector2f uv = sourceUvs[i];
                uvs.Add(new Vector2(uv.X, uv.Y));

                byte ao = sourceAo[i];
                colors.Add(new Color32(ao, ao, ao, 255));
            }

            ReadOnlySpan<int> sourceIndices = data.Indices.Span;
            for (int i = 0; i < data.IndexCount; i++)
            {
                indices.Add(sourceIndices[i]);
            }

            mesh.Clear();
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.SetIndices(indices, 0, data.IndexCount, MeshTopology.Triangles, 0, false);
            mesh.bounds = BoundsFrom(data.Min, data.Max);
        }

        private static Bounds BoundsFrom(Vector3f min, Vector3f max)
        {
            var center = new Vector3((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f, (min.Z + max.Z) * 0.5f);
            var size = new Vector3(max.X - min.X, max.Y - min.Y, max.Z - min.Z);
            return new Bounds(center, size);
        }

        private static void DestroyObject(UnityEngine.Object target)
        {
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
