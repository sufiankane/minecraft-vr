using System;
using Cubeglass.CoreMath;
using Cubeglass.Streaming;
using UnityEngine;

namespace Cubeglass.Unity.Rendering
{
    /// <summary>
    /// Drives chunk streaming from the player transform once per Unity frame
    /// (S7 Task 2): samples the transform, runs the
    /// <see cref="ChunkStreamingScheduler"/> against the wired
    /// <see cref="ChunkViewManager"/>, and exposes the counters the debug
    /// overlay and the tests read.
    /// </summary>
    /// <remarks>
    /// The inspector fields mirror <see cref="StreamingConfig"/>; <see cref="Awake"/>
    /// builds the scheduler and the manager once, and <see cref="Update"/>
    /// forwards one tick when <see cref="AutoUpdate"/> is on. PlayMode tests
    /// switch <see cref="AutoUpdate"/> off and call <see cref="Tick"/> directly.
    /// The runtime GameObject must sit at the world origin: chunk views are
    /// parented to the manager under it and placed at converted chunk origins
    /// (see <see cref="ChunkViewManager"/>). The sampled transform lives in
    /// Unity space, so <see cref="Tick"/> converts its position exactly once
    /// back to the internal frame (ADR-0004/ADR-0011, R52) before the
    /// scheduler sees it, and drains the view manager's dirty remeshes so an
    /// edit and the streaming that follows it share the same upload budget.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class StreamingRuntime : MonoBehaviour
    {
        [SerializeField] private Transform player;
        [SerializeField] private int viewDistanceChunks = 8;
        [SerializeField] private int unloadHysteresis = 2;
        [SerializeField] private int maxLoadsPerFrame = 4;
        [SerializeField] private int maxUnloadsPerFrame = 4;
        [SerializeField] private int maxMeshUploadsPerFrame = 4;
        [SerializeField] private float verticalRadiusChunks = 2f;
        [SerializeField] private int maxViews = 2048;
        [SerializeField] private long seed = 1L;
        [SerializeField] private bool autoUpdate = true;
        [SerializeField] private Material viewMaterial;

        private StreamingConfig config;
        private ChunkStreamingScheduler scheduler;
        private ChunkViewManager views;

        /// <summary>When true (default) <see cref="Update"/> ticks automatically.</summary>
        public bool AutoUpdate
        {
            get { return autoUpdate; }
            set { autoUpdate = value; }
        }

        /// <summary>The transform sampled each tick; defaults to this transform.</summary>
        public Transform PlayerTransform
        {
            get { return player; }
            set { player = value; }
        }

        /// <summary>The active streaming config.</summary>
        public StreamingConfig Config
        {
            get { return config; }
        }

        /// <summary>The scheduler fed every tick.</summary>
        public ChunkStreamingScheduler Scheduler
        {
            get { return scheduler; }
        }

        /// <summary>The chunk view manager receiving the scheduler actions.</summary>
        public ChunkViewManager Views
        {
            get { return views; }
        }

        /// <summary>Resident chunks known to the scheduler.</summary>
        public int LoadedChunks
        {
            get { return scheduler != null ? scheduler.LoadedCount : 0; }
        }

        /// <summary>Chunks in the current desired set.</summary>
        public int DesiredChunks
        {
            get { return scheduler != null ? scheduler.DesiredCount : 0; }
        }

        /// <summary>Resident chunks with a mesh waiting to upload.</summary>
        public int PendingMeshCount
        {
            get { return scheduler != null ? scheduler.PendingMeshCount : 0; }
        }

        /// <summary>Loads waiting for their one-per-frame generation slot.</summary>
        public int DeferredLoads
        {
            get { return views != null ? views.DeferredLoads : 0; }
        }

        /// <summary>Active chunk views.</summary>
        public int ActiveViews
        {
            get { return views != null ? views.ActiveViews : 0; }
        }

        /// <summary>Pooled chunk views waiting for reuse.</summary>
        public int PooledViews
        {
            get { return views != null ? views.PooledViews : 0; }
        }

        /// <summary>Mesh data built but not yet released; zero at rest.</summary>
        public long OutstandingMeshData
        {
            get { return views != null ? views.OutstandingMeshData : 0; }
        }

        private void Awake()
        {
            EnsureInitialized();
        }

        /// <summary>
        /// Replaces the inspector configuration (tests and scripted scenes).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="streamingConfig"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="poolCapacity"/> is not positive.</exception>
        public void Configure(StreamingConfig streamingConfig, long worldSeed, int poolCapacity, Transform playerTransform)
        {
            if (streamingConfig == null)
            {
                throw new ArgumentNullException(nameof(streamingConfig));
            }

            if (poolCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(poolCapacity), poolCapacity, "Pool capacity must be positive.");
            }

            config = streamingConfig;
            seed = worldSeed;
            maxViews = poolCapacity;
            player = playerTransform != null ? playerTransform : transform;
            Build();
        }

        /// <summary>
        /// Samples the player position and advances streaming by one frame.
        /// </summary>
        public void Tick()
        {
            EnsureInitialized();
            views.ProcessDeferredLoads();
            Transform target = player != null ? player : transform;
            Vector3 position = target.position;
            Vec3 internalPosition = UnityConvert.ToUnity(new Vec3(position.x, position.y, position.z));
            views.ProcessDirtyRemeshes();
            scheduler.Update(views, internalPosition);
        }

        private void Update()
        {
            if (autoUpdate)
            {
                Tick();
            }
        }

        private void EnsureInitialized()
        {
            if (scheduler != null)
            {
                return;
            }

            config = new StreamingConfig
            {
                ViewDistanceChunks = viewDistanceChunks,
                UnloadHysteresis = unloadHysteresis,
                MaxLoadsPerFrame = maxLoadsPerFrame,
                MaxUnloadsPerFrame = maxUnloadsPerFrame,
                MaxMeshUploadsPerFrame = maxMeshUploadsPerFrame,
                VerticalRadiusChunks = verticalRadiusChunks,
            };

            player = player != null ? player : transform;
            Build();
        }

        private void Build()
        {
            scheduler = new ChunkStreamingScheduler(config, seed);
            views = GetComponent<ChunkViewManager>();
            if (views == null)
            {
                views = gameObject.AddComponent<ChunkViewManager>();
            }

            views.ViewMaterial = viewMaterial;
            views.Initialize(config, seed, maxViews, scheduler);
        }
    }
}
