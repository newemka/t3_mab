#nullable enable
using System.Numerics;

namespace Lib.geometry;

/// <summary>
/// One corner of a <see cref="Polygon"/>.
/// </summary>
/// <remarks>
/// <see cref="PointId"/> is the corner's identity in a shared weld table, not a per-polygon index: two
/// corners cut from the same place carry the same id. That shared identity is what keeps neighbouring
/// faces meeting exactly, and it is why an index-based topology check reads the result as watertight.
/// </remarks>
internal struct Vert(Vector3 position, int pointId)
{
    public Vector3 Position = position;
    public int PointId = pointId;

    /// <summary>
    /// Corner attributes flattened to one component per entry, in schema order. Empty when the mesh
    /// carries no corner attributes.
    /// </summary>
    public float[] Attributes = [];
}
