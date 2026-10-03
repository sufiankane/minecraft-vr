using System;
using System.Collections.Generic;
using Cubeglass.CoreMath;
using Cubeglass.Mesh;
using Cubeglass.Streaming;
using Cubeglass.Voxel;
using UnityEngine;

namespace Cubeglass.Unity.Rendering
{
    /// <summary>
    /// Turns scheduler actions into pooled chunk views (S7 Task 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="OnLoad"/> accepts every load the scheduler emits (loads
    /// cannot be refused) but generates at most one chunk per Unity frame; the
    /// rest wait in a deferred queue and are reported to the scheduler through
    /// <see cref="ChunkStreamingScheduler.NotifyMeshReady"/> once generated.
    /// <see cref="OnUpload"/> meshes with one <see cref="GreedyMesher"/> and one
    /// <see cref="MeshBufferPool"/> owned by this component, and admits at most
    /// <c>StreamingConfig.MaxMeshUploadsPerFrame</c> uploads per frame (the
    /// scheduler counts down the same budget; this gate keeps the invariant if
    /// <see cref="OnUpload"/> is called directly). <see cref="OnUnload"/>
    /// returns the view to the pool.
    /// </para>
    /// <para>
    /// Views are parented to this component's transform, so keep the
    /// component's GameObject at the world origin (the <see cref="StreamingRuntime"/>
    /// does); chunk-local geometry is uploaded in Unity space through the
    /// single ADR-0004 <see cref="UnityConvert"/> mirror and the view is placed
    /// at the converted chunk origin (ADR-0011, R52).
    /// </para>
    /// <para>
    /// Edits run through <see cref="World.ChunkChanged"/>: the manager dirties
    /// the changed chunk plus every loaded chunk in its Chebyshev-1
    /// neighbourhood and rebuilds them on later frames through
    /// <see cref="ProcessDirtyRemeshes"/>, sharing the upload budget with new
    /// chunks. The event reports the chunk, not the edited cell, so the
    /// neighbourhood is the provable superset of
    /// <see cref="ChunkEditPropagation.GetAffectedChunks"/> over every cell the
    /// chunk can contain; meshing a neighbour that did not actually change
    /// rebuilds an identical mesh and is correctness-safe.
    /// </para>
    /// <para>
    /// The manager keeps its own loaded-chunk dictionary; the <see cref="World"/>
    /// used for neighbour snapshots has no unload API, so it accumulates chunks
    /// and is rebuilt from the live set once it retains about twice the live
    /// count (see <see cref="WorldCompactions"/>). Each upload snapshots the
    /// chunk and its 26 neighbours for the mesher, allocating transient copies
    /// on the synchronous S7 path; caching snapshots and meshing off the main
    /// thread are later work. Mesh data is released inside
    /// <see cref="ChunkViewPool.Upload"/> exactly once per mesh; see that type
    /// for the winding contract.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ChunkViewManager : MonoBehaviour, IStreamingTarget
    {
        /// <summary>The number of chunks in the Chebyshev-1 neighbourhood of a chunk.</summary>
        public const int RemeshNeighbourhoodSize = 27;

        private readonly Dictionary<ChunkCoord, Chunk> chunks = new Dictionary<ChunkCoord, Chunk>();
        private readonly Dictionary<ChunkCoord, ChunkView> views = new Dictionary<ChunkCoord, ChunkView>();
        private readonly Queue<ChunkCoord> deferredLoads = new Queue<ChunkCoord>();
        private readonly HashSet<ChunkCoord> deferredSet = new HashSet<ChunkCoord>();
        private readonly HashSet<ChunkCoord> dirtySet = new HashSet<ChunkCoord>();
        private readonly Queue<ChunkCoord> dirtyQueue = new Queue<ChunkCoord>();
        private readonly ChunkCoord[] neighbourhood = new ChunkCoord[RemeshNeighbourhoodSize];

        private ChunkViewPool pool;
        private MeshBufferPool bufferPool;
        private GreedyMesher mesher;
        private IWorldGenerator generator;
        private IBlockRegistry blocks = SliceBlockRegistry.Default;
        private ChunkStreamingScheduler scheduler;
        private World world;
        private long deltasLoaded;
        private long deltaEditsApplied;
        private long liveDeltasReapplied;
        private long liveEditsReapplied;
        private Material viewMaterial;
        private long seed;
        private int maxViews = 2048;
        private int maxMeshUploadsPerFrame = 4;
        private int lastGenerateFrame = int.MinValue;
        private int lastUploadFrame = int.MinValue;
        private int uploadsThisFrame;
        private int worldChunks;
        private long generatedChunks;
        private long uploadedChunks;
        private long unloadedChunks;
        private long remeshedChunks;
        private long worldCompactions;
        private bool initialized;

        /// <summary>
        /// The live world every loaded chunk is written into. Gameplay edits go
        /// through <see cref="World.Apply"/> so the same cells drive collision,
        /// targeting and the next mesh build.
        /// </summary>
        public World World
        {
            get { return world; }
        }

        /// <summary>The block definitions used for meshing; defaults to <see cref="SliceBlockRegistry.Default"/>.</summary>
        public IBlockRegistry Blocks
        {
            get { return blocks; }
            set { blocks = value != null ? value : throw new ArgumentNullException(nameof(value)); }
        }

        /// <summary>Optional material assigned to every view; may be null.</summary>
        public Material ViewMaterial
        {
            get { return viewMaterial; }
            set
            {
                viewMaterial = value;
                if (pool != null)
                {
                    pool.Material = value;
                }
            }
        }

        /// <summary>
        /// Optional persistence store consulted when a chunk is generated
        /// (S7 Task 4b): an existing delta is replayed over the generated
        /// baseline through <see cref="World.Apply"/> before the chunk is
        /// meshed, so edits survive a session. Loads happen on the main thread
        /// once per generated chunk; a store that faults is ignored and the
        /// chunk keeps its generated content.
        /// </summary>
        public IWorldStore Store { get; set; }

        /// <summary>
        /// Optional in-memory accumulated edit cache (S7 Task 4b fix round),
        /// normally the scene's <see cref="SaveBatches"/>. On every chunk
        /// generation, after a stored delta is replayed, the chunk's
        /// accumulated map is re-applied on top so an unload/reload keeps edits
        /// that have not been flushed; when a stored delta is loaded and no
        /// accumulated cells exist yet, the loaded cells are seeded into the
        /// cache so a later flush merges instead of replacing them.
        /// </summary>
        public IAppliedEditCache AppliedEdits { get; set; }

        /// <summary>Chunks that loaded a delta from <see cref="Store"/>.</summary>
        public long DeltasLoaded
        {
            get { return deltasLoaded; }
        }

        /// <summary>Edits replayed from deltas loaded out of <see cref="Store"/>.</summary>
        public long DeltaEditsApplied
        {
            get { return deltaEditsApplied; }
        }

        /// <summary>Chunks whose in-memory accumulated map was re-applied.</summary>
        public long LiveDeltasReapplied
        {
            get { return liveDeltasReapplied; }
        }

        /// <summary>Edits replayed from in-memory accumulated maps.</summary>
        public long LiveEditsReapplied
        {
            get { return liveEditsReapplied; }
        }

        /// <summary>The upload budget per frame, taken from the streaming config.</summary>
        public int MaxMeshUploadsPerFrame
        {
            get { return maxMeshUploadsPerFrame; }
        }

        /// <summary>The number of chunk views currently checked out.</summary>
        public int ActiveViews
        {
            get { return pool != null ? pool.ActiveViews : 0; }
        }

        /// <summary>The number of views waiting for reuse.</summary>
        public int PooledViews
        {
            get { return pool != null ? pool.PooledViews : 0; }
        }

        /// <summary>The view high-water mark.</summary>
        public int CreatedViews
        {
            get { return pool != null ? pool.CreatedViews : 0; }
        }

        /// <summary>The view pool's capacity.</summary>
        public int Capacity
        {
            get { return pool != null ? pool.Capacity : 0; }
        }

        /// <summary>Mesh data copies handed to the pool.</summary>
        public long MeshDataBuilds
        {
            get { return pool != null ? pool.MeshDataBuilds : 0; }
        }

        /// <summary>Mesh data copies released by the pool.</summary>
        public long MeshDataReleases
        {
            get { return pool != null ? pool.MeshDataReleases : 0; }
        }

        /// <summary>Mesh data built but not yet released; zero at rest.</summary>
        public long OutstandingMeshData
        {
            get { return pool != null ? pool.OutstandingMeshData : 0; }
        }

        /// <summary>The number of generated chunks currently resident.</summary>
        public int LoadedChunks
        {
            get { return chunks.Count; }
        }

        /// <summary>The number of loads waiting for their one-per-frame generation slot.</summary>
        public int DeferredLoads
        {
            get { return deferredSet.Count; }
        }

        /// <summary>The number of uploads performed in the current Unity frame.</summary>
        public int UploadedThisFrame
        {
            get { return uploadsThisFrame; }
        }

        /// <summary>Total chunks generated since initialization.</summary>
        public long GeneratedChunks
        {
            get { return generatedChunks; }
        }

        /// <summary>Total chunks uploaded since initialization.</summary>
        public long UploadedChunks
        {
            get { return uploadedChunks; }
        }

        /// <summary>Total chunks whose view was returned to the pool.</summary>
        public long UnloadedChunks
        {
            get { return unloadedChunks; }
        }

        /// <summary>Total view rebuilds driven by <see cref="World.ChunkChanged"/>.</summary>
        public long RemeshedChunks
        {
            get { return remeshedChunks; }
        }

        /// <summary>Chunks waiting for a dirty-remesh slot (with an active view).</summary>
        public int DirtyChunks
        {
            get { return dirtySet.Count; }
        }

        /// <summary>Times the neighbour-snapshot world was rebuilt to drop unloaded chunks.</summary>
        public long WorldCompactions
        {
            get { return worldCompactions; }
        }

        /// <summary>
        /// Fills <paramref name="destination"/> (at least
        /// <see cref="RemeshNeighbourhoodSize"/> entries) with the 27 chunks in
        /// the Chebyshev-1 neighbourhood of <paramref name="changed"/> and
        /// returns the count (always 27). This is the set of chunks a single
        /// edit can reach: for every cell of the changed chunk,
        /// <see cref="ChunkEditPropagation.GetAffectedChunks"/> is a subset of
        /// it, so dirtying the whole neighbourhood is never missing a view.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than 27.</exception>
        public static int FillRemeshNeighbourhood(ChunkCoord changed, ChunkCoord[] destination)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            if (destination.Length < RemeshNeighbourhoodSize)
            {
                throw new ArgumentException(
                    "The destination needs at least RemeshNeighbourhoodSize entries.",
                    nameof(destination));
            }

            int index = 0;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        destination[index] = new ChunkCoord(changed.X + dx, changed.Y + dy, changed.Z + dz);
                        index++;
                    }
                }
            }

            return index;
        }

        /// <summary>
        /// Wires the manager to a world seed and pool capacity without a
        /// scheduler (the deferred loads are then simply not reported).
        /// </summary>
        public void Initialize(StreamingConfig config, long worldSeed, int capacity)
        {
            Initialize(config, worldSeed, capacity, null, null);
        }

        /// <summary>
        /// Wires the manager to <paramref name="chunkScheduler"/>, which
        /// receives <see cref="ChunkStreamingScheduler.NotifyMeshReady"/> when a
        /// deferred chunk finishes generating.
        /// </summary>
        public void Initialize(StreamingConfig config, long worldSeed, int capacity, ChunkStreamingScheduler chunkScheduler)
        {
            Initialize(config, worldSeed, capacity, chunkScheduler, null);
        }

        /// <summary>
        /// Wires the manager, optionally with a custom world generator (tests
        /// inject a single-block generator).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
        public void Initialize(
            StreamingConfig config,
            long worldSeed,
            int capacity,
            ChunkStreamingScheduler chunkScheduler,
            IWorldGenerator worldGenerator)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
            }

            DisposeState();

            seed = worldSeed;
            maxViews = capacity;
            maxMeshUploadsPerFrame = config.MaxMeshUploadsPerFrame;
            scheduler = chunkScheduler;
            generator = worldGenerator != null ? worldGenerator : new TerrainGenerator();
            world = new World();
            world.ChunkChanged += HandleChunkChanged;
            pool = new ChunkViewPool(transform, capacity) { Material = viewMaterial };
            bufferPool = new MeshBufferPool();
            mesher = new GreedyMesher(new AtlasLayout(16, 16), bufferPool);
            initialized = true;
        }

        /// <summary>
        /// Queues a load; the first queued chunk of a frame is generated
        /// immediately, later ones are deferred (loads cannot be refused).
        /// </summary>
        public void OnLoad(ChunkCoord chunk)
        {
            EnsureInitialized();
            if (chunks.ContainsKey(chunk))
            {
                return;
            }

            if (deferredSet.Add(chunk))
            {
                deferredLoads.Enqueue(chunk);
            }

            ProcessDeferredLoads();
        }

        /// <summary>
        /// Drops the chunk data and returns its view (if any) to the pool. A
        /// queued load that has not generated yet is cancelled by the queue
        /// bookkeeping.
        /// </summary>
        public void OnUnload(ChunkCoord chunk)
        {
            if (!initialized)
            {
                return;
            }

            deferredSet.Remove(chunk);
            chunks.Remove(chunk);
            dirtySet.Remove(chunk);
            if (views.TryGetValue(chunk, out ChunkView view))
            {
                views.Remove(chunk);
                pool.Release(view);
                unloadedChunks++;
            }
        }

        /// <summary>
        /// Meshes and uploads one chunk. False leaves it pending: the chunk is
        /// not generated yet, the upload budget is spent, or the view pool is
        /// at its cap.
        /// </summary>
        public bool OnUpload(ChunkCoord chunk)
        {
            EnsureInitialized();
            if (views.ContainsKey(chunk))
            {
                return true;
            }

            if (!chunks.TryGetValue(chunk, out Chunk chunkData))
            {
                return false;
            }

            if (IsUploadBudgetExhausted())
            {
                return false;
            }

            if (!pool.TryAcquire(out ChunkView view))
            {
                return false;
            }

            try
            {
                ChunkSnapshot snapshot = chunkData.Snapshot();
                NeighbourSnapshot neighbours = world.CreateNeighbourSnapshot(chunk);
                MeshData meshData = mesher.Build(snapshot, neighbours, blocks);
                pool.Upload(view, meshData);
            }
            catch
            {
                pool.Release(view);
                throw;
            }

            view.Coord = chunk;
            view.GameObject.transform.localPosition = ConvertedChunkOrigin(chunk);
            views.Add(chunk, view);
            dirtySet.Remove(chunk);
            uploadsThisFrame++;
            uploadedChunks++;
            return true;
        }

        /// <summary>
        /// Rebuilds up to the remaining per-frame upload budget of chunks
        /// dirtied by <see cref="World.ChunkChanged"/> (S7 Task 4a). Chunks
        /// whose view is not active are dropped: their next load meshes fresh
        /// data anyway. Returns the number of views rebuilt.
        /// </summary>
        public int ProcessDirtyRemeshes()
        {
            EnsureInitialized();
            int processed = 0;
            int considered = 0;
            int queued = dirtyQueue.Count;
            while (considered < queued && dirtyQueue.Count > 0)
            {
                considered++;
                ChunkCoord chunk = dirtyQueue.Peek();
                if (!dirtySet.Contains(chunk))
                {
                    dirtyQueue.Dequeue();
                    continue;
                }

                if (!views.TryGetValue(chunk, out ChunkView view) ||
                    !chunks.TryGetValue(chunk, out Chunk chunkData))
                {
                    dirtySet.Remove(chunk);
                    dirtyQueue.Dequeue();
                    continue;
                }

                if (IsUploadBudgetExhausted())
                {
                    break;
                }

                ChunkSnapshot snapshot = chunkData.Snapshot();
                NeighbourSnapshot neighbours = world.CreateNeighbourSnapshot(chunk);
                MeshData meshData = mesher.Build(snapshot, neighbours, blocks);
                pool.Upload(view, meshData);
                dirtySet.Remove(chunk);
                dirtyQueue.Dequeue();
                uploadsThisFrame++;
                remeshedChunks++;
                processed++;
            }

            return processed;
        }

        /// <summary>
        /// Generates at most one deferred chunk for the current Unity frame and
        /// reports it ready to the scheduler. Returns 1 when a chunk was
        /// generated, 0 otherwise.
        /// </summary>
        public int ProcessDeferredLoads()
        {
            EnsureInitialized();
            if (deferredLoads.Count == 0 || Time.frameCount == lastGenerateFrame)
            {
                return 0;
            }

            while (deferredLoads.Count > 0)
            {
                ChunkCoord chunk = deferredLoads.Dequeue();
                if (!deferredSet.Remove(chunk) || chunks.ContainsKey(chunk))
                {
                    continue;
                }

                Chunk generated = generator.Generate(chunk, seed);
                world.LoadChunk(generated);

                // Replay a stored delta before the chunk joins the manager's
                // live set: HandleChunkChanged only dirties resident chunks, so
                // a fresh boot's replay does not schedule a remesh for a chunk
                // that has no view yet.
                ApplyStoredDelta(chunk);

                chunks.Add(chunk, generated);
                worldChunks++;
                generatedChunks++;
                lastGenerateFrame = Time.frameCount;
                CompactWorldIfNeeded();
                if (scheduler != null)
                {
                    scheduler.NotifyMeshReady(chunk);
                }

                return 1;
            }

            return 0;
        }

        /// <summary>True when the chunk currently has an active view.</summary>
        public bool TryGetView(ChunkCoord chunk, out ChunkView view)
        {
            return views.TryGetValue(chunk, out view);
        }

        private void ApplyStoredDelta(ChunkCoord chunk)
        {
            ChunkDelta stored = LoadStoredDelta(chunk);
            ChunkDelta live = null;
            bool hasLive = AppliedEdits != null
                && AppliedEdits.TryGetAccumulatedDelta(chunk, out live)
                && live != null
                && live.Edits.Count > 0;

            if (stored != null && stored.Edits.Count > 0)
            {
                deltasLoaded++;
                deltaEditsApplied += ApplyDelta(chunk, stored);
                if (AppliedEdits != null)
                {
                    // Merge (never overwrite) so a later flush writes the
                    // persisted cells plus this session's cells.
                    AppliedEdits.TrackLoadedDelta(chunk, stored);
                }
            }

            if (hasLive)
            {
                // In-session edits are newer than anything on disk and win.
                liveDeltasReapplied++;
                liveEditsReapplied += ApplyDelta(chunk, live);
            }
        }

        private ChunkDelta LoadStoredDelta(ChunkCoord chunk)
        {
            IWorldStore activeStore = Store;
            if (activeStore == null)
            {
                return null;
            }

            try
            {
                return activeStore.LoadAsync(chunk, System.Threading.CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[ChunkViewManager] delta load failed for chunk (" + chunk.X + ", "
                        + chunk.Y + ", " + chunk.Z + "): " + exception.Message);
                return null;
            }
        }

        private int ApplyDelta(ChunkCoord chunk, ChunkDelta delta)
        {
            int applied = 0;
            foreach (KeyValuePair<Int3, BlockId> edit in delta.Edits)
            {
                Int3 cell = ChunkMath.ToWorld(chunk, edit.Key);
                if (world.Apply(new EditCommand(cell, world.Get(cell), edit.Value, 0L)) == EditResult.Applied)
                {
                    applied++;
                }
            }

            return applied;
        }

        private void EnsureInitialized()
        {
            if (!initialized)
            {
                throw new InvalidOperationException("ChunkViewManager.Initialize must run before streaming callbacks.");
            }
        }

        private bool IsUploadBudgetExhausted()
        {
            int frame = Time.frameCount;
            if (frame != lastUploadFrame)
            {
                lastUploadFrame = frame;
                uploadsThisFrame = 0;
            }

            return uploadsThisFrame >= maxMeshUploadsPerFrame;
        }

        private static Vector3 ConvertedChunkOrigin(ChunkCoord chunk)
        {
            Vec3 origin = UnityConvert.ToUnity(new Vec3(
                chunk.X * ChunkMath.ChunkSize,
                chunk.Y * ChunkMath.ChunkSize,
                chunk.Z * ChunkMath.ChunkSize));
            return new Vector3((float)origin.X, (float)origin.Y, (float)origin.Z);
        }

        private void HandleChunkChanged(ChunkCoord changed)
        {
            int count = FillRemeshNeighbourhood(changed, neighbourhood);
            for (int i = 0; i < count; i++)
            {
                ChunkCoord chunk = neighbourhood[i];
                if (chunks.ContainsKey(chunk) && dirtySet.Add(chunk))
                {
                    dirtyQueue.Enqueue(chunk);
                }
            }
        }

        private void CompactWorldIfNeeded()
        {
            if (worldChunks <= (2 * chunks.Count) + 64)
            {
                return;
            }

            world.ChunkChanged -= HandleChunkChanged;
            var rebuilt = new World();
            foreach (Chunk chunk in chunks.Values)
            {
                rebuilt.LoadChunk(chunk);
            }

            world = rebuilt;
            world.ChunkChanged += HandleChunkChanged;
            worldChunks = chunks.Count;
            worldCompactions++;
        }

        private void OnDestroy()
        {
            DisposeState();
        }

        private void DisposeState()
        {
            if (world != null)
            {
                world.ChunkChanged -= HandleChunkChanged;
            }

            if (pool != null)
            {
                pool.Dispose();
                pool = null;
            }

            chunks.Clear();
            views.Clear();
            deferredLoads.Clear();
            deferredSet.Clear();
            dirtySet.Clear();
            dirtyQueue.Clear();
            scheduler = null;
            world = null;
            bufferPool = null;
            mesher = null;
            generator = null;
            initialized = false;
            uploadsThisFrame = 0;
            generatedChunks = 0;
            uploadedChunks = 0;
            unloadedChunks = 0;
            remeshedChunks = 0;
            worldChunks = 0;
            worldCompactions = 0;
            deltasLoaded = 0;
            deltaEditsApplied = 0;
            liveDeltasReapplied = 0;
            liveEditsReapplied = 0;
            lastGenerateFrame = int.MinValue;
            lastUploadFrame = int.MinValue;
        }
    }
}
