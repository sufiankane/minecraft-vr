using System;
using Cubeglass.CoreMath;

namespace Cubeglass.Mesh.Tests
{
    /// <summary>
    /// Test-only canonical mesh hash: FNV-1a 64 over the exact little-endian
    /// bytes of the five <see cref="MeshData"/> streams plus the counts.
    /// </summary>
    /// <remarks>
    /// The hashed byte sequence is, in order: <c>VertexCount</c> (int32 LE),
    /// <c>IndexCount</c> (int32 LE), every element of <c>Positions</c>
    /// (3 x float32 LE), <c>Normals</c> (3 x float32 LE), <c>Uvs</c>
    /// (2 x float32 LE), <c>Ao</c> (1 byte) and <c>Indices</c> (int32 LE).
    /// FNV-1a 64 uses offset basis <c>0xCBF29CE484222325</c> and prime
    /// <c>0x100000001B3</c>, mixing one byte at a time.
    /// </remarks>
    internal static class MeshHash
    {
        private const ulong OffsetBasis = 0xCBF29CE484222325UL;
        private const ulong Prime = 0x00000100000001B3UL;

        internal static ulong Hash(MeshData mesh)
        {
            ArgumentNullException.ThrowIfNull(mesh);

            ulong hash = OffsetBasis;
            hash = MixInt32(hash, mesh.VertexCount);
            hash = MixInt32(hash, mesh.IndexCount);

            ReadOnlySpan<Vector3f> positions = mesh.Positions.Span;
            for (int i = 0; i < positions.Length; i++)
            {
                hash = MixSingle(hash, positions[i].X);
                hash = MixSingle(hash, positions[i].Y);
                hash = MixSingle(hash, positions[i].Z);
            }

            ReadOnlySpan<Vector3f> normals = mesh.Normals.Span;
            for (int i = 0; i < normals.Length; i++)
            {
                hash = MixSingle(hash, normals[i].X);
                hash = MixSingle(hash, normals[i].Y);
                hash = MixSingle(hash, normals[i].Z);
            }

            ReadOnlySpan<Vector2f> uvs = mesh.Uvs.Span;
            for (int i = 0; i < uvs.Length; i++)
            {
                hash = MixSingle(hash, uvs[i].X);
                hash = MixSingle(hash, uvs[i].Y);
            }

            ReadOnlySpan<byte> ao = mesh.Ao.Span;
            for (int i = 0; i < ao.Length; i++)
            {
                hash = MixByte(hash, ao[i]);
            }

            ReadOnlySpan<int> indices = mesh.Indices.Span;
            for (int i = 0; i < indices.Length; i++)
            {
                hash = MixInt32(hash, indices[i]);
            }

            return hash;
        }

        private static ulong MixByte(ulong hash, byte value)
        {
            return (hash ^ value) * Prime;
        }

        private static ulong MixInt32(ulong hash, int value)
        {
            hash = MixByte(hash, (byte)value);
            hash = MixByte(hash, (byte)(value >> 8));
            hash = MixByte(hash, (byte)(value >> 16));
            hash = MixByte(hash, (byte)(value >> 24));
            return hash;
        }

        private static ulong MixSingle(ulong hash, float value)
        {
            return MixInt32(hash, BitConverter.SingleToInt32Bits(value));
        }
    }
}
