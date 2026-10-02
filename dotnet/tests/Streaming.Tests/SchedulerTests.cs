using System;
using System.Collections.Generic;
using Cubeglass.CoreMath;
using Cubeglass.Voxel;
using NUnit.Framework;

namespace Cubeglass.Streaming.Tests
{
    [TestFixture]
    public sealed class StreamingConfigTests
    {
        [Test]
        public void DefaultsMatchTheBrief()
        {
            var config = new StreamingConfig();

            Assert.That(config.ViewDistanceChunks, Is.EqualTo(8));
            Assert.That(config.UnloadHysteresis, Is.EqualTo(2));
            Assert.That(config.MaxLoadsPerFrame, Is.EqualTo(4));
            Assert.That(config.MaxUnloadsPerFrame, Is.EqualTo(4));
            Assert.That(config.MaxMeshUploadsPerFrame, Is.EqualTo(4));
            Assert.That(config.VerticalRadiusChunks, Is.EqualTo(2f));
        }
    }

    [TestFixture]
    public sealed class ChunkStreamingSchedulerTests
    {
        [Test]
        public void ConstructorRejectsNullConfig()
        {
            Assert.Throws<ArgumentNullException>(() => new ChunkStreamingScheduler(null!, 1L));
        }

        [Test]
        public void SeedIsPreserved()
        {
            var scheduler = new ChunkStreamingScheduler(Config(), 424242L);

            Assert.That(scheduler.Seed, Is.EqualTo(424242L));
        }

        [Test]
        public void UpdateRejectsNullTarget()
        {
            var scheduler = new ChunkStreamingScheduler(Config(), 1L);

            Assert.Throws<ArgumentNullException>(() => scheduler.Update(null!, Center(0, 0, 0)));
        }

        [Test]
        public void PlayerPositionMapsToFloorDividedChunks()
        {
            Assert.That(FirstLoad(new Vec3(15.9, 15.9, 15.9)), Is.EqualTo(new ChunkCoord(0, 0, 0)));
            Assert.That(FirstLoad(new Vec3(16.0, 16.0, 16.0)), Is.EqualTo(new ChunkCoord(1, 1, 1)));
            Assert.That(FirstLoad(new Vec3(-0.1, -0.1, -0.1)), Is.EqualTo(new ChunkCoord(-1, -1, -1)));
            Assert.That(FirstLoad(new Vec3(-16.0, -16.0, -16.0)), Is.EqualTo(new ChunkCoord(-1, -1, -1)));
            Assert.That(FirstLoad(new Vec3(-16.1, -16.1, -16.1)), Is.EqualTo(new ChunkCoord(-2, -2, -2)));
        }

        [Test]
        public void LoadsAreIssuedNearestFirstThenInCoordinateOrder()
        {
            var scheduler = new ChunkStreamingScheduler(
                Config(view: 1, vertical: 0, hysteresis: 0, loadBudget: 16),
                1L);

            List<StreamingAction> actions = RunFrame(scheduler, Center(0, 0, 0));

            var expected = new[]
            {
                Action(StreamingActionKind.Load, 0, 0, 0),
                Action(StreamingActionKind.Load, -1, 0, 0),
                Action(StreamingActionKind.Load, 0, 0, -1),
                Action(StreamingActionKind.Load, 0, 0, 1),
                Action(StreamingActionKind.Load, 1, 0, 0),
                Action(StreamingActionKind.Load, -1, 0, -1),
                Action(StreamingActionKind.Load, -1, 0, 1),
                Action(StreamingActionKind.Load, 1, 0, -1),
                Action(StreamingActionKind.Load, 1, 0, 1),
            };

            Assert.That(actions, Is.EqualTo(expected));
        }

        [Test]
        public void LoadBudgetIsEnforcedEveryFrameDuringAChunkBurst()
        {
            var scheduler = new ChunkStreamingScheduler(
                Config(view: 8, vertical: 0, hysteresis: 0, loadBudget: 4),
                2L);
            Vec3 position = Center(0, 0, 0);
            int total = 0;

            for (int frame = 0; frame < 200; frame++)
            {
                List<StreamingAction> actions = RunFrame(scheduler, position);
                int loads = CountKind(actions, StreamingActionKind.Load);
                Assert.That(loads, Is.LessThanOrEqualTo(4), $"frame {frame} exceeded the load budget");
                Assert.That(CountKind(actions, StreamingActionKind.Unload), Is.Zero, $"frame {frame}");
                total += loads;

                if (actions.Count == 0)
                {
                    Assert.That(frame, Is.GreaterThan(13), "the 50-chunk burst must take more than thirteen frames");
                    break;
                }
            }

            Assert.That(total, Is.GreaterThanOrEqualTo(50), "the burst must contain at least fifty chunks");
            Assert.That(scheduler.LoadedCount, Is.EqualTo(17 * 17));
            Assert.That(scheduler.DesiredCount, Is.EqualTo(17 * 17));
            Assert.That(scheduler.LoadedChunks.Count, Is.EqualTo(scheduler.LoadedCount));
            Assert.That(RunFrame(scheduler, position).Count, Is.Zero);
        }

        [Test]
        public void NonPositiveBudgetsEmitNothing()
        {
            var scheduler = new ChunkStreamingScheduler(
                Config(view: 1, vertical: 0, hysteresis: 0, loadBudget: 0, unloadBudget: -1, uploadBudget: 0),
                12L);

            Assert.That(RunFrame(scheduler, Center(0, 0, 0)).Count, Is.Zero);
            Assert.That(scheduler.LoadedCount, Is.Zero);

            var origin = new ChunkCoord(0, 0, 0);
            scheduler.NotifyLoaded(origin);
            scheduler.NotifyMeshReady(origin);

            Assert.That(RunFrame(scheduler, Center(0, 0, 0)).Count, Is.Zero, "a zero upload budget is just as strict");
            Assert.That(scheduler.IsPendingMesh(origin), Is.True);
            Assert.That(scheduler.PendingMeshCount, Is.EqualTo(1));
        }

        [Test]
        public void UnloadBudgetIsEnforcedEveryFrame()
        {
            var scheduler = new ChunkStreamingScheduler(
                Config(view: 1, vertical: 0, hysteresis: 0, loadBudget: 100, unloadBudget: 2),
                3L);
            RunFrame(scheduler, Center(0, 0, 0));
            Assert.That(scheduler.LoadedCount, Is.EqualTo(9));

            Vec3 position = Center(30, 0, 0);
            int total = 0;

            for (int frame = 0; frame < 20; frame++)
            {
                List<StreamingAction> actions = RunFrame(scheduler, position);
                int unloads = CountKind(actions, StreamingActionKind.Unload);
                Assert.That(unloads, Is.LessThanOrEqualTo(2), $"frame {frame} exceeded the unload budget");
                total += unloads;

                if (actions.Count == 0)
                {
                    break;
                }
            }

            Assert.That(total, Is.EqualTo(9));
            Assert.That(scheduler.LoadedCount, Is.EqualTo(9));
            Assert.That(scheduler.IsLoaded(new ChunkCoord(30, 0, 0)), Is.True);
            Assert.That(scheduler.IsLoaded(new ChunkCoord(0, 0, 0)), Is.False);
        }

        [Test]
        public void UploadBudgetIsEnforcedEveryFrame()
        {
            var scheduler = new ChunkStreamingScheduler(
                Config(view: 2, vertical: 0, hysteresis: 0, loadBudget: 100, unloadBudget: 100, uploadBudget: 3),
                4L);
            List<StreamingAction> loads = RunFrame(scheduler, Center(0, 0, 0));
            Assert.That(loads.Count, Is.EqualTo(25), "the whole 5x5 desired box loads in one frame");
            for (int i = 0; i < loads.Count; i++)
            {
                scheduler.NotifyMeshReady(loads[i].Chunk);
            }

            int total = 0;

            for (int frame = 0; frame < 20; frame++)
            {
                List<StreamingAction> actions = RunFrame(scheduler, Center(0, 0, 0));
                int uploads = CountKind(actions, StreamingActionKind.Upload);
                Assert.That(uploads, Is.LessThanOrEqualTo(3), $"frame {frame} exceeded the upload budget");
                total += uploads;

                if (actions.Count == 0)
                {
                    break;
                }
            }

            Assert.That(total, Is.EqualTo(25));
            Assert.That(scheduler.PendingMeshCount, Is.Zero);
            Assert.That(RunFrame(scheduler, Center(0, 0, 0)).Count, Is.Zero);
        }

        [Test]
        public void UploadsArePrioritisedNearestFirst()
        {
            var scheduler = new ChunkStreamingScheduler(
                Config(view: 2, vertical: 0, hysteresis: 0, loadBudget: 100, uploadBudget: 100),
                5L);
            List<StreamingAction> loads = RunFrame(scheduler, Center(0, 0, 0));
            for (int i = 0; i < loads.Count; i++)
            {
                scheduler.NotifyMeshReady(loads[i].Chunk);
            }

            List<StreamingAction> uploads = RunFrame(scheduler, Center(0, 0, 0));

            Assert.That(uploads[0], Is.EqualTo(Action(StreamingActionKind.Upload, 0, 0, 0)));
            Assert.That(uploads[1], Is.EqualTo(Action(StreamingActionKind.Upload, -1, 0, 0)));
            Assert.That(uploads[2], Is.EqualTo(Action(StreamingActionKind.Upload, 0, 0, -1)));
            Assert.That(uploads[3], Is.EqualTo(Action(StreamingActionKind.Upload, 0, 0, 1)));
        }

        [Test]
        public void HysteresisPreventsChurnAtTheBoundary()
        {
            var scheduler = new ChunkStreamingScheduler(
                Config(view: 4, vertical: 0, hysteresis: 2, loadBudget: 1000, unloadBudget: 1000),
                6L);
            RunFrame(scheduler, Center(0, 0, 0));
            Assert.That(scheduler.LoadedCount, Is.EqualTo(81));
            Assert.That(scheduler.IsLoaded(new ChunkCoord(4, 0, 0)), Is.True);

            int totalUnloads = 0;
            for (int frame = 0; frame < 40; frame++)
            {
                Vec3 position = frame % 2 == 0 ? Center(-2, 0, 0) : Center(0, 0, 0);
                List<StreamingAction> actions = RunFrame(scheduler, position);
                totalUnloads += CountKind(actions, StreamingActionKind.Unload);

                if (frame >= 1)
                {
                    Assert.That(
                        actions.Count,
                        Is.Zero,
                        $"frame {frame}: jitter inside the hysteresis window must not produce actions");
                }
            }

            Assert.That(totalUnloads, Is.Zero, "nothing inside the window may unload");

            List<StreamingAction> beyond = RunFrame(scheduler, Center(-3, 0, 0));
            int unloads = 0;
            for (int i = 0; i < beyond.Count; i++)
            {
                if (beyond[i].Kind != StreamingActionKind.Unload)
                {
                    continue;
                }

                unloads++;
                Assert.That(beyond[i].Chunk.X, Is.EqualTo(4), "only the column beyond radius + hysteresis unloads");
            }

            Assert.That(unloads, Is.EqualTo(9));
            Assert.That(scheduler.IsLoaded(new ChunkCoord(3, 0, 0)), Is.True, "the column at radius + hysteresis stays");
        }

        [Test]
        public void VerticalRadiusLimitsTheDesiredBoxAndAppliesHysteresis()
        {
            var scheduler = new ChunkStreamingScheduler(
                Config(view: 0, vertical: 1, hysteresis: 1, loadBudget: 100, unloadBudget: 100),
                7L);
            RunFrame(scheduler, Center(0, 0, 0));

            Assert.That(scheduler.LoadedCount, Is.EqualTo(3));
            Assert.That(scheduler.IsLoaded(new ChunkCoord(0, 1, 0)), Is.True);
            Assert.That(scheduler.IsLoaded(new ChunkCoord(0, -1, 0)), Is.True);
            Assert.That(scheduler.IsLoaded(new ChunkCoord(0, 2, 0)), Is.False, "dy = 2 is outside the vertical radius");

            RunFrame(scheduler, Center(0, 3, 0));

            Assert.That(scheduler.IsLoaded(new ChunkCoord(0, 1, 0)), Is.True, "dy = -2 stays inside the vertical window");
            Assert.That(scheduler.IsLoaded(new ChunkCoord(0, 4, 0)), Is.True);
            Assert.That(scheduler.IsLoaded(new ChunkCoord(0, 0, 0)), Is.False, "dy = -3 is beyond radius + hysteresis");
            Assert.That(scheduler.IsLoaded(new ChunkCoord(0, -1, 0)), Is.False, "dy = -4 is beyond radius + hysteresis");
            Assert.That(scheduler.LoadedCount, Is.EqualTo(4));
        }

        [Test]
        public void NotificationsTrackExternalLoadMeshAndUnload()
        {
            var scheduler = new ChunkStreamingScheduler(
                Config(view: 1, vertical: 0, hysteresis: 0, loadBudget: 100, unloadBudget: 100, uploadBudget: 100),
                8L);
            var origin = new ChunkCoord(0, 0, 0);

            scheduler.NotifyLoaded(origin);
            Assert.That(scheduler.IsLoaded(origin), Is.True);
            Assert.That(scheduler.LoadedCount, Is.EqualTo(1));

            scheduler.NotifyMeshReady(origin);
            Assert.That(scheduler.IsPendingMesh(origin), Is.True);
            Assert.That(scheduler.PendingMeshCount, Is.EqualTo(1));

            List<StreamingAction> actions = RunFrame(scheduler, Center(0, 0, 0));
            Assert.That(actions.Contains(Action(StreamingActionKind.Upload, 0, 0, 0)), Is.True);
            Assert.That(scheduler.PendingMeshCount, Is.Zero);
            Assert.That(scheduler.LoadedCount, Is.EqualTo(9));

            scheduler.NotifyUnloaded(origin);
            Assert.That(scheduler.IsLoaded(origin), Is.False);
            Assert.That(scheduler.LoadedCount, Is.EqualTo(8));

            List<StreamingAction> reloaded = RunFrame(scheduler, Center(0, 0, 0));
            Assert.That(reloaded.Count, Is.EqualTo(1), "the externally unloaded chunk is desired again");
            Assert.That(reloaded[0], Is.EqualTo(Action(StreamingActionKind.Load, 0, 0, 0)));
        }

        [Test]
        public void TargetPathCallsInTheSameOrderAsTheActionList()
        {
            StreamingConfig config = Config(view: 2, vertical: 1, hysteresis: 1, loadBudget: 3, unloadBudget: 2, uploadBudget: 2);
            var plain = new ChunkStreamingScheduler(config, 9L);
            var targeted = new ChunkStreamingScheduler(config, 9L);
            var timeline = new[]
            {
                Center(0, 0, 0),
                Center(1, 0, 0),
                Center(-2, 0, 1),
                Center(-2, 1, 1),
                Center(9, 0, 0),
                Center(9, 0, 0),
                Center(0, 0, 0),
            };

            for (int frame = 0; frame < timeline.Length; frame++)
            {
                List<StreamingAction> expected = RunFrame(plain, timeline[frame]);
                var target = new RecordingTarget();
                IReadOnlyList<StreamingAction> actual = targeted.Update(target, timeline[frame]);

                Assert.That(actual, Is.EqualTo(expected), $"frame {frame} action list");
                Assert.That(target.Calls, Is.EqualTo(expected), $"frame {frame} target calls");

                for (int i = 0; i < expected.Count; i++)
                {
                    if (expected[i].Kind == StreamingActionKind.Load)
                    {
                        plain.NotifyMeshReady(expected[i].Chunk);
                        targeted.NotifyMeshReady(expected[i].Chunk);
                    }
                }
            }
        }

        [Test]
        public void ARejectedUploadStaysPendingAndStopsFurtherUploads()
        {
            var scheduler = new ChunkStreamingScheduler(
                Config(view: 0, vertical: 0, hysteresis: 5, loadBudget: 100, uploadBudget: 4),
                10L);
            var origin = new ChunkCoord(0, 0, 0);
            var neighbour = new ChunkCoord(1, 0, 0);
            scheduler.NotifyLoaded(origin);
            scheduler.NotifyLoaded(neighbour);
            scheduler.NotifyMeshReady(origin);
            scheduler.NotifyMeshReady(neighbour);

            var vetoing = new RecordingTarget { UploadResult = false };
            IReadOnlyList<StreamingAction> rejected = scheduler.Update(vetoing, Center(0, 0, 0));

            Assert.That(rejected.Count, Is.Zero, "a vetoed upload did not happen");
            Assert.That(vetoing.Calls.Count, Is.EqualTo(1), "the veto stops the remaining upload attempts");
            Assert.That(scheduler.PendingMeshCount, Is.EqualTo(2), "both uploads stay pending");

            var accepting = new RecordingTarget();
            IReadOnlyList<StreamingAction> retried = scheduler.Update(accepting, Center(0, 0, 0));

            Assert.That(retried.Count, Is.EqualTo(2));
            Assert.That(retried[0], Is.EqualTo(Action(StreamingActionKind.Upload, 0, 0, 0)));
            Assert.That(retried[1], Is.EqualTo(Action(StreamingActionKind.Upload, 1, 0, 0)));
            Assert.That(scheduler.PendingMeshCount, Is.Zero);
        }

        [Test]
        public void SameTimelineProducesTheSameActionLog()
        {
            StreamingConfig config = Config(view: 3, vertical: 1, hysteresis: 1, loadBudget: 3, unloadBudget: 3, uploadBudget: 3);

            List<StreamingAction> first = RunScript(config, 11L);
            List<StreamingAction> second = RunScript(config, 11L);

            Assert.That(first.Count, Is.GreaterThan(0));
            Assert.That(second, Is.EqualTo(first));
        }

        private static List<StreamingAction> RunScript(StreamingConfig config, long seed)
        {
            var scheduler = new ChunkStreamingScheduler(config, seed);
            var log = new List<StreamingAction>();

            for (int frame = 0; frame < 200; frame++)
            {
                int x = ((frame * 7) % 23) - 11;
                int z = ((frame * 13) % 29) - 14;
                int y = ((frame / 25) % 3) - 1;
                IReadOnlyList<StreamingAction> actions = scheduler.Update(Center(x, y, z));

                for (int i = 0; i < actions.Count; i++)
                {
                    log.Add(actions[i]);
                    if (actions[i].Kind == StreamingActionKind.Load)
                    {
                        scheduler.NotifyMeshReady(actions[i].Chunk);
                    }
                }
            }

            return log;
        }

        private static ChunkCoord FirstLoad(Vec3 position)
        {
            var scheduler = new ChunkStreamingScheduler(
                Config(view: 0, vertical: 0, hysteresis: 0, loadBudget: 1),
                1L);
            List<StreamingAction> actions = RunFrame(scheduler, position);

            Assert.That(actions.Count, Is.EqualTo(1));
            Assert.That(actions[0].Kind, Is.EqualTo(StreamingActionKind.Load));
            return actions[0].Chunk;
        }

        private static StreamingConfig Config(
            int view = 8,
            int vertical = 2,
            int hysteresis = 2,
            int loadBudget = 4,
            int unloadBudget = 4,
            int uploadBudget = 4)
        {
            return new StreamingConfig
            {
                ViewDistanceChunks = view,
                UnloadHysteresis = hysteresis,
                MaxLoadsPerFrame = loadBudget,
                MaxUnloadsPerFrame = unloadBudget,
                MaxMeshUploadsPerFrame = uploadBudget,
                VerticalRadiusChunks = vertical,
            };
        }

        private static Vec3 Center(int x, int y, int z)
        {
            return new Vec3(
                (x * ChunkMath.ChunkSize) + 8.0,
                (y * ChunkMath.ChunkSize) + 8.0,
                (z * ChunkMath.ChunkSize) + 8.0);
        }

        private static StreamingAction Action(StreamingActionKind kind, int x, int y, int z)
        {
            return new StreamingAction(kind, new ChunkCoord(x, y, z));
        }

        private static List<StreamingAction> RunFrame(ChunkStreamingScheduler scheduler, Vec3 position)
        {
            IReadOnlyList<StreamingAction> actions = scheduler.Update(position);
            var copy = new List<StreamingAction>(actions.Count);
            for (int i = 0; i < actions.Count; i++)
            {
                copy.Add(actions[i]);
            }

            return copy;
        }

        private static int CountKind(List<StreamingAction> actions, StreamingActionKind kind)
        {
            int count = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                if (actions[i].Kind == kind)
                {
                    count++;
                }
            }

            return count;
        }

        private sealed class RecordingTarget : IStreamingTarget
        {
            public List<StreamingAction> Calls { get; } = new List<StreamingAction>();

            public bool UploadResult { get; set; } = true;

            public void OnLoad(ChunkCoord chunk)
            {
                Calls.Add(new StreamingAction(StreamingActionKind.Load, chunk));
            }

            public void OnUnload(ChunkCoord chunk)
            {
                Calls.Add(new StreamingAction(StreamingActionKind.Unload, chunk));
            }

            public bool OnUpload(ChunkCoord chunk)
            {
                Calls.Add(new StreamingAction(StreamingActionKind.Upload, chunk));
                return UploadResult;
            }
        }
    }
}
