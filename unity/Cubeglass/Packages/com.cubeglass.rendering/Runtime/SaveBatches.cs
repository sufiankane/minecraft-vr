using System;
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
    /// <see cref="TimedOutFlushes"/> increments and a warning is logged.
    /// </para>
    /// <para>
    /// <b>Memory.</b> A chunk's edit map is retained after a flush so the next
    /// flush can write the complete delta; the retained state is one
    /// <see cref="ChunkDelta"/>-sized dictionary per edited chunk, which is the
    /// same order as the world edits themselves.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class SaveBatches : MonoBehaviour
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

        private readonly Dictionary<ChunkCoord, PendingChunk> pending =
            new Dictionary<ChunkCoord, PendingChunk>();

        private readonly List<ChunkCoord> readyChunks = new List<ChunkCoord>();

        private FileWorldStore store;
        private Func<double> clock;

        /// <summary>The store every batch is written to; null disables saving.</summary>
        public FileWorldStore Store
        {
            get { return store; }
            set { store = value; }
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
            get { return clock != null ? clock : new Func<double>(DefaultClock); }
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

            PendingChunk state;
            if (!pending.TryGetValue(coord, out state))
            {
                state = new PendingChunk();
                pending.Add(coord, state);
            }

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
        /// Flushes every dirty chunk and synchronously waits up to
        /// <see cref="FlushWaitMilliseconds"/> for the writer queue to drain.
        /// This is the path <see cref="OnApplicationQuit"/> and
        /// <see cref="OnApplicationPause"/> use. Returns the number of chunks
        /// queued.
        /// </summary>
        public int Flush()
        {
            FlushCalls++;
            int flushed = FlushDirtyChunks();
            if (store != null
                && !store.WaitForPendingWrites(TimeSpan.FromMilliseconds(FlushWaitMilliseconds)))
            {
                TimedOutFlushes++;
                Debug.LogWarning(
                    "[SaveBatches] quit flush timed out after " + FlushWaitMilliseconds
                        + " ms; " + store.QueuedWrites + " write(s) may be lost.");
            }

            return flushed;
        }

        /// <summary>
        /// Flushes every dirty chunk and completes when the writer queue has
        /// drained; used by tests and by any caller that can await.
        /// </summary>
        public Task FlushAsync()
        {
            FlushCalls++;
            FlushDirtyChunks();
            return store != null ? store.FlushAsync() : Task.CompletedTask;
        }

        /// <summary>
        /// Flushes chunks whose oldest unsaved edit is older than
        /// <see cref="FlushIntervalSeconds"/>. Called every frame from
        /// <c>Update</c>; safe to call directly in tests.
        /// </summary>
        public void Tick()
        {
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
