#nullable enable
using System.Numerics;

namespace Lib.geometry;

/// <summary>
/// An oriented plane in Hessian normal form, used as a half-space: the side a point falls on is the
/// sign of its distance, and positive is the outward side.
/// </summary>
/// <remarks>
/// Extracted from [BooleanOperation] so other mesh operators can share the same plane arithmetic
/// instead of each growing their own.
/// </remarks>
internal readonly record struct HalfSpace(Vector3 Normal, float Offset)
{
    /// <summary>Signed distance from the plane; positive on the outward side.</summary>
    public float Distance(Vector3 point) => Vector3.Dot(Normal, point) - Offset;

    /// <summary>The same plane facing the other way, which is what inverting a solid does to it.</summary>
    public HalfSpace Flipped => new(-Normal, -Offset);

    /// <summary>False for a degenerate plane whose normal never got a usable length.</summary>
    public bool IsValid => Normal.LengthSquared() > 0.5f;

    /// <summary>A plane through <paramref name="point"/> with <paramref name="normal"/>; the normal is normalized.</summary>
    public static HalfSpace Through(Vector3 normal, Vector3 point)
    {
        var length = normal.Length();
        if (length < 1e-20f)
            return default;

        normal /= length;
        return new HalfSpace(normal, Vector3.Dot(normal, point));
    }
}
