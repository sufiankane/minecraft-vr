using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Cubeglass.Voxel.Tests
{
    [TestFixture]
    public sealed class WorldTests
    {
        private static readonly ChunkCoord Origin = new ChunkCoord(0, 0, 0);
        private static readonly BlockId Stone = new BlockId(1);
        private static readonly BlockId Dirt = new BlockId(2);

        [Test]
        public void GetOnAnUnloadedCellReturnsAir()
        {
            var world = new World();

            Assert.That(world.Get(new Int3(0, 0, 0)), Is.EqualTo(BlockId.Air));
            Assert.That(world.IsLoaded(new Int3(0, 0, 0)), Is.False);
        }

        [Test]
        public void LoadedChunkIsVisibleThroughGetAndIsLoaded()
        {
            World world = TestWorld.CreateLoaded(Origin, (new Int3(2, 3, 4), Stone));

            Assert.That(world.Get(new Int3(2, 3, 4)), Is.EqualTo(Stone));
            Assert.That(world.Get(new Int3(2, 3, 5)), Is.EqualTo(BlockId.Air));
            Assert.That(world.IsLoaded(new Int3(2, 3, 4)), Is.True);
        }

        [Test]
        public void NegativeCellsMapToTheirFloorDividedChunk()
        {
            var corner = new ChunkCoord(-1, -1, -1);
            World world = TestWorld.CreateLoaded(corner, (new Int3(15, 15, 15), Stone));

            Assert.That(world.Get(new Int3(-1, -1, -1)), Is.EqualTo(Stone));
            Assert.That(world.IsLoaded(new Int3(-1, -1, -1)), Is.True);
            Assert.That(world.Get(new Int3(-2, -1, -1)), Is.EqualTo(BlockId.Air));
        }

        [Test]
        public void ApplyWithMatchingExpectedSetsTheBlockAndRaisesChunkChangedOnce()
        {
            World world = TestWorld.CreateLoaded(Origin);
            var changed = new List<ChunkCoord>();
            world.ChunkChanged += edit => changed.Add(edit.Chunk);
            var command = new EditCommand(new Int3(3, 4, 5), BlockId.Air, Stone, 7);

            EditResult result = world.Apply(in command);

            Assert.That(result, Is.EqualTo(EditResult.Applied));
            Assert.That(world.Get(new Int3(3, 4, 5)), Is.EqualTo(Stone));
            Assert.That(changed, Is.EqualTo(new[] { Origin }));
        }

        [Test]
        public void ChunkChangedCarriesTheChunkCellAndBlockTransition()
        {
            World world = TestWorld.CreateLoaded(Origin);
            var edits = new List<ChunkEdit>();
            world.ChunkChanged += edits.Add;
            var command = new EditCommand(new Int3(3, 4, 5), BlockId.Air, Stone, 7);

            Assert.That(world.Apply(in command), Is.EqualTo(EditResult.Applied));

            Assert.That(edits, Has.Count.EqualTo(1));
            Assert.That(edits[0].Chunk, Is.EqualTo(Origin));
            Assert.That(edits[0].Cell, Is.EqualTo(new Int3(3, 4, 5)));
            Assert.That(edits[0].Previous, Is.EqualTo(BlockId.Air));
            Assert.That(edits[0].New, Is.EqualTo(Stone));
        }

        [Test]
        public void AThrowingSubscriberDoesNotAbortTheEditOrLaterSubscribers()
        {
            World world = TestWorld.CreateLoaded(Origin);
            var faults = new List<Exception>();
            int laterCalls = 0;
            world.ChunkChanged += _ => throw new InvalidOperationException("subscriber one");
            world.ChunkChanged += _ => laterCalls++;
            world.SubscriberFaulted += faults.Add;
            var command = new EditCommand(new Int3(0, 0, 0), BlockId.Air, Stone, 1);

            EditResult result = world.Apply(in command);

            Assert.That(result, Is.EqualTo(EditResult.Applied), "the edit itself must still succeed");
            Assert.That(world.Get(new Int3(0, 0, 0)), Is.EqualTo(Stone));
            Assert.That(laterCalls, Is.EqualTo(1), "the later subscriber must still observe the edit");
            Assert.That(world.SubscriberFaultCount, Is.EqualTo(1));
            Assert.That(faults, Has.Count.EqualTo(1));
            Assert.That(faults[0], Is.TypeOf<InvalidOperationException>());
            Assert.That(faults[0].Message, Is.EqualTo("subscriber one"));
        }

        [Test]
        public void AFaultHookThatThrowsIsAlsoIsolated()
        {
            World world = TestWorld.CreateLoaded(Origin);
            world.ChunkChanged += _ => throw new InvalidOperationException("subscriber");
            world.SubscriberFaulted += _ => throw new InvalidOperationException("fault hook");
            var command = new EditCommand(new Int3(0, 0, 0), BlockId.Air, Stone, 1);

            Assert.DoesNotThrow(() => world.Apply(in command));
            Assert.That(world.SubscriberFaultCount, Is.EqualTo(1));
        }

        [Test]
        public void UnsubscribedHandlerStopsReceivingChunkChanged()
        {
            World world = TestWorld.CreateLoaded(Origin);
            int calls = 0;
            Action<ChunkEdit> handler = _ => calls++;
            world.ChunkChanged += handler;
            var first = new EditCommand(new Int3(0, 0, 0), BlockId.Air, Stone, 1);
            Assert.That(world.Apply(in first), Is.EqualTo(EditResult.Applied));
            Assert.That(calls, Is.EqualTo(1));

            world.ChunkChanged -= handler;
            var second = new EditCommand(new Int3(0, 0, 0), Stone, Dirt, 2);
            Assert.That(world.Apply(in second), Is.EqualTo(EditResult.Applied));
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void ApplyWithMismatchingExpectedIsRejectedAndChangesNothing()
        {
            World world = TestWorld.CreateLoaded(Origin, (new Int3(1, 1, 1), Stone));
            int events = 0;
            world.ChunkChanged += _ => events++;
            var command = new EditCommand(new Int3(1, 1, 1), Dirt, Dirt, 1);

            EditResult result = world.Apply(in command);

            Assert.That(result, Is.EqualTo(EditResult.Rejected));
            Assert.That(world.Get(new Int3(1, 1, 1)), Is.EqualTo(Stone));
            Assert.That(events, Is.Zero);
        }

        [Test]
        public void ApplyOnAnUnloadedCellIsRejectedAndChangesNothing()
        {
            var world = new World();
            int events = 0;
            world.ChunkChanged += _ => events++;
            var command = new EditCommand(new Int3(0, 0, 0), BlockId.Air, Stone, 1);

            EditResult result = world.Apply(in command);

            Assert.That(result, Is.EqualTo(EditResult.Rejected));
            Assert.That(world.Get(new Int3(0, 0, 0)), Is.EqualTo(BlockId.Air));
            Assert.That(events, Is.Zero);
        }

        [Test]
        public void ChainedAppliesFollowTheExpectedValueFromAirToStoneToDirt()
        {
            World world = TestWorld.CreateLoaded(Origin);
            var changed = new List<ChunkCoord>();
            world.ChunkChanged += edit => changed.Add(edit.Chunk);
            var toStone = new EditCommand(new Int3(0, 0, 0), BlockId.Air, Stone, 1);
            var toDirt = new EditCommand(new Int3(0, 0, 0), Stone, Dirt, 2);
            var staleStone = new EditCommand(new Int3(0, 0, 0), Stone, Dirt, 3);

            Assert.That(world.Apply(in toStone), Is.EqualTo(EditResult.Applied));
            Assert.That(world.Apply(in toDirt), Is.EqualTo(EditResult.Applied));
            Assert.That(world.Apply(in staleStone), Is.EqualTo(EditResult.Rejected));
            Assert.That(world.Get(new Int3(0, 0, 0)), Is.EqualTo(Dirt));
            Assert.That(changed, Is.EqualTo(new[] { Origin, Origin }));
        }

        [Test]
        public void ApplyWorksForNegativeCells()
        {
            var corner = new ChunkCoord(-1, -1, -1);
            World world = TestWorld.CreateLoaded(corner, (new Int3(15, 15, 15), Stone));
            var changed = new List<ChunkCoord>();
            world.ChunkChanged += edit => changed.Add(edit.Chunk);
            var command = new EditCommand(new Int3(-1, -1, -1), Stone, Dirt, 4);

            Assert.That(world.Apply(in command), Is.EqualTo(EditResult.Applied));
            Assert.That(world.Get(new Int3(-1, -1, -1)), Is.EqualTo(Dirt));
            Assert.That(changed, Is.EqualTo(new[] { corner }));
        }

        [Test]
        public void LoadChunkReplacesAnExistingChunkAndTryGetChunkReturnsIt()
        {
            World world = TestWorld.CreateLoaded(Origin, (new Int3(0, 0, 0), Stone));
            Chunk replacement = TestWorld.CreateChunk(Origin, (new Int3(0, 0, 0), Dirt));

            world.LoadChunk(replacement);

            Assert.That(world.TryGetChunk(Origin), Is.SameAs(replacement));
            Assert.That(world.GetChunk(Origin), Is.SameAs(replacement));
            Assert.That(world.Get(new Int3(0, 0, 0)), Is.EqualTo(Dirt));
        }

        [Test]
        public void TryGetChunkReturnsNullForAnUnknownChunk()
        {
            var world = new World();

            Assert.That(world.TryGetChunk(Origin), Is.Null);
            Assert.That(world.GetChunk(Origin), Is.Null);
        }

        [Test]
        public void WorldKeepsTheOptionalGeneratorAndStore()
        {
            var store = new InMemoryWorldStore();
            World world = TestWorld.CreateWithGenerator((coord, seed) => TestWorld.CreateChunk(coord));

            Assert.That(world.Generator, Is.Not.Null);
            Assert.That(world.Generator!.Generate(Origin, 42).Coord, Is.EqualTo(Origin));

            var withStore = new World(null, store);
            Assert.That(withStore.Store, Is.SameAs(store));
        }

        [Test]
        public void SnapshotIsACopyThatDoesNotObserveLaterWrites()
        {
            Chunk chunk = TestWorld.CreateChunk(Origin, (new Int3(1, 2, 3), Stone));

            ChunkSnapshot snapshot = chunk.Snapshot();
            chunk.Set(new Int3(1, 2, 3), Dirt);

            Assert.That(snapshot.Coord, Is.EqualTo(Origin));
            Assert.That(snapshot.Get(new Int3(1, 2, 3)), Is.EqualTo(Stone));
            Assert.That(chunk.Get(new Int3(1, 2, 3)), Is.EqualTo(Dirt));
        }

        [Test]
        public void ChunkGetRejectsOutOfRangeLocalCells()
        {
            Chunk chunk = TestWorld.CreateChunk(Origin);

            Assert.Throws<ArgumentOutOfRangeException>(() => chunk.Get(new Int3(16, 0, 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => chunk.Get(new Int3(0, -1, 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => chunk.Get(new Int3(0, 0, 16)));
        }
    }

    [TestFixture]
    public sealed class InMemoryWorldStoreTests
    {
        private static readonly ChunkCoord Origin = new ChunkCoord(0, 0, 0);

        [Test]
        public async Task SaveThenLoadReturnsAnEqualDelta()
        {
            var store = new InMemoryWorldStore();
            ChunkDelta delta = CreateDelta(Origin);

            await store.SaveAsync(Origin, delta, CancellationToken.None);
            ChunkDelta? loaded = await store.LoadAsync(Origin, CancellationToken.None);

            Assert.That(loaded, Is.EqualTo(delta));
        }

        [Test]
        public async Task LoadReturnsNullForAnUnknownChunk()
        {
            var store = new InMemoryWorldStore();

            ChunkDelta? loaded = await store.LoadAsync(new ChunkCoord(4, 5, 6), CancellationToken.None);

            Assert.That(loaded, Is.Null);
        }

        [Test]
        public async Task SaveOverwritesThePreviousDelta()
        {
            var store = new InMemoryWorldStore();
            ChunkDelta first = CreateDelta(Origin);
            var second = new ChunkDelta(Origin, new Dictionary<Int3, BlockId> { [new Int3(9, 9, 9)] = new BlockId(3) });

            await store.SaveAsync(Origin, first, CancellationToken.None);
            await store.SaveAsync(Origin, second, CancellationToken.None);
            ChunkDelta? loaded = await store.LoadAsync(Origin, CancellationToken.None);

            Assert.That(loaded, Is.EqualTo(second));
        }

        [Test]
        public async Task ChunksAreStoredIndependently()
        {
            var store = new InMemoryWorldStore();
            var other = new ChunkCoord(1, 0, 0);
            ChunkDelta originDelta = CreateDelta(Origin);
            ChunkDelta otherDelta = CreateDelta(other);

            await store.SaveAsync(Origin, originDelta, CancellationToken.None);
            await store.SaveAsync(other, otherDelta, CancellationToken.None);

            Assert.That(await store.LoadAsync(Origin, CancellationToken.None), Is.EqualTo(originDelta));
            Assert.That(await store.LoadAsync(other, CancellationToken.None), Is.EqualTo(otherDelta));
        }

        [Test]
        public void SaveAndLoadHonourAPreCancelledToken()
        {
            var store = new InMemoryWorldStore();
            using var source = new CancellationTokenSource();
            source.Cancel();
            ChunkDelta delta = CreateDelta(Origin);

            Assert.ThrowsAsync<OperationCanceledException>(
                async () => await store.SaveAsync(Origin, delta, source.Token));
            Assert.ThrowsAsync<OperationCanceledException>(
                async () => await store.LoadAsync(Origin, source.Token));
        }

        private static ChunkDelta CreateDelta(ChunkCoord coord)
        {
            return new ChunkDelta(
                coord,
                new Dictionary<Int3, BlockId>
                {
                    [new Int3(1, 2, 3)] = new BlockId(4),
                    [new Int3(15, 0, 15)] = new BlockId(5),
                });
        }
    }
}
