using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Cubeglass.CoreMath;
using Cubeglass.Voxel;
using NUnit.Framework;

namespace Cubeglass.Gameplay.Tests
{
    /// <summary>
    /// Replays the JSON interaction scenarios under plain <c>dotnet test</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A scenario fixes <c>dt</c>, a player setup, a block layout, a frame
    /// timeline (each frame has an explicit <c>time</c>; a frame stays in
    /// effect until the next one, exactly like
    /// <see cref="ScriptedInputProvider"/>), a list of hand-written
    /// accepted-command model edits, per-step observations and the expected
    /// final world hash.
    /// </para>
    /// <para>
    /// The runner replays the frames through <see cref="ScriptedInputProvider"/>
    /// and a fresh <see cref="InteractionService"/>, optionally stepping
    /// <see cref="PlayerController"/> first when <c>stepPhysics</c> is true.
    /// The observed service world must equal a second, model world built by
    /// applying every declared <c>modelEdits</c> command through
    /// <see cref="IWorld.Apply"/>, and both must equal the pinned
    /// <c>expected.worldHash</c>. JSON parsing (System.Text.Json) lives here in
    /// the test project; <c>Cubeglass.Gameplay</c> stays JSON-free (ADR-0008).
    /// </para>
    /// </remarks>
    internal static class ScenarioRunner
    {
        /// <summary>Runs the scenario file at <paramref name="path"/>.</summary>
        internal static void RunFile(string path)
        {
            ScenarioData scenario = Parse(path);
            Run(scenario);
        }

        /// <summary>The <c>scenarios</c> directory of this test project.</summary>
        /// <exception cref="AssertionException">The directory cannot be located.</exception>
        internal static string ScenariosDirectory()
        {
            string? directory = AppContext.BaseDirectory;
            for (int level = 0; level <= 10 && !string.IsNullOrEmpty(directory); level++)
            {
                string candidate = Path.Combine(directory, "scenarios");
                if (File.Exists(Path.Combine(candidate, "walk.json")))
                {
                    return candidate;
                }

                directory = Path.GetDirectoryName(directory);
            }

            Assert.Fail($"cannot find the Gameplay.Tests scenarios directory from '{AppContext.BaseDirectory}'");
            return string.Empty;
        }

        /// <summary>The scenario names (file names without extension), ordinal-sorted.</summary>
        internal static IReadOnlyList<string> ScenarioNames()
        {
            string[] files = Directory.GetFiles(ScenariosDirectory(), "*.json");
            var names = new string[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                names[i] = Path.GetFileNameWithoutExtension(files[i]);
            }

            Array.Sort(names, StringComparer.Ordinal);
            return names;
        }

        /// <summary>The path of the scenario with <paramref name="name"/>.</summary>
        internal static string ScenarioPath(string name)
        {
            return Path.Combine(ScenariosDirectory(), name + ".json");
        }

        /// <summary>
        /// A deterministic FNV-1a hash over the inclusive cell box
        /// <paramref name="min"/>..<paramref name="max"/>, mixing coordinates
        /// and block ids so a moved block changes the hash.
        /// </summary>
        internal static ulong Hash(IWorld world, Int3 min, Int3 max)
        {
            ArgumentNullException.ThrowIfNull(world);

            ulong hash = 14695981039346656037UL;
            for (int y = min.Y; y <= max.Y; y++)
            {
                for (int z = min.Z; z <= max.Z; z++)
                {
                    for (int x = min.X; x <= max.X; x++)
                    {
                        hash = Mix(hash, (ulong)(uint)x);
                        hash = Mix(hash, (ulong)(uint)y);
                        hash = Mix(hash, (ulong)(uint)z);
                        hash = Mix(hash, world.Get(new Int3(x, y, z)).Value);
                    }
                }
            }

            return hash;
        }

        private static ulong Mix(ulong hash, ulong value)
        {
            return (hash ^ value) * 1099511628211UL;
        }

        private static ScenarioData Parse(string path)
        {
            string context = Path.GetFileName(path);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;

            var scenario = new ScenarioData
            {
                Name = root.TryGetProperty("name", out JsonElement nameElement)
                    ? nameElement.GetString() ?? context
                    : context,
            };

            scenario.Dt = Require(root, "dt", context).GetDouble();
            if (!(scenario.Dt > 0.0) || double.IsInfinity(scenario.Dt))
            {
                Assert.Fail($"{context}: dt must be positive and finite");
            }

            scenario.StepPhysics = root.TryGetProperty("stepPhysics", out JsonElement physics)
                && physics.GetBoolean();

            JsonElement player = Require(root, "player", context);
            scenario.Player.Position = Vec3From(Require(player, "position", context), context);
            scenario.Player.Yaw = OptFloat(player, "yaw", 0f);
            scenario.Player.Pitch = OptFloat(player, "pitch", 0f);
            scenario.Player.HotbarIndex = OptInt(player, "hotbarIndex", 0);

            foreach (JsonElement block in Require(root, "blocks", context).EnumerateArray())
            {
                var blockId = new BlockId((ushort)Require(block, "block", context).GetInt32());
                if (block.TryGetProperty("cell", out JsonElement cell))
                {
                    scenario.Blocks.Add(new BlockSetup
                    {
                        From = Int3From(cell, context),
                        To = Int3From(cell, context),
                        Block = blockId,
                    });
                }
                else
                {
                    scenario.Blocks.Add(new BlockSetup
                    {
                        From = Int3From(Require(block, "from", context), context),
                        To = Int3From(Require(block, "to", context), context),
                        Block = blockId,
                    });
                }
            }

            JsonElement bounds = Require(root, "hashBounds", context);
            scenario.HashMin = Int3From(Require(bounds, "min", context), context);
            scenario.HashMax = Int3From(Require(bounds, "max", context), context);
            if (scenario.HashMin.X > scenario.HashMax.X
                || scenario.HashMin.Y > scenario.HashMax.Y
                || scenario.HashMin.Z > scenario.HashMax.Z)
            {
                Assert.Fail($"{context}: hashBounds min must not exceed max");
            }

            JsonElement frames = Require(root, "frames", context);
            if (frames.ValueKind != JsonValueKind.Array || frames.GetArrayLength() == 0)
            {
                Assert.Fail($"{context}: frames must be a non-empty array");
            }

            double previousTime = double.NegativeInfinity;
            foreach (JsonElement frame in frames.EnumerateArray())
            {
                double time = Require(frame, "time", context).GetDouble();
                if (double.IsNaN(time) || double.IsInfinity(time) || time <= previousTime)
                {
                    Assert.Fail($"{context}: frame times must be finite and strictly increasing");
                }

                previousTime = time;
                scenario.Frames.Add((time, FrameFrom(frame, context)));
            }

            scenario.Steps = root.TryGetProperty("steps", out JsonElement steps)
                ? steps.GetInt32()
                : scenario.Frames.Count;
            if (scenario.Steps <= 0)
            {
                Assert.Fail($"{context}: steps must be positive");
            }

            if (root.TryGetProperty("modelEdits", out JsonElement edits))
            {
                foreach (JsonElement edit in edits.EnumerateArray())
                {
                    scenario.ModelEdits.Add(new EditSetup
                    {
                        Cell = Int3From(Require(edit, "cell", context), context),
                        Expected = new BlockId((ushort)Require(edit, "expected", context).GetInt32()),
                        New = new BlockId((ushort)Require(edit, "new", context).GetInt32()),
                    });
                }
            }

            foreach (JsonElement observation in Require(root, "observations", context).EnumerateArray())
            {
                scenario.Observations.Add(ObservationFrom(observation, context, scenario.Steps));
            }

            JsonElement expected = Require(root, "expected", context);
            scenario.Expected.WorldHash = ParseHash(Require(expected, "worldHash", context).GetString(), context);
            scenario.Expected.Yaw = OptFloatOrNull(expected, "yaw");
            scenario.Expected.Pitch = OptFloatOrNull(expected, "pitch");
            scenario.Expected.HotbarIndex = OptIntOrNull(expected, "hotbarIndex");
            if (expected.TryGetProperty("position", out JsonElement position))
            {
                scenario.Expected.Position = Vec3From(position, context);
            }

            return scenario;
        }

        private static InputFrame FrameFrom(JsonElement frame, string context)
        {
            Vector2f move = default;
            if (frame.TryGetProperty("move", out JsonElement moveElement))
            {
                RequireArray(moveElement, 2, context + " move");
                move = new Vector2f((float)moveElement[0].GetDouble(), (float)moveElement[1].GetDouble());
            }

            PointerRay? pointer = null;
            if (frame.TryGetProperty("pointer", out JsonElement pointerElement)
                && pointerElement.ValueKind == JsonValueKind.Object)
            {
                pointer = new PointerRay(
                    Vec3From(Require(pointerElement, "origin", context), context),
                    Vec3From(Require(pointerElement, "direction", context), context));
            }

            TrackingQuality quality = frame.TryGetProperty("quality", out JsonElement qualityElement)
                ? ParseEnum<TrackingQuality>(qualityElement.GetString(), "quality", context)
                : TrackingQuality.Good;

            return new InputFrame(
                move,
                OptFloat(frame, "turnSnap", 0f),
                frame.TryGetProperty("recenter", out JsonElement recenter) && recenter.GetBoolean(),
                pointer,
                frame.TryGetProperty("primary", out JsonElement primary)
                    ? ParseEnum<ButtonState>(primary.GetString(), "primary", context)
                    : ButtonState.Up,
                frame.TryGetProperty("secondary", out JsonElement secondary)
                    ? ParseEnum<ButtonState>(secondary.GetString(), "secondary", context)
                    : ButtonState.Up,
                OptInt(frame, "hotbarDelta", 0),
                quality);
        }

        private static Observation ObservationFrom(JsonElement element, string context, int steps)
        {
            var observation = new Observation
            {
                Step = Require(element, "step", context).GetInt32(),
            };
            if (observation.Step < 0 || observation.Step >= steps)
            {
                Assert.Fail($"{context}: observation step {observation.Step} lies outside [0, {steps})");
            }

            observation.Edited = OptBool(element, "edited");
            observation.BreakInProgress = OptBool(element, "breakInProgress");
            observation.BreakProgress = OptDouble(element, "breakProgress");
            observation.ProgressTolerance = OptDouble(element, "progressTolerance") ?? 1e-6;
            if (element.TryGetProperty("target", out JsonElement target))
            {
                observation.HasTarget = true;
                observation.Target = target.ValueKind == JsonValueKind.Null
                    ? null
                    : Int3From(target, context + " target");
            }

            if (element.TryGetProperty("state", out JsonElement state))
            {
                observation.State = ParseEnum<InteractionState>(state.GetString(), "state", context);
            }

            observation.Recentered = OptBool(element, "recentered");
            observation.Yaw = OptDouble(element, "yaw");
            observation.HotbarIndex = OptIntOrNull(element, "hotbarIndex");
            if (element.TryGetProperty("position", out JsonElement position))
            {
                observation.Position = Vec3From(position, context + " position");
            }

            if (element.TryGetProperty("worldHash", out JsonElement hash))
            {
                observation.WorldHash = ParseHash(hash.GetString(), context);
            }

            return observation;
        }

        private static void Run(ScenarioData scenario)
        {
            World world = BuildWorld(scenario);
            World model = BuildWorld(scenario);
            foreach (EditSetup edit in scenario.ModelEdits)
            {
                EditResult result = model.Apply(new EditCommand(edit.Cell, edit.Expected, edit.New, 0));
                Assert.That(
                    result,
                    Is.EqualTo(EditResult.Applied),
                    $"{scenario.Name}: model edit at {edit.Cell} was rejected");
            }

            var player = new PlayerState
            {
                Position = scenario.Player.Position,
                YawRadians = scenario.Player.Yaw,
                PitchRadians = scenario.Player.Pitch,
                HotbarIndex = scenario.Player.HotbarIndex,
            };

            var provider = new ScriptedInputProvider(scenario.Frames);
            var service = new InteractionService(new DdaRaycaster(), BlockRegistry.Default);

            var observations = new Dictionary<int, Observation>();
            foreach (Observation observation in scenario.Observations)
            {
                if (!observations.TryAdd(observation.Step, observation))
                {
                    Assert.Fail($"{scenario.Name}: duplicate observation for step {observation.Step}");
                }
            }

            for (int step = 0; step < scenario.Steps; step++)
            {
                InputFrame frame = provider.Sample(step * scenario.Dt);
                if (scenario.StepPhysics)
                {
                    PlayerController.Step(player, in frame, world, scenario.Dt);
                }

                InteractionResult result = service.Update(in frame, world, player, scenario.Dt);
                if (observations.TryGetValue(step, out Observation? observation))
                {
                    AssertObservation(scenario, step, observation, result, service, player, world);
                }
            }

            ulong actual = Hash(world, scenario.HashMin, scenario.HashMax);
            ulong modelHash = Hash(model, scenario.HashMin, scenario.HashMax);
            Assert.That(
                actual,
                Is.EqualTo(modelHash),
                $"{scenario.Name}: the service world does not match the accepted-command model");
            Assert.That(actual, Is.EqualTo(scenario.Expected.WorldHash), $"{scenario.Name}: final world hash");

            if (scenario.Expected.Yaw.HasValue)
            {
                Assert.That(player.YawRadians, Is.EqualTo(scenario.Expected.Yaw.Value).Within(1e-9), $"{scenario.Name}: yaw");
            }

            if (scenario.Expected.Pitch.HasValue)
            {
                Assert.That(
                    player.PitchRadians,
                    Is.EqualTo(scenario.Expected.Pitch.Value).Within(1e-9),
                    $"{scenario.Name}: pitch");
            }

            if (scenario.Expected.HotbarIndex.HasValue)
            {
                Assert.That(
                    player.HotbarIndex,
                    Is.EqualTo(scenario.Expected.HotbarIndex.Value),
                    $"{scenario.Name}: hotbar index");
            }

            if (scenario.Expected.Position.HasValue)
            {
                Assert.That(
                    player.Position.NearlyEquals(scenario.Expected.Position.Value, 1e-9),
                    Is.True,
                    $"{scenario.Name}: final player position {player.Position}");
            }
        }

        private static void AssertObservation(
            ScenarioData scenario,
            int step,
            Observation observation,
            InteractionResult result,
            InteractionService service,
            PlayerState player,
            IWorld world)
        {
            string context = $"{scenario.Name} step {step}";
            if (observation.Edited.HasValue)
            {
                Assert.That(result.Edited, Is.EqualTo(observation.Edited.Value), $"{context} Edited");
            }

            if (observation.BreakInProgress.HasValue)
            {
                Assert.That(
                    result.BreakInProgress,
                    Is.EqualTo(observation.BreakInProgress.Value),
                    $"{context} BreakInProgress");
            }

            if (observation.BreakProgress.HasValue)
            {
                Assert.That(
                    (double)result.BreakProgress,
                    Is.EqualTo(observation.BreakProgress.Value).Within(observation.ProgressTolerance),
                    $"{context} BreakProgress");
            }

            if (observation.HasTarget)
            {
                if (observation.Target.HasValue)
                {
                    Assert.That(
                        result.Target.GetValueOrDefault(),
                        Is.EqualTo(observation.Target.Value),
                        $"{context} Target");
                }
                else
                {
                    Assert.That(result.Target.HasValue, Is.False, $"{context} Target must be null");
                }
            }

            if (observation.State.HasValue)
            {
                Assert.That(service.State, Is.EqualTo(observation.State.Value), $"{context} State");
            }

            if (observation.Recentered.HasValue)
            {
                Assert.That(service.Recentered, Is.EqualTo(observation.Recentered.Value), $"{context} Recentered");
            }

            if (observation.Yaw.HasValue)
            {
                Assert.That(
                    (double)player.YawRadians,
                    Is.EqualTo(observation.Yaw.Value).Within(1e-9),
                    $"{context} yaw");
            }

            if (observation.HotbarIndex.HasValue)
            {
                Assert.That(player.HotbarIndex, Is.EqualTo(observation.HotbarIndex.Value), $"{context} hotbar index");
            }

            if (observation.Position.HasValue)
            {
                Assert.That(
                    player.Position.NearlyEquals(observation.Position.Value, 1e-9),
                    Is.True,
                    $"{context} player position {player.Position}");
            }

            if (observation.WorldHash.HasValue)
            {
                Assert.That(
                    Hash(world, scenario.HashMin, scenario.HashMax),
                    Is.EqualTo(observation.WorldHash.Value),
                    $"{context} world hash");
            }
        }

        private static World BuildWorld(ScenarioData scenario)
        {
            var world = new World();
            world.LoadChunk(new Chunk(new ChunkCoord(0, 0, 0)));
            foreach (BlockSetup block in scenario.Blocks)
            {
                for (int y = block.From.Y; y <= block.To.Y; y++)
                {
                    for (int z = block.From.Z; z <= block.To.Z; z++)
                    {
                        for (int x = block.From.X; x <= block.To.X; x++)
                        {
                            Int3 cell = new Int3(x, y, z);
                            Assert.That(
                                world.Apply(new EditCommand(cell, BlockId.Air, block.Block, 0)),
                                Is.EqualTo(EditResult.Applied),
                                $"{scenario.Name}: setup block at {cell} was rejected");
                        }
                    }
                }
            }

            return world;
        }

        private static JsonElement Require(JsonElement parent, string name, string context)
        {
            if (!parent.TryGetProperty(name, out JsonElement value))
            {
                Assert.Fail($"{context}: missing property '{name}'");
            }

            return value;
        }

        private static void RequireArray(JsonElement value, int length, string context)
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != length)
            {
                Assert.Fail($"{context}: expected an array of {length} numbers");
            }
        }

        private static Int3 Int3From(JsonElement array, string context)
        {
            RequireArray(array, 3, context);
            return new Int3(array[0].GetInt32(), array[1].GetInt32(), array[2].GetInt32());
        }

        private static Vec3 Vec3From(JsonElement array, string context)
        {
            RequireArray(array, 3, context);
            return new Vec3(array[0].GetDouble(), array[1].GetDouble(), array[2].GetDouble());
        }

        private static bool? OptBool(JsonElement parent, string name)
        {
            return parent.TryGetProperty(name, out JsonElement value) ? value.GetBoolean() : null;
        }

        private static double? OptDouble(JsonElement parent, string name)
        {
            return parent.TryGetProperty(name, out JsonElement value) ? value.GetDouble() : null;
        }

        private static float OptFloat(JsonElement parent, string name, float fallback)
        {
            return parent.TryGetProperty(name, out JsonElement value) ? (float)value.GetDouble() : fallback;
        }

        private static float? OptFloatOrNull(JsonElement parent, string name)
        {
            return parent.TryGetProperty(name, out JsonElement value) ? (float)value.GetDouble() : null;
        }

        private static int OptInt(JsonElement parent, string name, int fallback)
        {
            return parent.TryGetProperty(name, out JsonElement value) ? value.GetInt32() : fallback;
        }

        private static int? OptIntOrNull(JsonElement parent, string name)
        {
            return parent.TryGetProperty(name, out JsonElement value) ? value.GetInt32() : null;
        }

        private static T ParseEnum<T>(string? value, string property, string context)
            where T : struct, Enum
        {
            T parsed = default;
            if (value is null
                || !Enum.TryParse(value, ignoreCase: true, out parsed)
                || !Enum.IsDefined(parsed))
            {
                Assert.Fail($"{context}: '{value}' is not a valid {typeof(T).Name} for '{property}'");
            }

            return parsed;
        }

        private static ulong ParseHash(string? value, string context)
        {
            string text = value ?? string.Empty;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(2);
            }

            if (!ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
            {
                Assert.Fail($"{context}: '{value}' is not a hexadecimal world hash");
            }

            return hash;
        }

        private sealed class ScenarioData
        {
            internal string Name = string.Empty;
            internal double Dt;
            internal int Steps;
            internal bool StepPhysics;
            internal PlayerSetup Player = new PlayerSetup();
            internal List<BlockSetup> Blocks = new List<BlockSetup>();
            internal Int3 HashMin;
            internal Int3 HashMax;
            internal List<(double time, InputFrame frame)> Frames = new List<(double, InputFrame)>();
            internal List<EditSetup> ModelEdits = new List<EditSetup>();
            internal List<Observation> Observations = new List<Observation>();
            internal ExpectedSetup Expected = new ExpectedSetup();
        }

        private sealed class PlayerSetup
        {
            internal Vec3 Position;
            internal float Yaw;
            internal float Pitch;
            internal int HotbarIndex;
        }

        private sealed class BlockSetup
        {
            internal Int3 From;
            internal Int3 To;
            internal BlockId Block;
        }

        private sealed class EditSetup
        {
            internal Int3 Cell;
            internal BlockId Expected;
            internal BlockId New;
        }

        private sealed class Observation
        {
            internal int Step;
            internal bool? Edited;
            internal bool? BreakInProgress;
            internal double? BreakProgress;
            internal double ProgressTolerance = 1e-6;
            internal bool HasTarget;
            internal Int3? Target;
            internal InteractionState? State;
            internal bool? Recentered;
            internal double? Yaw;
            internal int? HotbarIndex;
            internal Vec3? Position;
            internal ulong? WorldHash;
        }

        private sealed class ExpectedSetup
        {
            internal ulong WorldHash;
            internal float? Yaw;
            internal float? Pitch;
            internal int? HotbarIndex;
            internal Vec3? Position;
        }
    }
}
