# SetShadow: cast shadows were never rendered

`[SetShadow]` renders the incoming command twice per frame — once into a shadow map, once for the visible pass —
and until now the shadow pass could never occlude anything.

## Defect 1: the shadow lookup's sampler was never bound

`mesh-DrawWithShadows.hlsl` declares three samplers (`texSampler : s0`, `linearSampler : s1`,
`clampedSampler : s2`) — `s2` is what `ShadowMap.Sample(clampedSampler, …)` and the BRDF lookup read through.
`DrawMeshWithShadow.t3` collected only two of them, so the slots shifted and `s2` stayed unbound. Fixed by
collecting the linear sampler twice: `[linear (s0), linear (s1), clamped (s2)]` (one duplicated connection).

## Defect 2: values that change *within* a frame were served from the per-frame cache

`[SetContextTexture]` publishes its texture around a subtree, so the resource that is current for a draw can
differ from the one an input slot cached at the start of the frame. The engine invalidates the graph once per
frame, so downstream ops saw stale state:

- `SrvFromTexture2d` built its view from a `null` context texture (read before the publication) and the later
  in-scope pull was skipped — the shader then sampled a null shadow map. Instrumented proof:
  ```
  get 'ShadowMap' found=False        <- input slot's cached value, read before the publication
  SRV <- tex #00000000 view=NULL
  publish 'ShadowMap' … tex #03292392
  SRV <- tex #00000000 view=NULL     <- input slot clean, so never re-read
  ```
- `SetShadow.t3` worked around this with `Group.ForceColorUpdate = true`, whose `InvalidateGraph()` re-dirtied the
  whole subtree before each pass. That also makes stateful operators step twice per frame (`[Trigger]` holds its
  result, `[Toggle Boolean]` toggles on every evaluation), which broke trigger-driven value chains.

### The fix: the ops that bind state refresh what they bind

`DirtyFlagTrigger.Animated` would fix the caching but is not acceptable here — it keeps those operators (and with
them the composition) out of idle mode. Instead, the operators that actually *bind* state for a pass ask for the
current value themselves, with a shallow `DirtyFlag.ForceInvalidate()` on the sources they are about to read:

- `SetPixelAndVertexShaderStage` refreshes the sources of its `ConstantBuffers` and `ShaderResources`;
- `OutputMergerStage` refreshes its `DepthStencilView` and `RenderTargetViews` sources;
- `ClearRenderTarget` refreshes its `RenderTarget` and `DepthStencilView` sources.

`ForceInvalidate()` only bumps that one slot's version — it does not walk upstream — so user value chains are not
re-invalidated and stateful operators do not step twice. The work happens only while a draw or pass is actually
being set up, so idle mode is unaffected.

The three view operators re-read their texture when they run (`Texture.DirtyFlag.ForceInvalidate()`) and rebuild
the view only when the resource actually changed. `IntsToBuffer` needs no change: the stage refresh re-evaluates
it per draw, and its `GetIntVar` input is already animated, so `[DrawMeshChunksAtPoints]` now packs
`IsShadowPass = 1` for the shadow pass and `0` for the visible pass — verified with a temporary probe:

```
[intstoprobe] packed [4,1,1,1]   <- shadow pass   (SegmentCount, UseWForSize, UseStretch, IsShadowPass)
[intstoprobe] packed [4,1,1,0]   <- visible pass
```

## Verification

With no forced update in the graph (`_agentTests`, TiXL-letter scene drawn with `[DrawMeshChunksAtPoints]`):

- `ShadowBias = 0.0001` versus `1.0` (occlusion effectively off) produce clearly different renders — with
  occlusion on the letters cast strong shadows, with it off the scene is uniformly lit;
- flanking the `[Trigger]` still drives `[BlendColors]`, so shadows and value chains work in the same session.

## Later/optional

- **Self-shadow acne** appears on a caster when the light is close to the target's view axis (the gimbal-lock
  case `Camera.Up`'s tooltip warns about). Now that occlusion works, `ShadowBias`/`ShadowOffset` may want tuning.
- `[PointLight]` transforms its subtree by its `Position`, so dragging the light also swings the scene.
- `DrawMeshAtPoints2.t3` binds **no** samplers on its `PixelShaderStage`/`VertexShaderStage` — an older,
  separate binding gap.
- A `CameraShadow`/`GroupTest` experiment and a depth-only shadow-pass shader were developed alongside this work
  and are not part of it; they were removed and backed up in `.temp/removed-wip-symbols.txt`.
