---
id: set-shadow-cast-shadows
scope: operators
added: 2026-09-30
added-in-version: "4.3"
---

# `[SetShadow]` casts shadows and keeps value chains intact

Manual test set for the two `[SetShadow]` defects (unbound shadow sampler, per-frame cached view) and the
`Group.ForceColorUpdate` workaround that was removed.

## Setup

A project with `[SetShadow]`: geometry with a caster and a receiver in its `Command`, a light whose
`LightPosition` is well off the target's view axis (e.g. `(-2, 2, 2)` toward the origin), and a value chain
(`[Trigger]` → `[ToggleBoolean]`/`[Ease]` → `[BlendColors]`) driving a material colour.

## Steps

1. Let the scene render for a second.
   - **Expect:** the caster throws a visible shadow onto the receiver.
   - **Previously:** no shadow at all — the shader sampled a null shadow map through an unbound sampler.
2. Set `DrawMeshWithShadow.ShadowBias` to `1.0` (occlusion effectively off) and compare with step 1.
   - **Expect:** a clearly different image; the shadow is gone with the bias at `1.0`.
3. Set `ShadowBias` back, then flank the `[Trigger]` input.
   - **Expect:** the driven colour reaches `ColorB` in the same frame, with the shadow still present.
   - **Previously:** with `Group.ForceColorUpdate = true` the forced re-invalidation made `[ToggleBoolean]`
     step twice per frame, so the chain flipped back and the visible pass kept the old colour.
4. Enable `SetShadow.ShowDebug`.
   - **Expect:** the shadow map is drawn as a greyscale quad (camera gizmos stay visible over it).

## Regression risk

- `SrvFromTexture2d` / `RtvFromTexture2d` / `DsvFromTexture2d` now re-evaluate per pull. Check that repeated
  draws in one frame do not leak views (the identity check should rebuild only on an actual resource change).
- Any operator that reads a context texture published by `[SetContextTexture]` inside its scope benefits from the
  same fix; verify a second consumer (e.g. the prefiltered-specular map) still works.
