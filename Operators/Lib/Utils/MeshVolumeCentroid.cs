#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using T3.Core.DataTypes;

namespace Lib.Utils;

/// <summary>
/// Volume centroid of a face set, by fan-triangulating each face and summing tetrahedra
/// from the origin. Exact for closed solids; falls back to the average corner position when
/// the faces hold no volume (an open shell, or a flat sheet), so callers always get a pivot.
/// </summary>
internal static class MeshVolumeCentroid
{
    public static Vector3 Compute(MeshGeometry mesh, IReadOnlyList<int> faceIndices)
    {
        var positions = mesh.Positions;
        var offsets = mesh.FaceCornerOffsets;
        var cornerPoints = mesh.CornerPointIndices;

        var centroidSum = Vector3.Zero;
        var totalVolume = 0.0;
        foreach (var faceIndex in faceIndices)
        {
            var start = offsets[faceIndex];
            var end = offsets[faceIndex + 1];
            if (end - start < 3)
                continue;

            var v0 = positions[cornerPoints[start]];
            for (var c = start + 1; c < end - 1; c++)
            {
                var v1 = positions[cornerPoints[c]];
                var v2 = positions[cornerPoints[c + 1]];
                var volume = Vector3.Dot(v0, Vector3.Cross(v1, v2)) / 6.0;
                if (Math.Abs(volume) < 1e-15)
                    continue;

                centroidSum += (float)volume * ((v0 + v1 + v2) * 0.25f);
                totalVolume += volume;
            }
        }

        if (Math.Abs(totalVolume) >= 1e-15)
            return centroidSum / (float)totalVolume;

        var usedPoints = new HashSet<int>();
        var sum = Vector3.Zero;
        foreach (var faceIndex in faceIndices)
        {
            for (var c = offsets[faceIndex]; c < offsets[faceIndex + 1]; c++)
            {
                if (usedPoints.Add(cornerPoints[c]))
                    sum += positions[cornerPoints[c]];
            }
        }

        return usedPoints.Count == 0 ? Vector3.Zero : sum / usedPoints.Count;
    }
}
