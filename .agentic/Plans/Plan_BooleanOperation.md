# Plan: BooleanOperation (CSG booleans on MeshGeometry)

Status: **parked mid-implementation** — no-operand pass-through works and ships; the solver is
unfinished and produces open meshes for anything needing a cut.
Last update: 2026-09-27

## Why this exists

`[VoronoiFracture]` works on a `CubeGeometry` but not on complex geometry such as extruded 3D
text. The request that started this: a `BooleanOperation` op (union / difference / intersection)
that can also emit all the resulting pieces, because both `CubeGeometry -> VoronoiFracture ->
SeparateLooseParts` and `TextToCurves -> CurvesToGeometryExp -> SeparateLooseParts` produce
multi-part geometry.

## What ships now (verified)

- **No operand connected = pass-through.** `Result` is the input instance itself, so attributes,
  part table and part attributes all survive. This is deliberate and tested by hand; it means the
  op is safe to leave in a chain.
- **Two disjoint solids combine correctly** in a manual graph (union of two separated cubes:
  12 faces, volume 2.0, watertight).
- **`Coincident Difference` and `Coincident Intersection`** pass the diagnostic table exactly.
- `Operands` is a `MultiInputSlot`; operands are folded left to right (union merges all,
  difference/intersection subtract one at a time).
- `SplitIntoParts` emits each connected component as its own part with a volume-centroid pivot.
- Cut faces carry `IsCut = 1` and `Selection = 1`, matching the fracture's caps.

## What is broken

Everything that needs an actual cut. Measured with `BooleanOperationDiagnostics` (5 cases x 3 modes):

| Case | Status |
|---|---|
| Separated | union/difference/intersection: FAIL |
| Touching | FAIL |
| Overlapping | FAIL (24 open edges on all three) |
| Contained | FAIL (volumes 5.0/4.0/4.0 against 8.0/7.0/1.0) |
| Coincident | difference + intersection pass, union FAIL (0 faces) |

The op logs a warning whenever the result is not watertight; that warning is the honest signal.

## Architecture (rebuilt 2026-09-27, third attempt)

`Operators/Lib/Symbols/geometry/BooleanOperation.cs`

- **Vertices are point ids** into a single `PointWeld` table. Every vertex — source, split or cap —
  goes through `GetOrAddPoint`, so two faces cut at the same place necessarily share one id. This
  is the property that lets the pieces close, and it is the reason the older BSP-and-signature
  kernel was abandoned: its per-fragment bookkeeping drifted out of sync (faces were lost as
  `LOST`, and `classified 11 of 6 faces` was observed).
- **No BSP tree.** `Kernel.Partition` splits a face only along the planes it actually crosses —
  the other solid's face planes taken as inward half-spaces (`Solid.Planes`) — then keeps the
  fragments a classification predicate accepts.
- **Classification** is a point test (`SolidTester`, wrapping `MeshInsideTester`): a fragment's own
  vertices vote, the centroid breaks a tie.
- **Capping** groups boundary edges by the plane that cut them, chains loops **by point id**, and
  tessellates with `Tess`. Because loop vertices are the same ids the surface uses, caps meet the
  surface exactly.

## Open defect (start here)

`Separated` (two boxes 1.5 apart, verified bounds `A=(-0.5..0.5)`, `B=(1.0..2.0)`, `overlap=0`)
reports `partition in=6 kept=2 coincident=4` — four of A's faces are declared to lie in B's planes
when the closest approach is 0.5.

`Kernel.LiesInPlane` requires **all** corners within `_epsilon` (`weld.Extent * 1e-6` = 1e-6), and
the executed build is confirmed current by the `BooleanOperation[{KernelVersion}]` log marker. So
the next step is not more reasoning but the log line that prints, for each face declared coplanar,
its four corners and their individual distances:

```
BooleanOperation[4]:   face(x,y,z) lies in n=(...) d=...; distances=(...)...
```

- Distances all `0.0000` for four faces → the faces really are coplanar and the bug is in
  `Solid.Load` / `HalfSpaceOf` (the planes A is tested against are not B's).
- Distances around `0.5000` → `HalfSpace.Distance` is not measuring what it claims, and the fix is
  arithmetic inside `HalfSpace`.

Note: `BooleanOperation.cs` still carries temporary diagnostics — the `KernelVersion` marker, the
`partition`/`emit` `Log.Debug` lines, the `lies in` dump, and the `_planesTouched` / `_planesFullyOn`
counters. **Strip all of these once the table is green.** Keep the open-edges warning and the
`[BooleanOperationDiagnostics]` op.

## Diagnostic op (keep)

`Operators/Lib/Symbols/geometry/BooleanOperationDiagnostics.cs` (+ `.t3` / `.t3ui`), with
`Lib/Utils/TestBoxGeometry.cs`. It runs five box pairs (separated, touching, overlapping, contained,
coincident) through the same `BooleanOperation.Evaluate` path the op uses — deliberately not a
parallel implementation — and reports face count, measured volume against the volume worked out by
hand, and open/non-manifold edges per case. `Only` filters cases to keep the log readable.

This harness was worth more than any amount of code reading: it found a malformed-output crash, the
double-emitted coplanar faces, and the frozen-table/stale-assembly confusion. Any future work on this
op should be gated on it, not on how the result looks in the viewport.

## Lessons worth keeping

- **A source change that leaves the diagnostic numbers byte-identical is a signal about the
  pipeline, not the algorithm.** Check the running assembly (the log file under
  `%APPDATA%\TiXL4.3-alpha\Log\` is the fastest way) before concluding anything about the kernel.
- **Instrument, don't infer.** Every correct diagnosis in this work came from logged numbers; every
  wrong one came from reading the code and predicting.
- The `Touching` test case was written wrong at first (offset 0.5 is a half-overlap, not a touch)
  with expected volumes that assumed no overlap — the row could never pass. Check the harness's own
  arithmetic before blaming the kernel.
