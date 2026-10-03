using Cubeglass.Voxel;
using NUnit.Framework;
using UnityEngine;

namespace Cubeglass.Editor.Tests
{
    /// <summary>
    /// Pins the game-scene spawn contract: the authored Unity spawn column is
    /// sampled for height at the internal column the Unity cell centre maps to
    /// (ADR-0004 mirrors Z). The seed-1 columns happen to coincide, so the
    /// discriminating case uses seed 2, where the naive <c>(8, 8)</c> and the
    /// mirrored <c>(8, -9)</c> columns have different heights; a regression to
    /// sampling the naive column fails that case.
    /// </summary>
    public sealed class GameSceneBuilderSpawnTests
    {
        [Test]
        public void InternalColumnForUnityMirrorsThenFloorsEverySign()
        {
            int[] columns = { 0, 1, 7, 8, 9, 16, -1, -9, -16 };
            foreach (int column in columns)
            {
                Assert.AreEqual(
                    -column - 1,
                    GameSceneBuilder.InternalColumnForUnity(column),
                    "the cell containing the mirrored centre of column " + column);
            }

            Assert.AreEqual(-1, GameSceneBuilder.InternalColumnForUnity(0), "Unity cell 0 centre 0.5 mirrors to -0.5, cell -1");
            Assert.AreEqual(8, GameSceneBuilder.InternalColumnForUnity(-9), "the mapping is its own inverse");
        }

        [Test]
        public void SampleColumnForUnitySpawnMatchesTheDeclaredSampleConstants()
        {
            GameSceneBuilder.SampleColumnForUnitySpawn(
                GameSceneBuilder.SpawnColumnX, GameSceneBuilder.SpawnColumnZ,
                out int sampleX, out int sampleZ);

            Assert.AreEqual(GameSceneBuilder.SampleColumnX, sampleX, "X is not mirrored");
            Assert.AreEqual(GameSceneBuilder.SampleColumnZ, sampleZ, "Z is the declared mirrored sample column");
            Assert.AreEqual(8, sampleX, "the authored Unity spawn column is documented as (8, 8)");
            Assert.AreEqual(-9, sampleZ, "the Unity cell centre (8.5) mirrors to internal -8.5, whose cell is -9");

            GameSceneBuilder.SampleColumnForUnitySpawn(-9, -9, out int mirroredX, out int mirroredZ);
            Assert.AreEqual(-9, mirroredX, "negative X columns are not mirrored");
            Assert.AreEqual(8, mirroredZ, "the internal sample column mirrors back to the authored Unity column");
        }

        [Test]
        public void ComputeSpawnPositionSamplesTheMirroredColumnEvenWhenTheNaiveColumnDiffers()
        {
            const int unityColumnX = 8;
            const int unityColumnZ = 8;
            const long seed = 2L;
            const int heightAboveSurface = 2;

            int mirroredTop = TerrainGenerator.HeightAt(8, -9, seed) + 1;
            int naiveTop = TerrainGenerator.HeightAt(8, 8, seed) + 1;
            Assert.AreNotEqual(
                naiveTop, mirroredTop,
                "seed 2 must discriminate the mirrored (8, -9) from the naive (8, 8) column");

            Vector3 spawn = GameSceneBuilder.ComputeSpawnPosition(
                unityColumnX, unityColumnZ, seed, heightAboveSurface);

            Assert.AreEqual(
                mirroredTop + heightAboveSurface,
                spawn.y,
                1e-6f,
                "the spawn height must come from the mirrored internal column");
            Assert.That(
                spawn.y,
                Is.Not.EqualTo((float)(naiveTop + heightAboveSurface)).Within(1e-4f),
                "a regression to the naive column would produce this height");
            Assert.AreEqual(8.5f, spawn.x, 1e-6f);
            Assert.AreEqual(8.5f, spawn.z, 1e-6f, "Unity Z is the mirror of the internal sample cell centre");
            Assert.AreEqual(unityColumnX, Mathf.FloorToInt(spawn.x), "the Unity spawn maps back to the authored X column");
            Assert.AreEqual(unityColumnZ, Mathf.FloorToInt(spawn.z), "the Unity spawn maps back to the authored Z column");
        }

        [Test]
        public void DefaultSpawnUsesTheBuilderSeedAndSampleColumn()
        {
            GameSceneBuilder.SampleColumnForUnitySpawn(
                GameSceneBuilder.SpawnColumnX, GameSceneBuilder.SpawnColumnZ,
                out int sampleX, out int sampleZ);
            int sampledTop = TerrainGenerator.HeightAt(
                sampleX, sampleZ, GameSceneBuilder.DefaultWorldSeed) + 1;

            Assert.AreEqual(
                sampledTop,
                GameSceneBuilder.DefaultSurfaceTop(),
                "the committed starter ground samples the default seed at the mirrored column");
            Assert.AreEqual(
                DefaultSampleTop(),
                sampledTop,
                "the declared sample constants describe the column the builder samples");

            Vector3 spawn = GameSceneBuilder.DefaultSpawnPosition();
            Assert.AreEqual(
                sampledTop + GameSceneBuilder.SpawnHeightAboveSurface,
                spawn.y,
                1e-6f,
                "the committed spawn uses DefaultWorldSeed at the mirrored column");
            Assert.AreEqual(GameSceneBuilder.SpawnColumnX + 0.5f, spawn.x, 1e-6f);
            Assert.AreEqual(GameSceneBuilder.SpawnColumnZ + 0.5f, spawn.z, 1e-6f);
        }

        private static int DefaultSampleTop()
        {
            return TerrainGenerator.HeightAt(
                GameSceneBuilder.SampleColumnX, GameSceneBuilder.SampleColumnZ,
                GameSceneBuilder.DefaultWorldSeed) + 1;
        }
    }
}
