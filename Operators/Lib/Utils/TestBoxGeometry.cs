#nullable enable
using System;
using System.Numerics;
using T3.Core.DataTypes;

namespace Lib.Utils;

/// <summary>
/// Builds an axis-aligned box as a closed <see cref="MeshGeometry"/>: six quad faces sharing eight
/// points, wound counter-clockwise seen from outside. Used by the boolean diagnostics, where an
/// operand has to be known-good so a failure can be attributed to the kernel rather than the input.
/// </summary>
internal static class TestBoxGeometry
{
    public static MeshGeometry Create(Vector3 size, Vector3 center)
    {
        var half = size * 0.5f;
        var min = center - half;
        var max = center + half;

        var mesh = new MeshGeometry
        {
            Positions =
            [
                new Vector3(min.X, min.Y, min.Z),
                new Vector3(max.X, min.Y, min.Z),
                new Vector3(max.X, max.Y, min.Z),
                new Vector3(min.X, max.Y, min.Z),
                new Vector3(min.X, min.Y, max.Z),
                new Vector3(max.X, min.Y, max.Z),
                new Vector3(max.X, max.Y, max.Z),
                new Vector3(min.X, max.Y, max.Z),
            ],
            FaceCornerOffsets =
            [
                0, 4, 8, 12, 16, 20, 24,
            ],
            CornerPointIndices =
            [
                4, 5, 6, 7, // +Z
                1, 0, 3, 2, // -Z
                5, 1, 2, 6, // +X
                0, 4, 7, 3, // -X
                7, 6, 2, 3, // +Y
                0, 1, 5, 4, // -Y
            ],
        };

        mesh.InvalidateTopologyCaches();
        return mesh;
    }

    /// <summary>Volume of the box overlap, i.e. the volume of the intersection of two such boxes.</summary>
    public static float OverlapVolume(Vector3 sizeA, Vector3 centerA, Vector3 sizeB, Vector3 centerB)
    {
        var halfA = sizeA * 0.5f;
        var halfB = sizeB * 0.5f;
        var minA = centerA - halfA;
        var maxA = centerA + halfA;
        var minB = centerB - halfB;
        var maxB = centerB + halfB;

        var overlap = Vector3.Min(maxA, maxB) - Vector3.Max(minA, minB);
        if (overlap.X <= 0 || overlap.Y <= 0 || overlap.Z <= 0)
            return 0;

        return overlap.X * overlap.Y * overlap.Z;
    }

    public static float Volume(Vector3 size)
    {
        return MathF.Abs(size.X * size.Y * size.Z);
    }
}
