#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Lib.geometry;

/// <summary>
/// The one place points are created for a mesh operation. Positions within the weld tolerance map to a
/// single id, so the same cut point asked for from two different faces always answers with the same id.
/// </summary>
/// <remarks>
/// <para>Weld identity is what makes an operation's output watertight: without it, two faces that meet
/// along a cut would each name their own copy of the shared corner and an index-based edge count would
/// read the surface as open.</para>
///
/// <para>Points are looked up in a hash grid whose cell is the tolerance, so a query only compares
/// against the 27 neighbouring cells rather than every point added so far.</para>
///
/// <para>Extracted from [BooleanOperation] so other mesh operators share one weld implementation. The
/// tolerance is derived from an extent the caller supplies, because float precision is relative to the
/// size of what is being worked on - a tolerance that suits a large model would swallow a small
/// cutter, which is why the caller passes the <em>smallest</em> extent involved.</para>
/// </remarks>
internal sealed class PointWeld
{
    public PointWeld(float extent)
    {
        Extent = extent;
        _tolerance = MathF.Max(extent * ToleranceFactor, 1e-9f);
        _gridScale = 1f / _tolerance;
        _toleranceSq = _tolerance * _tolerance;
    }

    public float Extent { get; }

    /// <summary>Merge radius; also the slack callers use when asking whether a point lies on an edge.</summary>
    public float Tolerance => _tolerance;

    /// <summary>The merge radius squared, for callers testing many candidates against it.</summary>
    public float ToleranceSq => _toleranceSq;

    /// <summary>Every distinct position, indexed by the id returned from <see cref="GetOrAddPoint"/>.</summary>
    public List<Vector3> Positions { get; } = [];

    /// <summary>The id of <paramref name="position"/>, adding it when it is a new point.</summary>
    public int GetOrAddPoint(Vector3 position)
    {
        var (kx, ky, kz) = Quantize(position);
        for (var dz = -1; dz <= 1; dz++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            if (!_lookup.TryGetValue((kx + dx, ky + dy, kz + dz), out var candidate))
                continue;

            while (candidate >= 0)
            {
                if (Vector3.DistanceSquared(Positions[candidate], position) < _toleranceSq)
                    return candidate;

                candidate = _nextInBucket[candidate];
            }
        }

        var id = Positions.Count;
        Positions.Add(position);
        _nextInBucket.Add(_lookup.TryGetValue((kx, ky, kz), out var head) ? head : -1);
        _lookup[(kx, ky, kz)] = id;
        return id;
    }

    /// <summary>Bucket indices are 64 bit: a scene far from the origin would overflow a 32 bit cell index.</summary>
    private (long, long, long) Quantize(Vector3 position)
    {
        return ((long)MathF.Floor(position.X * _gridScale),
                (long)MathF.Floor(position.Y * _gridScale),
                (long)MathF.Floor(position.Z * _gridScale));
    }

    /// <summary>
    /// Weld radius as a fraction of extent. Splitting the same edge from two neighbouring faces computes
    /// the crossing point twice with different rounding, so the two results have to land in one bucket -
    /// this is the slack that lets them, and it stays well below any real feature.
    /// </summary>
    private const float ToleranceFactor = 4e-6f;

    private readonly float _tolerance;
    private readonly float _toleranceSq;
    private readonly float _gridScale;
    private readonly List<int> _nextInBucket = [];
    private readonly Dictionary<(long, long, long), int> _lookup = [];
}
