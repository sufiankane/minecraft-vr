using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cubeglass.Voxel;
using UnityEngine;

namespace Cubeglass.Unity.Rendering
{
    /// <summary>
    /// Collects applied edits per chunk and flushes complete
    /// <see cref="ChunkDelta"/>s to a <see cref="FileWorldStore"/> in batches
    /// (S7 Task 4b).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Batching.</b> <see cref="GameplayBridge"/> forwards every applied edit
    /// to <see cref="TrackEdit"/>; a chunk is queued for saving once it has
    /// accumulated <see cref="EditsPerFlush"/> (default 32) more edits since its
    /// last flush, or once its oldest unsaved edit is
    /// <see cref="FlushIntervalSeconds"/> (default 2 s) old (checked by
    /// <see cref="Tick"/>, driven from <c>Update</c>). Every flush writes the
    /// chunk's <em>complete</em> edit map relative to its generated baseline,
    /// never a delta-of-deltas, so an overwritten file is always a full
    /// replacement and boot load only ever needs one file per chunk.
    /// </para>
    /// <para>
    /// <b>Quit / pause.</b> <see cref="OnApplicationQuit"/> and
    /// <see cref="OnApplicationPause"/> (pausing) call <see cref="Flush"/>, the
    /// synchronous best-effort path: it queues every dirty chunk and then waits
    /// up to <see cref="FlushWaitMilliseconds"/> (default 2000 ms, documented
    /// and bounded because the process may not survive longer) for the writer
    /// queue to drain. On timeout the remaining writes are abandoned,
    /// <see cref="TimedOutFlushes"/> increments and a warning is logged. When
    /// the drain finishes but writes failed, <see cref="FailedFlushes"/>
    /// increments and the failure is logged as an error — a failed write is
    /// never reported as a clean flush (the store's
    /// <see cref="FileWorldStore.FlushAsync"/> returns a
    /// <see cref="FlushResult"/> carrying the failure count).
    /// </para>
    /// <para>
    /// <b>Failed writes (TD-021).</b> The store retries a write with a bounded
    /// backoff first; if it still fails it raises
    /// <see cref="FileWorldStore.WriteFailed"/>. This component re-dirties the
    /// chunk on its next <see cref="Tick"/>/<see cref="Flush"/> (the complete
    /// edit map is retained, so the retry rewrites the same payload) and counts
    /// it in <see cref="ReDirtiedChunks"/>. A quit flush therefore gives a
    /// failed batch one immediate retry instead of silently dropping the edits.
    /// </para>
    /// <para>
    /// <b>Memory.</b> A chunk's edit map is retained after a flush so the next
    /// flush can write the complete delta; the retained state is one
    /// <see cref="ChunkDelta"/>-sized dictionary per edited chunk, which is the
    /// same order as the world edits themselves.
    /// </para>
    /// <para>
    /// <b>Loaded deltas.</b> As an <see cref="IAppliedEditCache"/>, the
    /// component also seeds that map with deltas a manager loaded from disk or
    /// re-applies after an unload/reload (see
    /// <see cref="TrackLoadedDelta"/> and
    /// <see cref="TryGetAccumulatedDelta"/>), so a later flush merges the
    /// session's edits with the persisted ones instead of replacing them, and
    /// in-session regeneration keeps edits that have not been flushed yet.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class SaveBatches : MonoBehaviour, IAppliedEditCache
    {
        /// <summary>Default edits accumulated per chunk before a flush.</summary>
        public const int DefaultEditsPerFlush = 32;

        /// <summary>Default age of the oldest unsaved edit before a time flush, in seconds.</summary>
        public const float DefaultFlushIntervalSeconds = 2f;

        /// <summary>Default bounded wait for the writer queue on the quit path, in milliseconds.</summary>
        public const int DefaultFlushWaitMilliseconds = 2000;

        [SerializeField] private int editsPerFlush = DefaultEditsPerFlush;
        [SerializeField] private float flushIntervalSeconds = DefaultFlushIntervalSeconds;
        [SerializeField] private int flushWaitMilliseconds = DefaultFlushWaitMilliseconds;

        private static readonly Func<double> DefaultClockDelegate = DefaultClock;

        private readonly Dictionary<ChunkCoord, PendingChunk> pending =
            new Dictionary<ChunkCoord, PendingChunk>();

        private readonly List<ChunkCoord> readyChunks = new List<ChunkCoord>();

        private readonly ConcurrentQueue<ChunkCoord> failedWrites = new ConcurrentQueue<ChunkCoord>();

        private FileWorldStore store;
        private Func<double> clock;

        /// <summary>The store every batch is written to; null disables saving.</summary>
        public FileWorldStore Store
        {
            get { return store; }
            set
            {
                if (store != null)
                {
                    store.WriteFailed -= OnStoreWriteFailed;
                }

                store = value;
                if (store != null)
                {
                    store.WriteFailed += OnStoreWriteFailed;
                }
            }
        }

        /// <summary>Edits accumulated per chunk before a count flush; defaults to 32.</summary>
        public int EditsPerFlush
        {
            get { return editsPerFlush; }
            set { editsPerFlush = value > 0 ? value : DefaultEditsPerFlush; }
        }

        /// <summary>Seconds an edit may stay unsaved before a time flush; defaults to 2.</summary>
        public float FlushIntervalSeconds
        {
            get { return flushIntervalSeconds; }
            set { flushIntervalSeconds = value >= 0f ? value : DefaultFlushIntervalSeconds; }
        }

        /// <summary>Bounded wait for the writer queue on <see cref="Flush"/>, in milliseconds.</summary>
        public int FlushWaitMilliseconds
        {
            get { return flushWaitMilliseconds; }
            set { flushWaitMilliseconds = value >= 0 ? value : DefaultFlushWaitMilliseconds; }
        }

        /// <summary>
        /// Monotonic clock used for the time flush, in seconds. Defaults to
        /// <see cref="Time.unscaledTimeAsDouble"/>; tests replace it to pin the
        /// interval deterministically.
        /// </summary>
        public Func<double> Clock
        {
            get { return clock != null ? clock : DefaultClockDelegate; }
            set { clock = value; }
        }

        /// <summary>Chunks with at least one tracked edit (saved or pending).</summary>
        public int PendingChunks
        {
            get { return pending.Count; }
        }

        /// <summary>Edits accepted by <see cref="TrackEdit"/> since scene start.</summary>
        public long TrackedEdits { get; private set; }

        /// <summary>Chunk flushes handed to the store since scene start.</summary>
        public long FlushesQueued { get; private set; }

        /// <summary>Edits included in a flush since scene start.</summary>
        public long FlushedEdits { get; private set; }

        /// <summary>Times a bounded <see cref="Flush"/> wait expired with writes still queued.</summary>
        public long TimedOutFlushes { get; private set; }

        /// <summary>Times a completed drain reported at least one failed write.</summary>
        public long FailedFlushes { get; private set; }

        /// <summary>
        /// Chunks re-dirtied after the store gave up on a write (TD-021): the
        /// chunk is queued for the next <see cref="Tick"/>/<see cref="Flush"/>
        /// instead of being silently treated as saved.
        /// </summary>
        public long ReDirtiedChunks { get; private set; }

        /// <summary>Calls to <see cref="Flush"/> or <see cref="FlushAsync"/> since scene start.</summary>
        public long FlushCalls { get; private set; }

        /// <summary>
        /// Records one applied edit (world cell plus its new block) and flushes
        /// the owning chunk when it crosses <see cref="EditsPerFlush"/>.
        /// </summary>
        public void TrackEdit(Int3 cell, BlockId block)
        {
            ChunkCoord coord = ChunkMath.ToChunk(cell);
            Int3 local = ChunkMath.ToLocal(cell);
            PendingChunk state = GetOrCreate(coord);

            BlockId existing;
            if (state.Edits.TryGetValue(local, out existing) && existing == block)
            {
                return;
            }

            state.Edits[local] = block;
            state.EditsSinceFlush++;
            TrackedEdits++;
            if (!state.Dirty)
            {
                state.Dirty = true;
                state.DirtySince = Clock();
            }

            if (state.EditsSinceFlush >= EditsPerFlush)
            {
                FlushChunk(coord, state);
            }
        }

        /// <summary>
        /// Seeds the accumulated map for <paramref name="coord"/> with the cells
        /// of a delta loaded from the store, without counting them as unsaved
        /// edits (they are already on disk) and without overwriting cells this
        /// session has edited (those are newer). This is what lets a later flush
        /// write the session's cells plus the persisted ones instead of
        /// replacing the file with only the current session.
        /// </summary>
        public void TrackLoadedDelta(ChunkCoord coord, ChunkDelta delta)
        {
            if (delta == null || delta.Edits.Count == 0)
            {
                return;
            }

            PendingChunk state = GetOrCreate(coord);
            foreach (KeyValuePair<Int3, BlockId> edit in delta.Edits)
            {
                if (!state.Edits.ContainsKey(edit.Key))
                {
                    state.Edits.Add(edit.Key, edit.Value);
                }
            }
        }

        /// <summary>
        /// Returns the chunk's complete in-memory edit map, including loaded
        /// cells and edits not yet flushed. A manager re-applies it after
        /// regenerating a chunk so an unload/reload cannot revert edits whose
        /// file has not been written yet.
        /// </summary>
        public bool TryGetAccumulatedDelta(ChunkCoord coord, out ChunkDelta delta)
        {
            PendingChunk state;
            if (pending.TryGetValue(coord, out state) && state.Edits.Count > 0)
            {
                delta = new ChunkDelta(coord, state.Edits);
                return true;
            }

            delta = null;
            return false;
        }

        /// <summary>
        /// Flushes every dirty chunk and synchronously waits up to
        /// <see cref="FlushWaitMilliseconds"/> for the writer queue to drain.
        /// This is the path <see cref="OnApplicationQuit"/> and
        /// <see cref="OnApplicationPause"/> use. Returns the number of chunks
        /// queued. A completed drain with failed writes is logged loudly as an
        /// error and counted in <see cref="FailedFlushes"/>.
        /// </summary>
        public int Flush()
        {
            FlushCalls++;
            DrainFailedWrites();
            int flushed = FlushDirtyChunks();
            if (store == null)
            {
                return flushed;
            }

            FlushResult result = store.WaitForPendingWrites(TimeSpan.FromMilliseconds(FlushWaitMilliseconds));
            if (!result.Completed)
            {
                TimedOutFlushes++;
                Debug.LogWarning(
                    "[SaveBatches] quit flush timed out after " + FlushWaitMilliseconds
                        + " ms; " + store.QueuedWrites + " write(s) may be lost.");
            }
            else if (result.FailedWrites > 0)
            {
                FailedFlushes++;
                Debug.LogError(
                    "[SaveBatches] " + result.FailedWrites
                        + " write(s) failed during the quit flush; those edits were not persisted.");
            }

            return flushed;
        }

        /// <summary>
        /// Flushes every dirty chunk and completes with the store's
        /// <see cref="FlushResult"/> when the writer queue has drained; used by
        /// tests and by any caller that can await. The result's
        /// <see cref="FlushResult.FailedWrites"/> surfaces write failures
        /// instead of reporting a clean flush.
        /// </summary>
        public Task<FlushResult> FlushAsync()
        {
            FlushCalls++;
            DrainFailedWrites();
            FlushDirtyChunks();
            return store != null
                ? store.FlushAsync()
                : Task.FromResult(new FlushResult(true, 0));
        }

        /// <summary>
        /// Flushes chunks whose oldest unsaved edit is older than
        /// <see cref="FlushIntervalSeconds"/>. Called every frame from
        /// <c>Update</c>; safe to call directly in tests.
        /// </summary>
        public void Tick()
        {
            DrainFailedWrites();
            if (pending.Count == 0)
            {
                return;
            }

            double now = Clock();
            readyChunks.Clear();
            foreach (KeyValuePair<ChunkCoord, PendingChunk> entry in pending)
            {
                if (entry.Value.Dirty && (now - entry.Value.DirtySince) >= FlushIntervalSeconds)
                {
                    readyChunks.Add(entry.Key);
                }
            }

            for (int i = 0; i < readyChunks.Count; i++)
            {
                PendingChunk state;
                if (pending.TryGetValue(readyChunks[i], out state))
                {
                    FlushChunk(readyChunks[i], state);
                }
            }
        }

        private void Update()
        {
            Tick();
        }

        private void OnApplicationQuit()
        {
            Flush();
        }

        private void OnApplicationPause(bool pause)
        {
            if (pause)
            {
                Flush();
            }
        }

        private int FlushDirtyChunks()
        {
            int flushed = 0;
            foreach (KeyValuePair<ChunkCoord, PendingChunk> entry in pending)
            {
                if (entry.Value.Dirty)
                {
                    FlushChunk(entry.Key, entry.Value);
                    flushed++;
                }
            }

            return flushed;
        }

        private void FlushChunk(ChunkCoord coord, PendingChunk state)
        {
            if (store == null)
            {
                return;
            }

            try
            {
                var delta = new ChunkDelta(coord, state.Edits);
                store.SaveAsync(coord, delta, CancellationToken.None);
                FlushesQueued++;
                FlushedEdits += state.EditsSinceFlush;
                state.EditsSinceFlush = 0;
                state.Dirty = false;
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[SaveBatches] could not queue the delta for chunk (" + coord.X + ", "
                        + coord.Y + ", " + coord.Z + "): " + exception.Message);
            }
        }

        private PendingChunk GetOrCreate(ChunkCoord coord)
        {
            PendingChunk state;
            if (!pending.TryGetValue(coord, out state))
            {
                state = new PendingChunk();
                pending.Add(coord, state);
            }

            return state;
        }

        /// <summary>
        /// Store callback (pump thread): records the chunk whose write failed
        /// after its bounded retries so the main thread can re-dirty it.
        /// </summary>
        private void OnStoreWriteFailed(ChunkCoord coord)
        {
            failedWrites.Enqueue(coord);
        }

        /// <summary>
        /// Re-dirties every chunk the store reported failed (TD-021), so the
        /// next <see cref="Tick"/>/<see cref="Flush"/> re-queues it instead of
        /// silently treating a failed write as saved. Runs on the main thread;
        /// the complete edit map is retained, so the retry rewrites the same
        /// payload.
        /// </summary>
        private void DrainFailedWrites()
        {
            while (failedWrites.TryDequeue(out ChunkCoord coord))
            {
                PendingChunk state = GetOrCreate(coord);
                state.Dirty = true;
                state.DirtySince = Clock();
                ReDirtiedChunks++;
            }
        }

        private static double DefaultClock()
        {
            return Time.unscaledTimeAsDouble;
        }

        private sealed class PendingChunk
        {
            public readonly Dictionary<Int3, BlockId> Edits = new Dictionary<Int3, BlockId>();

            public int EditsSinceFlush;

            public bool Dirty;

            public double DirtySince;
        }
    }
}
