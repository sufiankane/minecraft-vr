using System;
using Cubeglass.CoreMath;

namespace Cubeglass.Mesh
{
    /// <summary>
    /// Pure mapping from a block's atlas tile index to UV coordinates
    /// (ADR-0007).
    /// </summary>
    /// <remarks>
    /// S3 uses no padding: a tile occupies exactly
    /// <c>[TileMin, TileMin + TileUvSize]</c> on both axes. Tile 0 is the
    /// top-left tile; indices advance left to right, then top to bottom
    /// (ADR-0004 pixel origin is top-left).
    /// </remarks>
    public static class AtlasMap
    {
        /// <summary>
        /// The UV size of one tile: <c>1 / TilesPerRow</c> on both axes.
        /// </summary>
        public static Vector2f TileUvSize(AtlasLayout layout)
        {
            if (layout is null)
            {
                throw new ArgumentNullException(nameof(layout));
            }

            float size = 1.0F / layout.TilesPerRow;
            return new Vector2f(size, size);
        }

        /// <summary>
        /// The top-left UV of the tile at <paramref name="tileIndex"/>.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="tileIndex"/> lies outside
        /// <c>[0, TilesPerRow * TilesPerRow)</c>.
        /// </exception>
        public static Vector2f TileMin(AtlasLayout layout, int tileIndex)
        {
            if (layout is null)
            {
                throw new ArgumentNullException(nameof(layout));
            }

            int tileCount = layout.TilesPerRow * layout.TilesPerRow;
            if (tileIndex < 0 || tileIndex >= tileCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(tileIndex),
                    tileIndex,
                    "Tile index must be in [0, TilesPerRow * TilesPerRow).");
            }

            float size = 1.0F / layout.TilesPerRow;
            int column = tileIndex % layout.TilesPerRow;
            int row = tileIndex / layout.TilesPerRow;
            return new Vector2f(column * size, row * size);
        }
    }
}
