using System;
using System.Collections.Generic;
using Cubeglass.Voxel;

namespace Cubeglass.Unity.Rendering
{
    /// <summary>
    /// Code-built <see cref="IBlockRegistry"/> for the six S7 slice blocks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This mirrors <c>dotnet/src/Voxel/Content/blocks.json</c>, the data-driven
    /// source of truth parsed by <see cref="BlockRegistry"/>. That parser needs
    /// <c>System.Text.Json</c>, which is not part of Unity's runtime and is
    /// deliberately not shipped as a managed plugin, and Unity's runtime never
    /// meshes through the pure module's registry; the S7 slice only needs the
    /// built-in terrain blocks. The EditMode test
    /// <c>SliceBlockRegistryMatchesTheCommittedBlocksJson</c> pins every value
    /// against the JSON; change both together.
    /// </para>
    /// <para>
    /// <see cref="Get"/> is total, matching the pure module (dotnet review I1
    /// and its Unity mirror): an id with no definition returns
    /// <see cref="Fallback"/> (air-equivalent: non-solid, non-opaque, hardness
    /// 0, atlas tile 0) instead of throwing, so a corrupted save or a future
    /// block id can never stop a frame in the mesher or the interaction
    /// service. The fallback is still meshed as a visible placeholder diamond
    /// because the mesher keys "air vs block" on the stored id, not on the
    /// resolved definition.
    /// </para>
    /// <para>
    /// <see cref="Get"/> scans a fixed array (six entries), so a chunk build
    /// performs no allocation and no dictionary lookup.
    /// </para>
    /// </remarks>
    public sealed class SliceBlockRegistry : IBlockRegistry
    {
        /// <summary>
        /// The definition returned for an id with no entry: an air-equivalent
        /// placeholder with atlas tile 0 so the mesher can still draw it.
        /// </summary>
        public static readonly BlockDefinition Fallback =
            new BlockDefinition(BlockId.Air, "Unknown", false, false, 0.0f, 0, 0, 0);

        private static readonly BlockDefinition[] Definitions =
        {
            new BlockDefinition(new BlockId(0), "Air", false, false, 0.0f, 0, 0, 0),
            new BlockDefinition(new BlockId(1), "Stone", true, true, 1.5f, 1, 1, 1),
            new BlockDefinition(new BlockId(2), "Dirt", true, true, 0.5f, 2, 2, 2),
            new BlockDefinition(new BlockId(3), "Grass", true, true, 0.6f, 3, 4, 4),
            new BlockDefinition(new BlockId(4), "Sand", true, true, 0.5f, 5, 5, 5),
            new BlockDefinition(new BlockId(5), "Wood", true, true, 2.0f, 7, 6, 6),
        };

        private static readonly BlockId[] PlaceableIds =
        {
            new BlockId(1),
            new BlockId(2),
            new BlockId(3),
            new BlockId(4),
            new BlockId(5),
        };

        /// <summary>The shared registry instance.</summary>
        public static SliceBlockRegistry Default { get; } = new SliceBlockRegistry();

        /// <summary>
        /// Returns the definition of <paramref name="id"/>, or
        /// <see cref="Fallback"/> when no built-in block has that id. Never
        /// throws for an id.
        /// </summary>
        public BlockDefinition Get(BlockId id)
        {
            for (int i = 0; i < Definitions.Length; i++)
            {
                if (Definitions[i].Id == id)
                {
                    return Definitions[i];
                }
            }

            return Fallback;
        }

        /// <summary>Every placeable block id in definition order; excludes air.</summary>
        public IReadOnlyList<BlockId> Placeable
        {
            get { return PlaceableIds; }
        }
    }
}
