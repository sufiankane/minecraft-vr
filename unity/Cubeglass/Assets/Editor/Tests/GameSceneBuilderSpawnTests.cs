using Cubeglass.Voxel;
using NUnit.Framework;
using UnityEngine;

namespace Cubeglass.Editor.Tests
{
    /// <summary>
    /// Pins the game-scene spawn contract: the authored Unity spawn column is
    /// sampled for height at the internal column the Unity cell centre maps to
    /// (ADR-0004 mirrors Z), so a future seed or spawn change cannot silently
    /// regress to sampling a column the player never lands in.
    /// </summary>
    public sealed class GameSceneBuilderSpawnTests
    {
        [Test]
        public void AuthoredUnitySpawnColumnMirrorsToTheDeclaredSampleColumn()
        {
            Assert.AreEqual(8, GameSceneBuilder.SpawnColumnX, "the authored Unity spawn column is documented as (8, 8)");
            Assert.AreEqual(8, GameSceneBuilder.SpawnColumnZ, "the authored Unity spawn column is documented as (8, 8)");
            Assert.AreEqual(
                GameSceneBuilder.SpawnColumnX,
                GameSceneBuilder.SampleColumnX,
                "Unity X is not mirrored, so the sample column X equals the spawn column X");
            Assert.AreEqual(
                -9,
                GameSceneBuilder.SampleColumnZ,
                "the Unity cell centre (8.5) mirrors to internal -8.5, whose cell is -9");
            Assert.AreEqual(
                -GameSceneBuilder.SpawnColumnZ - 1,
                GameSceneBuilder.SampleColumnZ,
                "the sample column is the floor of the mirrored cell centre");
        }

        [Test]
        public void SpawnPositionSitsOnTheSurfaceSampledAtTheMirroredColumn()
        {
            const int unityColumnX = 8;
            const int unityColumnZ = 8;
            const long seed = 1L;
            Vector3 spawn = GameSceneBuilder.ComputeSpawnPosition(unityColumnX, unityColumnZ, seed, 2);

            Assert.AreEqual(8.5f, spawn.x, 1e-6f);
            Assert.AreEqual(8.5f, spawn.z, 1e-6f, "Unity Z is the mirror of the internal sample cell centre");
            Assert.AreEqual(unityColumnX, Mathf.FloorToInt(spawn.x), "the Unity spawn maps back to the authored X column");
            Assert.AreEqual(unityColumnZ, Mathf.FloorToInt(spawn.z), "the Unity spawn maps back to the authored Z column");

            int surfaceTop = TerrainGenerator.HeightAt(
                GameSceneBuilder.SampleColumnX, GameSceneBuilder.SampleColumnZ, seed) + 1;
            Assert.AreEqual(
                surfaceTop + 2,
                spawn.y,
                1e-6f,
                "the spawn height is sampled at the mirrored internal column, not the naive one");
        }
    }
}
