using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using Cubeglass.CoreMath;
using Cubeglass.Streaming;
using Cubeglass.Voxel;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Cubeglass.Unity.Rendering.Tests
{
    /// <summary>
    /// EditMode coverage for the S7 review-wave manager fixes: precise remesh
    /// dirty tracking from the cell-carrying <c>ChunkChanged</c> event
    /// (TD-016/TD-017), same-frame edit/compaction survival (TD-015),
    /// once-only boot replay (TD-018), the asynchronous stored-delta load
    /// (TD-060 M-3) and the generated chunk palette material (TD-060 M-5).
    /// </summary>
    public sealed class ChunkViewManagerEditModeTests
    {
        private GameObject root;
        private readonly List<string> tempRoots = new List<string>();

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("ChunkManagerTestRoot");
        }

        [TearDown]
        public void TearDown()
        {
            if (root != null)
            {
                Object.DestroyImmediate(root);
                root = null;
            }

            for (int i = 0; i < tempRoots.Count; i++)
            {
                try
                {
                    if (Directory.Exists(tempRoots[i]))
                    {
                        Directory.Delete(tempRoots[i], true);
                    }
                }
                catch (Exception exception) when (
                    exception is IOException || exception is UnauthorizedAccessException)
                {
                    // A leftover unique temp world never affects other tests.
                }
            }

            tempRoots.Clear();
        }

        [Test]
        public void PreciseEditDirtiesOnlyTheChunksThatCanChange()
        {
            var manager = NewManager();
            var coord = new ChunkCoord(0, 0, 0);
            manager.OnLoad(coord);

            Int3 cell = ChunkMath.ToWorld(coord, new Int3(5, 5, 5));
            Assert.AreEqual(1, ChunkEditPropagation.GetAffectedChunks(cell).Count, "fixture: an interior cell affects one chunk");

            Assert.AreEqual(
                EditResult.Applied,
                manager.World.Apply(new EditCommand(cell, manager.World.Get(cell), new BlockId(5), 0L)),
                "fixture edit must apply");

            Assert.AreEqual(1, manager.PreciseEdits, "the cell-carrying event reached the precise path (TD-016)");
            Assert.AreEqual(1, manager.DirtyChunks, "only the edited chunk is dirty (TD-017)");
            Assert.IsTrue(manager.IsDirty(coord));
        }

        [Test]
        public void PreciseRemeshAfterAWorldEditRebuildsOnlyTheEditedChunk()
        {
            var manager = NewManager();
            var centre = new ChunkCoord(0, 0, 0);
            var superset = new ChunkCoord[ChunkViewManager.RemeshNeighbourhoodSize];
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        OnLoadNow(manager, new ChunkCoord(x, y, z));
                    }
                }
            }

            manager.OnUpload(centre);
            Int3 cell = ChunkMath.ToWorld(centre, new Int3(5, 5, 5));
            Assert.AreEqual(
                EditResult.Applied,
                manager.World.Apply(new EditCommand(cell, manager.World.Get(cell), new BlockId(5), 0L)),
                "fixture edit must apply");

            Assert.AreEqual(
                ChunkViewManager.RemeshNeighbourhoodSize,
                ChunkViewManager.FillRemeshNeighbourhood(centre, superset),
                "fixture: the superset is all 27 chunks");
            Assert.AreEqual(1, manager.DirtyChunks, "the precise set dirties only the edited chunk");
            manager.ProcessDirtyRemeshes();
            Assert.AreEqual(1, manager.RemeshedChunks, "fewer rebuilds than the 27-chunk superset, with identical output");
        }

        [Test]
        public void BoundaryEditDirtiesOnlyTheChunksAcrossThatBoundary()
        {
            var manager = NewManager();
            var coord = new ChunkCoord(0, 0, 0);
            var below = new ChunkCoord(0, -1, 0);
            var side = new ChunkCoord(1, 0, 0);
            OnLoadNow(manager, coord);
            OnLoadNow(manager, below);
            OnLoadNow(manager, side);

            Int3 cell = ChunkMath.ToWorld(coord, new Int3(5, 0, 5));
            IReadOnlyList<ChunkCoord> affected = ChunkEditPropagation.GetAffectedChunks(cell);
            Assert.AreEqual(2, affected.Count, "fixture: a y=0 face cell affects the chunk below too");

            Assert.AreEqual(
                EditResult.Applied,
                manager.World.Apply(new EditCommand(cell, manager.World.Get(cell), new BlockId(4), 0L)),
                "fixture edit must apply");

            Assert.AreEqual(2, manager.DirtyChunks, "exactly the edited chunk and its y-1 neighbour are dirty");
            Assert.IsTrue(manager.IsDirty(coord));
            Assert.IsTrue(manager.IsDirty(below));
            Assert.IsFalse(manager.IsDirty(side), "the x+1 neighbour cannot be affected by an interior x/z cell");
            Assert.AreEqual(1, manager.PreciseEdits);
        }

        [Test]
        public void SameFrameEditAndCompactionSurviveAndRemesh()
        {
            var manager = NewManager();
            var coord = new ChunkCoord(0, 0, 0);
            manager.OnLoad(coord);
            manager.OnUpload(coord);
            Assert.IsTrue(manager.TryGetView(coord, out ChunkView view), "fixture: the chunk must have a view");
            int before = view.Mesh.vertexCount;

            Int3 cell = FindSurfaceCell(manager.World, coord);
            Assert.AreNotEqual(BlockId.Air, manager.World.Get(cell), "fixture: the surface cell is solid");
            Assert.AreEqual(BlockId.Air, manager.World.Get(new Int3(cell.X, cell.Y + 1, cell.Z)), "fixture: air above the surface");

            World preCompaction = manager.World;
            Assert.AreEqual(
                EditResult.Applied,
                preCompaction.Apply(new EditCommand(cell, manager.World.Get(cell), BlockId.Air, 0L)),
                "the same-frame edit must apply");

            // Compact before the dirty queue drained: the edit and the
            // compaction share a frame (TD-015).
            manager.CompactWorldNow();
            Assert.AreNotSame(preCompaction, manager.World, "the world must have been replaced");
            Assert.AreEqual(BlockId.Air, manager.World.Get(cell), "the same-frame edit must survive the compaction");

            manager.ProcessDirtyRemeshes();

            Assert.GreaterOrEqual(manager.RemeshedChunks, 1L, "the edit must still remesh after the compaction");
            Assert.IsTrue(manager.TryGetView(coord, out ChunkView remeshed));
            Assert.AreNotEqual(before, remeshed.Mesh.vertexCount, "the remesh must reflect the edit (a block was removed)");
        }

        [Test]
        public void StoredDeltaLoadsOffTheMainThread()
        {
            string tempRoot = NewTempRoot();
            var store = new FileWorldStore("m3", tempRoot);
            var coord = new ChunkCoord(0, 0, 0);
            var local = new Int3(1, 2, 3);
            store.SaveAsync(coord, DeltaFor(coord, local, new BlockId(5)), CancellationToken.None);
            Assert.IsTrue(store.WaitForPendingWrites(TimeSpan.FromSeconds(10)).Succeeded, "fixture delta must write");

            store.ReadDelay = TimeSpan.FromMilliseconds(300);
            var manager = NewManager();
            manager.Store = store;

            var watch = Stopwatch.StartNew();
            manager.OnLoad(coord);
            watch.Stop();

            Assert.Less(watch.ElapsedMilliseconds, 100, "OnLoad must return without waiting for disk IO (TD-060 M-3)");
            Assert.AreEqual(0, manager.DeltasLoaded, "the delta is not applied yet");
            Assert.AreEqual(1, manager.DeferredDeltaLoads, "the load is queued on the store pump");
            Assert.AreEqual(1, manager.DeltaLoadsQueued);
            Assert.AreEqual(1, manager.LoadedChunks, "the chunk itself is generated and resident");

            var deadline = Stopwatch.StartNew();
            while (manager.DeltasLoaded == 0 && deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                manager.ProcessDeltaLoads();
                Thread.Sleep(5);
            }

            Assert.AreEqual(1, manager.DeltasLoaded, "the background load must complete");
            Assert.AreEqual(0, manager.DeferredDeltaLoads);
            Assert.AreEqual(new BlockId(5), manager.World.Get(ChunkMath.ToWorld(coord, local)), "the delta applied at world coordinates");
            store.Dispose();
        }

        [Test]
        public void GeneratedChunksCarryThePaletteMaterial()
        {
            var manager = NewManager();
            Assert.IsNotNull(manager.ViewMaterial, "a manager without an assigned material must generate the palette one (M-5)");
            Assert.IsNotNull(manager.ViewMaterial.shader, "the generated material needs a shader");
            var atlas = manager.ViewMaterial.mainTexture as Texture2D;
            Assert.IsNotNull(atlas, "the generated material must carry the palette atlas");
            Assert.AreEqual(ChunkPalette.AtlasTiles, atlas.width);
            Assert.AreEqual(ChunkPalette.AtlasTiles, atlas.height);

            var pixels = atlas.GetPixels32();
            var distinct = new HashSet<Color32>();
            for (int i = 0; i < pixels.Length; i++)
            {
                distinct.Add(pixels[i]);
            }

            Assert.GreaterOrEqual(distinct.Count, 4, "the palette must colour the slice blocks distinctly");

            var coord = new ChunkCoord(0, 0, 0);
            manager.OnLoad(coord);
            manager.OnUpload(coord);
            Assert.IsTrue(manager.TryGetView(coord, out ChunkView view));
            Assert.AreSame(manager.ViewMaterial, view.Renderer.sharedMaterial, "chunk views must use the generated palette material");
        }

        [Test]
        public void BootReplaysTheStoredDeltaOnceAndDerivesTheMergedMap()
        {
            string tempRoot = NewTempRoot();
            var store = new FileWorldStore("td18", tempRoot);
            var coord = new ChunkCoord(0, 0, 0);
            var cellA = new Int3(1, 2, 3);
            var cellB = new Int3(4, 5, 6);
            var both = new Dictionary<Int3, BlockId> { { cellA, new BlockId(5) }, { cellB, new BlockId(4) } };
            store.SaveAsync(coord, new ChunkDelta(coord, both), CancellationToken.None);
            Assert.IsTrue(store.WaitForPendingWrites(TimeSpan.FromSeconds(10)).Succeeded, "fixture delta must write");

            var cache = new TestEditCache();
            var manager = NewManager();
            manager.Store = store;
            manager.AppliedEdits = cache;

            manager.OnLoad(coord);
            PumpUntilLoaded(manager, 1);

            Assert.AreEqual(1, manager.DeltasLoaded);
            Assert.AreEqual(2, manager.DeltaEditsApplied, "both stored cells apply exactly once");
            Assert.AreEqual(0, manager.LiveEditsReapplied, "no merged map exists on the first boot");
            Assert.AreEqual(0, manager.EditsSkippedAlreadyApplied);
            Assert.AreEqual(new BlockId(5), manager.World.Get(ChunkMath.ToWorld(coord, cellA)));
            Assert.AreEqual(new BlockId(4), manager.World.Get(ChunkMath.ToWorld(coord, cellB)));

            // Reload in the same session: the store seeds the merged map, and
            // the replay must not double-apply or double-count (TD-018).
            manager.OnUnload(coord);
            OnLoadNow(manager, coord);
            PumpUntilLoaded(manager, 2);

            Assert.AreEqual(2, manager.DeltasLoaded, "the second boot loaded the stored delta");
            Assert.AreEqual(4, manager.DeltaEditsApplied, "each boot applies the stored delta once");
            Assert.AreEqual(0, manager.LiveEditsReapplied, "merged cells already matching the world are skipped");
            Assert.AreEqual(2, manager.EditsSkippedAlreadyApplied, "the merged map's two cells are derived, not re-applied");
            Assert.AreEqual(new BlockId(5), manager.World.Get(ChunkMath.ToWorld(coord, cellA)));
            Assert.AreEqual(new BlockId(4), manager.World.Get(ChunkMath.ToWorld(coord, cellB)));

            store.Dispose();
        }

        private ChunkViewManager NewManager()
        {
            var manager = root.AddComponent<ChunkViewManager>();
            manager.Initialize(new StreamingConfig(), 11L, 32);
            return manager;
        }

        /// <summary>
        /// Generates <paramref name="coord"/> in the current Unity frame. Real
        /// play advances frames between loads; an EditMode test does not, and
        /// <see cref="ChunkViewManager.OnLoad"/> deliberately generates at most
        /// one chunk per frame. Resetting the frame guard mirrors "the scheduler
        /// asked for this chunk on a later frame".
        /// </summary>
        private static void OnLoadNow(ChunkViewManager manager, ChunkCoord coord)
        {
            FieldInfo field = typeof(ChunkViewManager).GetField(
                "lastGenerateFrame", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "ChunkViewManager.lastGenerateFrame must exist");
            field.SetValue(manager, int.MinValue);
            manager.OnLoad(coord);
        }

        private static ChunkDelta DeltaFor(ChunkCoord coord, Int3 local, BlockId block)
        {
            return new ChunkDelta(coord, new Dictionary<Int3, BlockId> { { local, block } });
        }

        private static void PumpUntilLoaded(ChunkViewManager manager, long expectedLoads)
        {
            var deadline = Stopwatch.StartNew();
            while (manager.DeltasLoaded < expectedLoads && deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                manager.ProcessDeltaLoads();
                Thread.Sleep(5);
            }
        }

        private static Int3 FindSurfaceCell(World world, ChunkCoord coord)
        {
            for (int z = 4; z < ChunkMath.ChunkSize - 4; z++)
            {
                for (int x = 4; x < ChunkMath.ChunkSize - 4; x++)
                {
                    for (int y = ChunkMath.ChunkSize - 2; y > 0; y--)
                    {
                        Int3 worldCell = ChunkMath.ToWorld(coord, new Int3(x, y, z));
                        if (world.Get(worldCell) == BlockId.Air)
                        {
                            continue;
                        }

                        if (world.Get(new Int3(worldCell.X, worldCell.Y + 1, worldCell.Z)) == BlockId.Air)
                        {
                            return worldCell;
                        }
                    }
                }
            }

            Assert.Fail("fixture: no exposed surface cell found in the generated chunk");
            return default;
        }

        private string NewTempRoot()
        {
            string path = Path.Combine(Path.GetTempPath(), "cg-manager-" + Guid.NewGuid().ToString("N"));
            tempRoots.Add(path);
            return path;
        }

        private sealed class TestEditCache : IAppliedEditCache
        {
            private readonly Dictionary<ChunkCoord, ChunkDelta> maps = new Dictionary<ChunkCoord, ChunkDelta>();

            public void TrackLoadedDelta(ChunkCoord coord, ChunkDelta delta)
            {
                if (!maps.TryGetValue(coord, out ChunkDelta existing))
                {
                    maps.Add(coord, delta);
                    return;
                }

                var merged = new Dictionary<Int3, BlockId>(delta.Edits.Count + existing.Edits.Count);
                foreach (KeyValuePair<Int3, BlockId> edit in delta.Edits)
                {
                    merged[edit.Key] = edit.Value;
                }

                foreach (KeyValuePair<Int3, BlockId> edit in existing.Edits)
                {
                    merged[edit.Key] = edit.Value;
                }

                maps[coord] = new ChunkDelta(coord, merged);
            }

            public bool TryGetAccumulatedDelta(ChunkCoord coord, out ChunkDelta delta)
            {
                return maps.TryGetValue(coord, out delta) && delta.Edits.Count > 0;
            }
        }
    }
}
