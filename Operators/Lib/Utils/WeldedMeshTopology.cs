#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using T3.Core.DataTypes;

namespace Lib.Utils;

/// <summary>
/// Open and non-manifold edge counts over positions welded by distance, cached per input
/// version. Two positions closer than the tolerance are one vertex - that is float noise
/// and per-face vertex splits, not geometry. Callers decide the tolerance: it must stay
/// far below their own cut weld so a real crack is still reported as open.
/// </summary>
internal sealed class WeldedMeshTopology
{
    public int BoundaryEdges { get; private set; }
    public int NonManifoldEdges { get; private set; }

    /// <summary>Remeasures if <paramref name="geometry"/> changed since the last call. Returns true when it did.</summary>
    public bool UpdateIfChanged(MeshGeometry geometry, float toleranceFactor)
    {
        if (ReferenceEquals(geometry, _lastGeometry) && geometry.Version == _lastVersion && toleranceFactor == _lastToleranceFactor)
            return false;

        Measure(geometry, toleranceFactor);
        _lastGeometry = geometry;
        _lastVersion = geometry.Version;
        _lastToleranceFactor = toleranceFactor;
        return true;
    }

    /// <summary>Counts edges after welding positions within <paramref name="toleranceFactor"/> of the mesh extent.</summary>
    public void Measure(MeshGeometry geometry, float toleranceFactor)
    {
        BoundaryEdges = 0;
        NonManifoldEdges = 0;
        var positions = geometry.Positions;
        if (positions.Length == 0 || geometry.FaceCount == 0)
        {
            return;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var position in positions)
        {
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        var extent = MathF.Max(max.X - min.X, MathF.Max(max.Y - min.Y, max.Z - min.Z));
        var tolerance = extent * toleranceFactor;
        var toleranceSq = tolerance * tolerance;
        var gridScale = tolerance > 0 ? 1f / tolerance : 0f;

        // Weld points: the bucket size equals the tolerance, so the 27 neighbouring buckets
        // cover the whole radius and a bucket chains its points through _nextInBucket.
        _weldedPositions.Clear();
        _bucketHeads.Clear();
        _nextInBucket.Clear();
        var weldedIds = new int[positions.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            var position = positions[i];
            var (kx, ky, kz) = ((int)MathF.Floor(position.X * gridScale),
                                (int)MathF.Floor(position.Y * gridScale),
                                (int)MathF.Floor(position.Z * gridScale));
            var found = -1;
            for (var dz = -1; dz <= 1 && found < 0; dz++)
            for (var dy = -1; dy <= 1 && found < 0; dy++)
            for (var dx = -1; dx <= 1 && found < 0; dx++)
            {
                if (!_bucketHeads.TryGetValue((kx + dx, ky + dy, kz + dz), out var candidate))
                    continue;

                while (candidate >= 0)
                {
                    if (Vector3.DistanceSquared(_weldedPositions[candidate], position) < toleranceSq)
                    {
                        found = candidate;
                        break;
                    }

                    candidate = _nextInBucket[candidate];
                }
            }

            if (found < 0)
            {
                found = _weldedPositions.Count;
                _weldedPositions.Add(position);
                _nextInBucket.Add(_bucketHeads.TryGetValue((kx, ky, kz), out var head) ? head : -1);
                _bucketHeads[(kx, ky, kz)] = found;
            }

            weldedIds[i] = found;
        }

        _edgeUse.Clear();
        var offsets = geometry.FaceCornerOffsets;
        var corners = geometry.CornerPointIndices;
        for (var faceIndex = 0; faceIndex < geometry.FaceCount; faceIndex++)
        {
            var start = offsets[faceIndex];
            var end = offsets[faceIndex + 1];
            for (var c = start; c < end; c++)
            {
                var a = weldedIds[corners[c]];
                var b = weldedIds[corners[c + 1 == end ? start : c + 1]];
                if (a == b)
                    continue; // welding collapsed this edge; it is not an open edge

                var key = a < b ? (a, b) : (b, a);
                _edgeUse[key] = _edgeUse.GetValueOrDefault(key) + 1;
            }
        }

        foreach (var use in _edgeUse.Values)
        {
            if (use == 1)
            {
                BoundaryEdges++;
            }
            else if (use > 2)
            {
                NonManifoldEdges++;
            }
        }
    }

    private MeshGeometry? _lastGeometry;
    private int _lastVersion;
    private float _lastToleranceFactor = float.NaN;
    private readonly List<Vector3> _weldedPositions = [];
    private readonly List<int> _nextInBucket = [];
    private readonly Dictionary<(int, int, int), int> _bucketHeads = [];
    private readonly Dictionary<(int, int), int> _edgeUse = [];
}
