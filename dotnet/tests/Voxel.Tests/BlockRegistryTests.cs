using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Cubeglass.Voxel.Tests
{
    [TestFixture]
    public sealed class BlockRegistryTests
    {
        private const string AirBlock =
            "{\"Id\":0,\"Name\":\"Air\",\"Solid\":false,\"Opaque\":false,\"Hardness\":0,\"AtlasIndexTop\":0,\"AtlasIndexFront\":0,\"AtlasIndexSide\":0}";

        private const string StoneBlock =
            "{\"Id\":1,\"Name\":\"Stone\",\"Solid\":true,\"Opaque\":true,\"Hardness\":1.5,\"AtlasIndexTop\":1,\"AtlasIndexFront\":1,\"AtlasIndexSide\":1}";

        [Test]
        public void EmbeddedRegistryLoadsAtLeastSixBlocksWithAirAtIdZero()
        {
            BlockRegistry registry = BlockRegistry.Default;

            Assert.That(registry.Count, Is.GreaterThanOrEqualTo(6));
            Assert.That(registry.Get(BlockId.Air).Id, Is.EqualTo(BlockId.Air));
            Assert.That(registry.Get(BlockId.Air).Name, Is.EqualTo("Air"));
            Assert.That(registry.Get(BlockId.Air).Solid, Is.False);
            Assert.That(registry.Get(BlockId.Air).Opaque, Is.False);
        }

        [Test]
        public void EmbeddedRegistryDefinesGrassAsSolidAndOpaque()
        {
            BlockDefinition? grass = FindByName(BlockRegistry.Default, "Grass");

            Assert.That(grass, Is.Not.Null);
            Assert.That(grass!.Solid, Is.True);
            Assert.That(grass.Opaque, Is.True);
        }

        [Test]
        public void PlaceableExcludesAir()
        {
            BlockRegistry registry = BlockRegistry.Default;

            Assert.That(registry.Placeable, Does.Not.Contain(BlockId.Air));
            Assert.That(registry.Placeable.Count, Is.EqualTo(registry.Count - 1));
        }

        [Test]
        public void ParseAcceptsAValidFixtureAndKeepsTheDeclaredValues()
        {
            BlockRegistry registry = BlockRegistry.Parse("[" + AirBlock + "," + StoneBlock + "]");

            Assert.That(registry.Count, Is.EqualTo(2));
            Assert.That(registry.Placeable, Is.EqualTo(new[] { new BlockId(1) }));

            BlockDefinition stone = registry.Get(new BlockId(1));
            Assert.That(stone.Name, Is.EqualTo("Stone"));
            Assert.That(stone.Solid, Is.True);
            Assert.That(stone.Opaque, Is.True);
            Assert.That(stone.Hardness, Is.EqualTo(1.5f));
            Assert.That(stone.AtlasIndexTop, Is.EqualTo(1));
            Assert.That(stone.AtlasIndexFront, Is.EqualTo(1));
            Assert.That(stone.AtlasIndexSide, Is.EqualTo(1));
        }

        [Test]
        public void ParseRejectsMalformedJson()
        {
            Assert.Throws<FormatException>(() => BlockRegistry.Parse("[{\"Id\":"));
        }

        [Test]
        public void ParseRejectsANonArrayRoot()
        {
            Assert.Throws<FormatException>(() => BlockRegistry.Parse("{\"Blocks\":[]}"));
        }

        [Test]
        public void ParseRejectsDuplicateIds()
        {
            Assert.Throws<FormatException>(() => BlockRegistry.Parse("[" + StoneBlock + "," + StoneBlock + "]"));
        }

        [Test]
        public void ParseRejectsAMissingAirBlock()
        {
            Assert.Throws<FormatException>(() => BlockRegistry.Parse("[" + StoneBlock + "]"));
        }

        [Test]
        public void ParseRejectsANegativeAtlasIndex()
        {
            const string NegativeAtlas =
                "{\"Id\":0,\"Name\":\"Air\",\"Solid\":false,\"Opaque\":false,\"Hardness\":0,\"AtlasIndexTop\":0,\"AtlasIndexFront\":0,\"AtlasIndexSide\":-1}";

            Assert.Throws<FormatException>(() => BlockRegistry.Parse("[" + NegativeAtlas + "]"));
        }

        [Test]
        public void ParseRejectsNegativeHardness()
        {
            const string NegativeHardness =
                "{\"Id\":0,\"Name\":\"Air\",\"Solid\":false,\"Opaque\":false,\"Hardness\":-0.5,\"AtlasIndexTop\":0,\"AtlasIndexFront\":0,\"AtlasIndexSide\":0}";

            Assert.Throws<FormatException>(() => BlockRegistry.Parse("[" + NegativeHardness + "]"));
        }

        [Test]
        public void ParseRejectsAMissingName()
        {
            const string MissingName =
                "{\"Id\":0,\"Solid\":false,\"Opaque\":false,\"Hardness\":0,\"AtlasIndexTop\":0,\"AtlasIndexFront\":0,\"AtlasIndexSide\":0}";

            Assert.Throws<FormatException>(() => BlockRegistry.Parse("[" + MissingName + "]"));
        }

        [Test]
        public void ParseRejectsAnEmptyName()
        {
            const string EmptyName =
                "{\"Id\":0,\"Name\":\"\",\"Solid\":false,\"Opaque\":false,\"Hardness\":0,\"AtlasIndexTop\":0,\"AtlasIndexFront\":0,\"AtlasIndexSide\":0}";

            Assert.Throws<FormatException>(() => BlockRegistry.Parse("[" + EmptyName + "]"));
        }

        [Test]
        public void ParseRejectsTheReservedSentinelId()
        {
            const string ReservedId =
                "{\"Id\":65535,\"Name\":\"Reserved\",\"Solid\":true,\"Opaque\":true,\"Hardness\":1,\"AtlasIndexTop\":1,\"AtlasIndexFront\":1,\"AtlasIndexSide\":1}";

            Assert.Throws<FormatException>(() => BlockRegistry.Parse("[" + ReservedId + "]"));
        }

        [Test]
        public void ParseAcceptsTheHighestNonReservedId()
        {
            const string HighestId =
                "{\"Id\":65534,\"Name\":\"Highest\",\"Solid\":true,\"Opaque\":true,\"Hardness\":1,\"AtlasIndexTop\":1,\"AtlasIndexFront\":1,\"AtlasIndexSide\":1}";

            BlockRegistry registry = BlockRegistry.Parse("[" + AirBlock + "," + HighestId + "]");

            Assert.That(registry.Get(new BlockId(65534)).Name, Is.EqualTo("Highest"));
        }

        [Test]
        public void GetThrowsForAnUnknownBlockId()
        {
            BlockRegistry registry = BlockRegistry.Parse("[" + AirBlock + "]");

            Assert.Throws<KeyNotFoundException>(() => registry.Get(new BlockId(9)));
        }

        private static BlockDefinition? FindByName(BlockRegistry registry, string name)
        {
            for (int id = 0; id < registry.Count; id++)
            {
                BlockDefinition definition = registry.Get(new BlockId((ushort)id));
                if (definition.Name == name)
                {
                    return definition;
                }
            }

            return null;
        }
    }
}
