using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cubeglass.Voxel;
using UnityEngine;

namespace Cubeglass.Unity.Rendering
{
    /// <summary>
    /// The Unity-side <see cref="IWorldStore"/> (S7 Task 4b): one version-1
    /// <see cref="ChunkDeltaCodec"/> file per edited chunk, written through a
    /// background queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Layout.</b> Deltas live at
    /// <c>&lt;root&gt;/&lt;world&gt;/&lt;x&gt;_&lt;y&gt;_&lt;z&gt;.cgdl</c>, with
    /// <c>&lt;root&gt;</c> defaulting to
    /// <c>Application.persistentDataPath/Cubeglass/saves</c>; a test or tool
    /// may pass an explicit root. The world name is validated so it can never
    /// escape the root through separators or <c>..</c>.
    /// </para>
    /// <para>
    /// <b>Writes never block the caller.</b> <see cref="SaveAsync"/> serialises
    /// the delta synchronously (cheap: one RLE pass over 4096 cells) and then
    /// enqueues the bytes. A single background pump writes one payload at a
    /// time: it writes a per-chunk temp file and then
    /// <see cref="File.Replace(string, string, string)"/>s or moves it over the
    /// destination, so a reader always sees either the old or the new complete
    /// file, never a partial one. The pump is one queue, so a repeated save of
    /// the same chunk can never interleave with itself (last write wins), and a
    /// save enqueued while an older payload of the same chunk is still queued
    /// replaces it instead of duplicating the IO.
    /// </para>
    /// <para>
    /// <b>Reads.</b> <see cref="LoadAsync"/> is synchronous small-file IO on
    /// the calling thread and never throws into gameplay: a missing file
    /// returns null silently, and a malformed payload (anything the codec
    /// rejects) or an IO error logs a warning, increments a counter and
    /// returns null, so the caller falls back to the generated baseline.
    /// </para>
    /// <para>
    /// <b>Lifecycle.</b> <see cref="FlushAsync"/> completes when every write
    /// enqueued up to the call has finished (failed writes count as finished);
    /// <see cref="WaitForPendingWrites"/> is the bounded synchronous form used
    /// by the quit path. <see cref="Dispose"/> stops the pump after draining
    /// what is already queued; a store must not be used after disposal.
    /// </para>
    /// </remarks>
    public sealed class FileWorldStore : IWorldStore, IDisposable
    {
        /// <summary>Directory under the root that holds all worlds.</summary>
        public const string SavesFolderName = "saves";

        /// <summary>Directory under <see cref="Application.persistentDataPath"/> for all saves.</summary>
        public const string RootFolderName = "Cubeglass";

        /// <summary>File extension of one encoded <see cref="ChunkDelta"/>.</summary>
        public const string DeltaExtension = ".cgdl";

        private readonly object gate = new object();
        private readonly Dictionary<ChunkCoord, PendingWrite> pending = new Dictionary<ChunkCoord, PendingWrite>();
        private readonly Queue<ChunkCoord> queue = new Queue<ChunkCoord>();
        private readonly List<DrainWaiter> drainWaiters = new List<DrainWaiter>();
        private readonly SemaphoreSlim signal = new SemaphoreSlim(0);
        private readonly Task pump;

        private long enqueued;
        private long completed;
        private long successfulWrites;
        private long failedWrites;
        private long rejectedLoads;
        private long failedLoads;
        private int tempCounter;
        private bool disposed;

        /// <summary>Creates a store for <paramref name="worldName"/> under the default root.</summary>
        public FileWorldStore(string worldName)
            : this(worldName, DefaultRootDirectory)
        {
        }

        /// <summary>
        /// Creates a store for <paramref name="worldName"/> under
        /// <paramref name="rootDirectory"/>, creating the world directory.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="rootDirectory"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="worldName"/> is empty or names a path.</exception>
        public FileWorldStore(string worldName, string rootDirectory)
        {
            if (rootDirectory == null)
            {
                throw new ArgumentNullException(nameof(rootDirectory));
            }

            WorldName = ValidateWorldName(worldName);
            WorldDirectory = Path.Combine(rootDirectory, WorldName);
            Directory.CreateDirectory(WorldDirectory);
            pump = Task.Run(new Func<Task>(PumpAsync));
        }

        /// <summary>The root every world directory is created under.</summary>
        public static string DefaultRootDirectory
        {
            get
            {
                return Path.Combine(Application.persistentDataPath, RootFolderName, SavesFolderName);
            }
        }

        /// <summary>The validated world name this store reads and writes.</summary>
        public string WorldName { get; }

        /// <summary>The directory holding this world's <c>.cgdl</c> files.</summary>
        public string WorldDirectory { get; }

        /// <summary>Writes that completed and replaced (or created) a delta file.</summary>
        public long SuccessfulWrites
        {
            get { return Interlocked.Read(ref successfulWrites); }
        }

        /// <summary>Writes that failed; the previous file (if any) is left untouched.</summary>
        public long FailedWrites
        {
            get { return Interlocked.Read(ref failedWrites); }
        }

        /// <summary>Loads rejected because the payload failed <see cref="ChunkDeltaCodec.TryDeserialize"/>.</summary>
        public long RejectedLoads
        {
            get { return Interlocked.Read(ref rejectedLoads); }
        }

        /// <summary>Loads that failed on IO (the file exists but could not be read).</summary>
        public long FailedLoads
        {
            get { return Interlocked.Read(ref failedLoads); }
        }

        /// <summary>Payloads queued but not yet written; a coalesced save counts once.</summary>
        public int QueuedWrites
        {
            get
            {
                lock (gate)
                {
                    return queue.Count;
                }
            }
        }

        /// <summary>
        /// Test/diagnostic hook: wall time the pump sleeps before each write.
        /// Default <see cref="TimeSpan.Zero"/>; set it to make the
        /// asynchronous write window observable.
        /// </summary>
        public TimeSpan WriteDelay { get; set; }

        /// <summary>The path of the delta file for <paramref name="coord"/>.</summary>
        public string ChunkPath(ChunkCoord coord)
        {
            return Path.Combine(
                WorldDirectory,
                coord.X.ToString(CultureInfo.InvariantCulture) + "_"
                    + coord.Y.ToString(CultureInfo.InvariantCulture) + "_"
                    + coord.Z.ToString(CultureInfo.InvariantCulture) + DeltaExtension);
        }

        /// <summary>Whether a delta file exists for <paramref name="coord"/>.</summary>
        public bool HasSavedChunk(ChunkCoord coord)
        {
            return File.Exists(ChunkPath(coord));
        }

        /// <summary>Number of <c>.cgdl</c> files currently present in the world directory.</summary>
        public int CountSavedChunks()
        {
            return Directory.Exists(WorldDirectory)
                ? Directory.GetFiles(WorldDirectory, "*" + DeltaExtension).Length
                : 0;
        }

        /// <summary>
        /// Serialises <paramref name="delta"/> and queues the write. Returns
        /// as soon as the bytes are queued; the pump performs the IO.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="delta"/> is null.</exception>
        /// <exception cref="ArgumentException">The delta carries the reserved no-edit block id.</exception>
        /// <exception cref="ObjectDisposedException">The store was disposed.</exception>
        public ValueTask SaveAsync(ChunkCoord c, ChunkDelta delta, CancellationToken ct)
        {
            if (delta is null)
            {
                throw new ArgumentNullException(nameof(delta));
            }

            ct.ThrowIfCancellationRequested();
            byte[] bytes = ChunkDeltaCodec.Serialize(delta);
            Enqueue(c, bytes);
            return default;
        }

        /// <summary>
        /// Reads and decodes the delta of <paramref name="c"/>, or null when
        /// no file exists. A corrupt payload (codec rejection) or an IO error
        /// logs a warning, bumps a counter and returns null; this method never
        /// throws for file content problems.
        /// </summary>
        public ValueTask<ChunkDelta> LoadAsync(ChunkCoord c, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string path = ChunkPath(c);
            byte[] bytes;
            try
            {
                if (!File.Exists(path))
                {
                    return new ValueTask<ChunkDelta>((ChunkDelta)null);
                }

                bytes = File.ReadAllBytes(path);
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref failedLoads);
                Debug.LogWarning(
                    "[FileWorldStore] could not read '" + path + "': " + exception.Message
                        + "; the chunk will be generated from its seed.");
                return new ValueTask<ChunkDelta>((ChunkDelta)null);
            }

            ChunkDelta decoded;
            if (!ChunkDeltaCodec.TryDeserialize(bytes, out decoded) || decoded == null)
            {
                Interlocked.Increment(ref rejectedLoads);
                Debug.LogWarning(
                    "[FileWorldStore] rejected corrupt delta '" + path + "'; "
                        + "the chunk will be generated from its seed.");
                return new ValueTask<ChunkDelta>((ChunkDelta)null);
            }

            // The payload carries no coordinate (ADR-0006): re-key the decoded
            // edits to the chunk that was asked for, which is the store's key.
            return new ValueTask<ChunkDelta>(new ChunkDelta(c, decoded.Edits));
        }

        /// <summary>
        /// Completes when every write enqueued up to this call has finished
        /// (successfully or not). Safe to await from any thread.
        /// </summary>
        public Task FlushAsync()
        {
            lock (gate)
            {
                if (completed >= enqueued)
                {
                    return Task.CompletedTask;
                }

                var waiter = new DrainWaiter(enqueued);
                drainWaiters.Add(waiter);
                return waiter.Task;
            }
        }

        /// <summary>
        /// Synchronously waits up to <paramref name="timeout"/> for
        /// <see cref="FlushAsync"/> to complete. Returns false on timeout; the
        /// quit path uses this bounded wait because the process may not
        /// survive a longer one.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative.</exception>
        public bool WaitForPendingWrites(TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be non-negative.");
            }

            Task drain = FlushAsync();
            return drain.IsCompleted || drain.Wait(timeout);
        }

        /// <summary>
        /// Stops the pump after it has drained the writes already queued.
        /// Best-effort: a pump still blocked on IO is abandoned.
        /// </summary>
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
            }

            signal.Release();
            try
            {
                pump.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // The pump catches its own IO failures; a fault here is a bug
                // best surfaced by the failed-write counters, not by Dispose.
            }
        }

        private void Enqueue(ChunkCoord coord, byte[] bytes)
        {
            lock (gate)
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(nameof(FileWorldStore));
                }

                long version = ++enqueued;
                PendingWrite existing;
                if (pending.TryGetValue(coord, out existing))
                {
                    // Last write wins while the older payload is still queued.
                    existing.Bytes = bytes;
                    existing.Version = version;
                }
                else
                {
                    pending.Add(coord, new PendingWrite(bytes, version));
                    queue.Enqueue(coord);
                }
            }

            signal.Release();
        }

        private async Task PumpAsync()
        {
            while (true)
            {
                await signal.WaitAsync().ConfigureAwait(false);
                try
                {
                    WriteQueued();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[FileWorldStore] writer pump error: " + exception.Message);
                }

                bool stop;
                lock (gate)
                {
                    stop = disposed && queue.Count == 0;
                }

                if (stop)
                {
                    return;
                }
            }
        }

        private void WriteQueued()
        {
            while (true)
            {
                ChunkCoord coord;
                PendingWrite write;
                lock (gate)
                {
                    if (queue.Count == 0)
                    {
                        return;
                    }

                    coord = queue.Dequeue();
                    write = pending[coord];
                    pending.Remove(coord);
                }

                WriteOne(coord, write);
            }
        }

        private void WriteOne(ChunkCoord coord, PendingWrite write)
        {
            string path = ChunkPath(coord);
            string temp = path + "." + Interlocked.Increment(ref tempCounter).ToString(CultureInfo.InvariantCulture) + ".tmp";
            try
            {
                if (WriteDelay > TimeSpan.Zero)
                {
                    Thread.Sleep(WriteDelay);
                }

                File.WriteAllBytes(temp, write.Bytes);
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temp, path, null);
                    }
                    catch (FileNotFoundException)
                    {
                        // The destination vanished between the check and the
                        // replace; a plain move is still atomic enough here.
                        File.Move(temp, path);
                    }
                }
                else
                {
                    File.Move(temp, path);
                }

                Interlocked.Increment(ref successfulWrites);
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref failedWrites);
                DeleteBestEffort(temp);
                Debug.LogWarning(
                    "[FileWorldStore] could not write '" + path + "': " + exception.Message
                        + "; the previous save is untouched.");
            }
            finally
            {
                FinishWrite(write.Version);
            }
        }

        private void FinishWrite(long version)
        {
            DrainWaiter[] ready;
            lock (gate)
            {
                if (version > completed)
                {
                    completed = version;
                }

                int count = 0;
                for (int i = 0; i < drainWaiters.Count; i++)
                {
                    if (drainWaiters[i].Target <= completed)
                    {
                        count++;
                    }
                }

                if (count == 0)
                {
                    return;
                }

                ready = new DrainWaiter[count];
                int index = 0;
                for (int i = drainWaiters.Count - 1; i >= 0; i--)
                {
                    if (drainWaiters[i].Target <= completed)
                    {
                        ready[index++] = drainWaiters[i];
                        drainWaiters.RemoveAt(i);
                    }
                }
            }

            for (int i = 0; i < ready.Length; i++)
            {
                ready[i].Complete();
            }
        }

        private static void DeleteBestEffort(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // A leftover temp file is harmless; the next write uses a new name.
            }
        }

        private static string ValidateWorldName(string worldName)
        {
            if (string.IsNullOrWhiteSpace(worldName))
            {
                throw new ArgumentException("A world name is required.", nameof(worldName));
            }

            if (worldName == "." || worldName == ".."
                || worldName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || worldName.IndexOf(Path.DirectorySeparatorChar) >= 0
                || worldName.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            {
                throw new ArgumentException(
                    "A world name must be a single file-system-safe name without path separators.",
                    nameof(worldName));
            }

            return worldName;
        }

        private sealed class PendingWrite
        {
            public PendingWrite(byte[] bytes, long version)
            {
                Bytes = bytes;
                Version = version;
            }

            public byte[] Bytes { get; set; }

            public long Version { get; set; }
        }

        private sealed class DrainWaiter
        {
            private readonly TaskCompletionSource<bool> source =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public DrainWaiter(long target)
            {
                Target = target;
            }

            public long Target { get; }

            public Task Task
            {
                get { return source.Task; }
            }

            public void Complete()
            {
                source.TrySetResult(true);
            }
        }
    }
}
