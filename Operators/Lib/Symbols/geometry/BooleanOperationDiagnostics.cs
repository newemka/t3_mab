#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Lib.Utils;
using T3.Core.DataTypes;
using T3.Core.Logging;

namespace Lib.geometry;

/// <summary>
/// Developer tool: runs a fixed table of boolean cases through the same kernel
/// [BooleanOperation] uses and reports the measured volume and watertightness against the volume
/// the arrangement of boxes implies.
/// </summary>
/// <remarks>
/// Two boxes give a result whose volume is known by hand, so a failure can be attributed to a
/// specific case instead of being chased through a graph. Every case is an axis-aligned box pair:
/// separated, touching, overlapping, contained and coincident, which between them cover the
/// robust-coplanar paths that dominate boolean code.
/// </remarks>
[Guid("7c4b1e02-9d38-4a51-8e60-3f2a8d17c549")]
internal sealed class BooleanOperationDiagnostics : Instance<BooleanOperationDiagnostics>
{
    [Output(Guid = "2f8e6a41-5b73-4c09-9d84-71e0a3f6b2d8")]
    public readonly Slot<string> Report = new();

    [Output(Guid = "b1d0c395-7e28-4f6a-83b7-95c2e6d40173")]
    public readonly Slot<bool> AllPassed = new();

    public BooleanOperationDiagnostics()
    {
        Report.UpdateAction = Update;
        AllPassed.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var run = Run.GetValue(context);
        if (!run)
        {
            _hasRun = false;
            return;
        }

        if (_hasRun)
            return;

        _hasRun = true;

        var text = new StringBuilder();
        var allPassed = true;
        var filter = Only.GetValue(context);
        if (Detail.GetValue(context))
        {
            text.Append("case            mode          faces   volume   expected   open  result\n");
        }

        foreach (var testCase in Cases)
        {
            if (!string.IsNullOrWhiteSpace(filter)
                && filter.IndexOf(testCase.Name, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }
            var sizeA = testCase.SizeA;
            var sizeB = testCase.SizeB;
            var left = TestBoxGeometry.Create(sizeA, Vector3.Zero);
            var right = TestBoxGeometry.Create(sizeB, testCase.Offset);
            var volumeA = TestBoxGeometry.Volume(sizeA);
            var volumeB = TestBoxGeometry.Volume(sizeB);
            var overlap = TestBoxGeometry.OverlapVolume(sizeA, Vector3.Zero, sizeB, testCase.Offset);

            // The cases below are only meaningful if the boxes really sit where the name says, so the
            // measured bounds are logged rather than assumed.
            Log.Debug($"BooleanOperationDiagnostics: {testCase.Name} A={Bounds(left)} B={Bounds(right)} overlap={overlap:F4}");

            foreach (var operation in Modes)
            {
                var expected = operation switch
                {
                    BooleanOperation.Operations.Union => volumeA + volumeB - overlap,
                    BooleanOperation.Operations.Difference => volumeA - overlap,
                    _ => overlap,
                };

                var result = new MeshGeometry();
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                BooleanOperation.Evaluate(left, [right], operation, splitIntoParts: false, result);
                var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - started)
                                * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

                _stats.Measure(result);
                var volumeError = MathF.Abs(_stats.Volume - expected);
                var passed = _stats.BoundaryEdges == 0
                             && _stats.NonManifoldEdges == 0
                             && volumeError <= VolumeTolerance;

                allPassed &= passed;
                text.Append($"{testCase.Name,-15} {operation,-13} {_stats.FaceCount,5} {_stats.Volume,8:F3} "
                            + $"{expected,10:F3} {_stats.BoundaryEdges,6} {(passed ? "pass" : "FAIL"),6}\n");

                var status = passed ? "pass" : "FAIL";
                Log.Info($"BooleanOperationDiagnostics: {testCase.Name} {operation} -> {status} "
                         + $"faces={_stats.FaceCount} volume={_stats.Volume:F4} expected={expected:F4} "
                         + $"openEdges={_stats.BoundaryEdges} nonManifold={_stats.NonManifoldEdges} {elapsedMs:F0}ms");
            }
        }

        Report.Value = text.ToString();
        AllPassed.Value = allPassed;
        Log.Info($"BooleanOperationDiagnostics: {(allPassed ? "all cases passed" : "FAILURES present")} "
                 + $"({Cases.Length} cases x {Modes.Length} modes)");
    }

    private readonly record struct Case(string Name, Vector3 SizeA, Vector3 SizeB, Vector3 Offset);

    private static string Bounds(MeshGeometry mesh)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var position in mesh.Positions)
        {
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        return $"({min.X:F2},{min.Y:F2},{min.Z:F2})..({max.X:F2},{max.Y:F2},{max.Z:F2}) pts={mesh.PointCount} faces={mesh.FaceCount}";
    }

    /// <summary>Box pairs whose boolean results have a volume that can be worked out by hand.</summary>
    private static readonly Case[] Cases =
    [
        // No shared volume at all: both surfaces have to survive untouched.
        new("Separated", new Vector3(1, 1, 1), new Vector3(1, 1, 1), new Vector3(1.5f, 0, 0)),
        // Face-adjacent: they share a whole face but no volume, which is the coplanar case where a
        // union must not emit the shared face twice and a difference must remove nothing.
        new("Touching", new Vector3(1, 1, 1), new Vector3(1, 1, 1), new Vector3(1f, 0, 0)),
        // Offset on every axis, so every face of one clips the other.
        new("Overlapping", new Vector3(1, 1, 1), new Vector3(1, 1, 1), new Vector3(0.25f, 0.29f, 0.11f)),
        // A wholly enclosed operand: no surface of it lies outside the other.
        new("Contained", new Vector3(2, 2, 2), new Vector3(1, 1, 1), new Vector3(0, 0, 0)),
        // Identical solids: every face is coplanar with a face of the other.
        new("Coincident", new Vector3(1, 1, 1), new Vector3(1, 1, 1), new Vector3(0, 0, 0)),
    ];

    private static readonly BooleanOperation.Operations[] Modes =
    [
        BooleanOperation.Operations.Union,
        BooleanOperation.Operations.Difference,
        BooleanOperation.Operations.Intersection,
    ];

    /// <summary>A hair of slack for float volume integration; the failure it catches is percent-scale, not this.</summary>
    private const float VolumeTolerance = 1e-3f;

    private readonly MeshGeometryStats _stats = new();
    private bool _hasRun;

    [Input(Guid = "6a3c9f24-8e15-4b70-a2d6-47f1b8e05c93")]
    public readonly InputSlot<bool> Run = new();

    [Input(Guid = "d5e7b120-3c84-4a26-9f51-68b0d2c7e394")]
    public readonly InputSlot<bool> Detail = new();

    /// <summary>Runs only the cases whose name contains this text; empty runs all of them. For narrowing a log.</summary>
    [Input(Guid = "8f2b6d41-0a97-4e35-b8c2-5164f7d09e28")]
    public readonly InputSlot<string> Only = new();
}
