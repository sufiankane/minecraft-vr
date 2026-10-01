using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Cubeglass.Voxel
{
    /// <summary>
    /// Data-driven <see cref="IBlockRegistry"/> backed by an embedded
    /// <c>Content/blocks.json</c> resource (ADR-0005).
    /// </summary>
    /// <remarks>
    /// Parsing happens once per instance. <see cref="Parse"/> is the pure test
    /// seam; <see cref="Default"/> loads the embedded resource through
    /// <c>Assembly.GetManifestResourceStream</c>, never <c>System.IO</c> file
    /// APIs. Invalid content throws <see cref="FormatException"/> so the pure
    /// module never references <c>System.IO.InvalidDataException</c>.
    /// </remarks>
    public sealed class BlockRegistry : IBlockRegistry
    {
        private const string ResourceName = "Cubeglass.Voxel.Content.blocks.json";

        private static readonly Lazy<BlockRegistry> Embedded =
            new Lazy<BlockRegistry>(LoadEmbedded);

        private readonly Dictionary<BlockId, BlockDefinition> _byId;
        private readonly BlockId[] _placeable;

        private BlockRegistry(Dictionary<BlockId, BlockDefinition> byId, BlockId[] placeable)
        {
            _byId = byId;
            _placeable = placeable;
        }

        /// <summary>The registry parsed from the embedded block definitions.</summary>
        public static BlockRegistry Default => Embedded.Value;

        /// <summary>The number of defined blocks, including air.</summary>
        public int Count => _byId.Count;

        public IReadOnlyList<BlockId> Placeable => _placeable;

        public BlockDefinition Get(BlockId id)
        {
            if (_byId.TryGetValue(id, out BlockDefinition? definition))
            {
                return definition;
            }

            throw new KeyNotFoundException(
                string.Format(CultureInfo.InvariantCulture, "No block definition for id {0}.", id.Value));
        }

        /// <summary>
        /// Parses block definitions from JSON text.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="json"/> is null.
        /// </exception>
        /// <exception cref="FormatException">
        /// The text is not valid JSON, is not an array, contains a duplicate id,
        /// omits air (id 0), defines the reserved id 65535 (0xFFFF), omits a
        /// name or declares an empty one, or contains a negative atlas index or
        /// hardness.
        /// </exception>
        public static BlockRegistry Parse(string json)
        {
            if (json is null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            try
            {
                return Create(JsonSerializer.Deserialize<List<BlockDto?>>(json));
            }
            catch (JsonException exception)
            {
                throw new FormatException("Block definitions are not valid JSON.", exception);
            }
        }

        private static BlockRegistry LoadEmbedded()
        {
            var stream = typeof(BlockRegistry).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.InvariantCulture, "Embedded resource '{0}' was not found.", ResourceName));
            }

            using (stream)
            {
                try
                {
                    return Create(JsonSerializer.Deserialize<List<BlockDto?>>(stream));
                }
                catch (JsonException exception)
                {
                    throw new FormatException("The embedded block definitions are not valid JSON.", exception);
                }
            }
        }

        private static BlockRegistry Create(List<BlockDto?>? dtos)
        {
            if (dtos is null)
            {
                throw new FormatException("Block definitions must be a JSON array.");
            }

            var byId = new Dictionary<BlockId, BlockDefinition>(dtos.Count);
            var placeable = new List<BlockId>(dtos.Count);

            foreach (BlockDto? dto in dtos)
            {
                if (dto is null)
                {
                    throw new FormatException("Block definitions must not contain null entries.");
                }

                var id = new BlockId(dto.Id);
                if (byId.ContainsKey(id))
                {
                    throw new FormatException(
                        string.Format(CultureInfo.InvariantCulture, "Duplicate block id {0}.", dto.Id));
                }

                if (dto.Id == ushort.MaxValue)
                {
                    throw new FormatException(
                        "Block id 65535 (0xFFFF) is reserved by ADR-0006 and must not be defined.");
                }

                if (string.IsNullOrEmpty(dto.Name))
                {
                    throw new FormatException(
                        string.Format(CultureInfo.InvariantCulture, "Block {0} must declare a non-empty name.", dto.Id));
                }

                if (dto.AtlasIndexTop < 0 || dto.AtlasIndexFront < 0 || dto.AtlasIndexSide < 0)
                {
                    throw new FormatException(
                        string.Format(CultureInfo.InvariantCulture, "Block {0} has a negative atlas index.", dto.Id));
                }

                if (!(dto.Hardness >= 0f))
                {
                    throw new FormatException(
                        string.Format(CultureInfo.InvariantCulture, "Block {0} has a negative hardness.", dto.Id));
                }

                byId.Add(
                    id,
                    new BlockDefinition(
                        id,
                        dto.Name ?? string.Empty,
                        dto.Solid,
                        dto.Opaque,
                        dto.Hardness,
                        dto.AtlasIndexTop,
                        dto.AtlasIndexFront,
                        dto.AtlasIndexSide));

                if (id != BlockId.Air)
                {
                    placeable.Add(id);
                }
            }

            if (!byId.ContainsKey(BlockId.Air))
            {
                throw new FormatException("Air (id 0) must be present in the block definitions.");
            }

            return new BlockRegistry(byId, placeable.ToArray());
        }

        private sealed class BlockDto
        {
            public ushort Id { get; set; }

            public string? Name { get; set; }

            public bool Solid { get; set; }

            public bool Opaque { get; set; }

            public float Hardness { get; set; }

            public int AtlasIndexTop { get; set; }

            public int AtlasIndexFront { get; set; }

            public int AtlasIndexSide { get; set; }
        }
    }
}
