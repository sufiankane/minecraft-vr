using System;

namespace Cubeglass.Mesh
{
    /// <summary>
    /// The tile geometry of the texture atlas (ADR-0007): a square grid of
    /// <see cref="TilesPerRow"/> tiles per row, each <see cref="TileSize"/>
    /// pixels on a side.
    /// </summary>
    /// <remarks>
    /// A value-like record. It is declared as a record with explicit get-only
    /// properties rather than a positional record so <c>Cubeglass.Mesh</c>
    /// does not need an <c>IsExternalInit</c> polyfill on netstandard2.1.
    /// </remarks>
    public sealed record AtlasLayout
    {
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="tilesPerRow"/> or <paramref name="tileSize"/> is
        /// zero or negative.
        /// </exception>
        public AtlasLayout(int tilesPerRow, int tileSize)
        {
            if (tilesPerRow <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(tilesPerRow),
                    tilesPerRow,
                    "Tiles per row must be positive.");
            }

            if (tileSize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(tileSize),
                    tileSize,
                    "Tile size must be positive.");
            }

            TilesPerRow = tilesPerRow;
            TileSize = tileSize;
        }

        /// <summary>The number of tiles per row and per column.</summary>
        public int TilesPerRow { get; }

        /// <summary>The edge length of one tile in pixels.</summary>
        public int TileSize { get; }
    }
}
