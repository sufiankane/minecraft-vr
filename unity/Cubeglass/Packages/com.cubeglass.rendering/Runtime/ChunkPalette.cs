using System;
using Cubeglass.Voxel;
using UnityEngine;

namespace Cubeglass.Unity.Rendering
{
    /// <summary>
    /// Generates the placeholder chunk material (TD-060 M-5): a 16×16 colour
    /// palette atlas matching <see cref="AtlasLayout"/>'s 16-tile grid plus an
    /// unlit shader that multiplies the palette colour by the mesh vertex
    /// colour (the mesher's per-vertex AO). This gives every chunk a distinct
    /// block-coloured surface instead of the untextured default; art can
    /// replace the palette by assigning <see cref="ChunkViewManager.ViewMaterial"/>.
    /// </summary>
    /// <remarks>
    /// The shader lives at <c>Assets/Resources/Cubeglass/ChunkPalette.shader</c>
    /// so a player build includes it without a scene reference; if it is
    /// missing (for example an asset-stripped build), the factory falls back to
    /// the always-included built-in shaders. Both the material and the atlas
    /// are generated once per manager and destroyed with it, so steady-state
    /// rendering performs no allocation.
    /// </remarks>
    public static class ChunkPalette
    {
        /// <summary>Tiles per axis, matching the manager's <c>AtlasLayout(16, 16)</c>.</summary>
        public const int AtlasTiles = 16;

        /// <summary>Resources path of the unlit palette shader.</summary>
        public const string ShaderResourcePath = "Cubeglass/ChunkPalette";

        /// <summary>
        /// Creates the material and its palette atlas using
        /// <paramref name="registry"/>. The caller owns both (destroy them with
        /// the owning object).
        /// </summary>
        public static Material CreateMaterial(IBlockRegistry registry)
        {
            Shader shader = FindShader();
            if (shader == null)
            {
                Debug.LogError("ChunkPalette: no usable chunk shader found; chunks will use the default material.");
                return null;
            }

            var material = new Material(shader);
            material.name = "CubeglassChunkPalette";
            material.hideFlags = HideFlags.HideAndDontSave;
            material.mainTexture = CreateAtlas(registry);
            return material;
        }

        /// <summary>
        /// The palette atlas: every texel is one atlas tile; defined blocks get
        /// a colour per face role (top / front / side) and undefined tiles a
        /// neutral grey. Point filtering keeps the single-texel tiles crisp and
        /// repeat wrapping lets merged greedy quads span tiles.
        /// </summary>
        public static Texture2D CreateAtlas(IBlockRegistry registry)
        {
            var texture = new Texture2D(AtlasTiles, AtlasTiles, TextureFormat.RGBA32, false);
            texture.name = "CubeglassChunkPaletteAtlas";
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Repeat;

            var pixels = new Color32[AtlasTiles * AtlasTiles];
            var neutral = (Color32)new Color(0.35f, 0.35f, 0.35f, 1f);
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = neutral;
            }

            if (registry != null)
            {
                for (int i = 0; i < registry.Placeable.Count; i++)
                {
                    BlockDefinition definition = registry.Get(registry.Placeable[i]);
                    SetTile(pixels, definition.AtlasIndexTop, ColourFor(definition, true));
                    SetTile(pixels, definition.AtlasIndexFront, ColourFor(definition, false));
                    SetTile(pixels, definition.AtlasIndexSide, ColourFor(definition, false));
                }
            }

            texture.SetPixels32(pixels);
            // Kept CPU-readable (the atlas is 1 KiB): tests and tooling can
            // inspect the palette, and art replacement is a material swap.
            texture.Apply(false, false);
            return texture;
        }

        /// <summary>
        /// The palette colour for a block face: a hand-tuned table for the S7
        /// slice blocks keyed by data name, else a deterministic hue derived
        /// from the block id so a future block is still distinguishable.
        /// </summary>
        public static Color ColourFor(BlockDefinition definition, bool top)
        {
            switch (definition.Name)
            {
                case "Stone":
                    return new Color(0.55f, 0.56f, 0.58f, 1f);
                case "Dirt":
                    return new Color(0.44f, 0.31f, 0.20f, 1f);
                case "Grass":
                    return top
                        ? new Color(0.32f, 0.56f, 0.22f, 1f)
                        : new Color(0.42f, 0.31f, 0.19f, 1f);
                case "Sand":
                    return new Color(0.82f, 0.75f, 0.50f, 1f);
                case "Wood":
                    return top
                        ? new Color(0.62f, 0.48f, 0.28f, 1f)
                        : new Color(0.40f, 0.28f, 0.16f, 1f);
                default:
                    return FallbackColour(definition.Id.Value, top);
            }
        }

        /// <summary>
        /// The shader: the project's unlit palette shader when included, else
        /// the always-included built-ins (which ignore either the vertex colour
        /// or the palette, but keep chunks visible).
        /// </summary>
        public static Shader FindShader()
        {
            Shader shader = Resources.Load<Shader>(ShaderResourcePath);
            if (shader != null)
            {
                return shader;
            }

            return Shader.Find("Cubeglass/ChunkPalette")
                ?? Shader.Find("Unlit/Texture")
                ?? Shader.Find("Sprites/Default");
        }

        private static Color FallbackColour(ushort id, bool top)
        {
            float hue = (id * 0.618034f) % 1f;
            Color colour = Color.HSVToRGB(hue, 0.55f, top ? 0.85f : 0.68f);
            colour.a = 1f;
            return colour;
        }

        private static void SetTile(Color32[] pixels, int atlasIndex, Color colour)
        {
            if (atlasIndex < 0 || atlasIndex >= pixels.Length)
            {
                return;
            }

            pixels[atlasIndex] = colour;
        }
    }
}
