using System;
using System.Collections.Generic;
using Cubeglass.CoreMath;
using Cubeglass.Voxel;
using FsCheck;
using FsCheck.Fluent;
using Microsoft.FSharp.Core;
using NUnit.Framework;

namespace Cubeglass.Streaming.Tests
{
    [TestFixture]
    public sealed class SchedulerPropertyTests
    {
        private const ulong Seed = 20261002UL;
        private const int MaxPathSteps = 40;
        private const int DrainGuard = 2000;

        [Test]
        public void RandomPathsDrainToTheDesiredSetWithinBudgets()
        {
            Arbitrary<int[]> scripts = Arb.Array(Arb.From(Gen.Choose(-64, 64)));
            Property property = Prop.ForAll(
                scripts,
                (int[] script) =>
                {
                    RunPathProperty(script, hysteresis: 0, requireExactDesired: true);
                    return Prop.ToProperty(true);
                });

            Check.One("random paths drain to the desired set", DeterministicConfig(150), property);
        }

        [Test]
        public void RandomPathsWithHysteresisStayBetweenDesiredAndRetention()
        {
            Arbitrary<int[]> scripts = Arb.Array(Arb.From(Gen.Choose(-64, 64)));
            Property property = Prop.ForAll(
                scripts,
                (int[] script) =>
                {
                    RunPathProperty(script, hysteresis: 2, requireExactDesired: false);
                    return Prop.ToProperty(true);
                });

            Check.One("random paths respect the retention window", DeterministicConfig(100), property);
        }

        private static void RunPathProperty(int[] script, int hysteresis, bool requireExactDesired)
        {
            if (script.Length < 4)
            {
                return;
            }

            var config = new StreamingConfig
            {
                ViewDistanceChunks = 2,
                UnloadHysteresis = hysteresis,
                MaxLoadsPerFrame = 3,
                MaxUnloadsPerFrame = 2,
                MaxMeshUploadsPerFrame = 2,
                VerticalRadiusChunks = 1,
            };
            var scheduler = new ChunkStreamingScheduler(config, 4242L);
            int x = 0;
            int z = 0;

            for (int step = 0; step < MaxPathSteps; step++)
            {
                x += script[(step * 2) % script.Length] % 3;
                z += script[((step * 2) + 1) % script.Length] % 3;
                Vec3 position = Center(x, 0, z);

                Drain(scheduler, config, position);
                AssertResident(scheduler, config, x, 0, z, hysteresis, requireExactDesired);
            }
        }

        private static void Drain(ChunkStreamingScheduler scheduler, StreamingConfig config, Vec3 position)
        {
            for (int guard = 0; guard < DrainGuard; guard++)
            {
                IReadOnlyList<StreamingAction> actions = scheduler.Update(position);
                int loads = 0;
                int unloads = 0;
                int uploads = 0;

                for (int i = 0; i < actions.Count; i++)
                {
                    StreamingAction action = actions[i];
                    switch (action.Kind)
                    {
                        case StreamingActionKind.Load:
                            loads++;
                            scheduler.NotifyMeshReady(action.Chunk);
                            break;
                        case StreamingActionKind.Unload:
                            unloads++;
                            break;
                        default:
                            uploads++;
                            break;
                    }
                }

                if (loads > config.MaxLoadsPerFrame)
                {
                    throw new InvalidOperationException($"frame emitted {loads} loads, budget {config.MaxLoadsPerFrame}");
                }

                if (unloads > config.MaxUnloadsPerFrame)
                {
                    throw new InvalidOperationException($"frame emitted {unloads} unloads, budget {config.MaxUnloadsPerFrame}");
                }

                if (uploads > config.MaxMeshUploadsPerFrame)
                {
                    throw new InvalidOperationException($"frame emitted {uploads} uploads, budget {config.MaxMeshUploadsPerFrame}");
                }

                if (actions.Count == 0)
                {
                    return;
                }
            }

            throw new InvalidOperationException("the scheduler did not drain within the guard");
        }

        private static void AssertResident(
            ChunkStreamingScheduler scheduler,
            StreamingConfig config,
            int cx,
            int cy,
            int cz,
            int hysteresis,
            bool requireExactDesired)
        {
            int radius = config.ViewDistanceChunks;
            int vertical = (int)Math.Floor(config.VerticalRadiusChunks);
            int desiredCount = 0;

            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -vertical; dy <= vertical; dy++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        desiredCount++;
                        var chunk = new ChunkCoord(cx + dx, cy + dy, cz + dz);
                        if (!scheduler.IsLoaded(chunk))
                        {
                            throw new InvalidOperationException($"desired chunk {chunk} is not loaded");
                        }
                    }
                }
            }

            int retainedCount = 0;
            int horizontal = radius + hysteresis;
            int verticalLimit = vertical + hysteresis;

            for (int dx = -horizontal; dx <= horizontal; dx++)
            {
                for (int dy = -verticalLimit; dy <= verticalLimit; dy++)
                {
                    for (int dz = -horizontal; dz <= horizontal; dz++)
                    {
                        if (scheduler.IsLoaded(new ChunkCoord(cx + dx, cy + dy, cz + dz)))
                        {
                            retainedCount++;
                        }
                    }
                }
            }

            if (retainedCount != scheduler.LoadedCount)
            {
                throw new InvalidOperationException(
                    $"a loaded chunk lies outside the retention window ({retainedCount} of {scheduler.LoadedCount})");
            }

            if (requireExactDesired && scheduler.LoadedCount != desiredCount)
            {
                throw new InvalidOperationException(
                    $"loaded {scheduler.LoadedCount} chunks but the desired set has {desiredCount}");
            }
        }

        private static Vec3 Center(int x, int y, int z)
        {
            return new Vec3(
                (x * ChunkMath.ChunkSize) + 8.0,
                (y * ChunkMath.ChunkSize) + 8.0,
                (z * ChunkMath.ChunkSize) + 8.0);
        }

        private static Config DeterministicConfig(int maxTests)
        {
            Replay replay = new Replay(new Rnd(Seed), FSharpOption<int>.None);
            return Config.QuickThrowOnFailure
                .WithMaxTest(maxTests)
                .WithReplay(FSharpOption<Replay>.Some(replay));
        }
    }
}
