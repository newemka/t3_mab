#nullable enable
using T3.Core.DataTypes.ShaderGraph;
using T3.Core.Utils;

namespace Lib.field.generate.sdf._;

[Guid("8fd60f01-9960-47fb-a730-45b50b9b94d2")]
internal sealed class ExecuteImage2dSdf : Instance<ExecuteImage2dSdf>
,IGraphNodeOp
{
    [Output(Guid = "b4922e19-3c13-4536-876c-3169e6532343")]
    public readonly Slot<ShaderGraphNode> Result = new();

    public ExecuteImage2dSdf()
    {
        ShaderNode = new ShaderGraphNode(this);

        Result.Value = ShaderNode;
        Result.UpdateAction += Update;
    }

    private void Update(EvaluationContext context)
    {
        ShaderNode.Update(context);
        _srv = SdfImageSrv.GetValue(context);
        if (_srv == null || _srv.IsDisposed)
            _srv = null;

        // Get all parameters to clear operator dirty flag
    }
    
    public ShaderGraphNode ShaderNode { get; }

    void IGraphNodeOp.AddDefinitions(CodeAssembleContext c)
    {
        c.Definitions.Append($$"""
                                float sdf2DExtruded{{ShaderNode}}(float3 p, float2 imageSize, float sdfScale, float halfHeight)
                                {
                                    float2 uv = p.xy / imageSize;
                                    uv.y *= -1;
                                    uv += 0.5;

                                    float2 clampedUV = clamp(uv, 0.0, 1.0);
                                    float2 delta = uv - clampedUV;
                                    float2 worldDelta = delta * imageSize;
                                    float outside2D = length(worldDelta);

                                    float texDist = 1.0 - saturate({{ShaderNode}}SdfImage.SampleLevel(ClampedSampler, clampedUV, 2.0));
                                    float d2d = texDist * sdfScale;

                                    float dz = abs(p.z) - halfHeight;
                                    float2 w = float2(d2d, dz);
                                    float extrusionDist = min(max(w.x, w.y), 0.0) + length(max(w, 0.0));

                                    if (all(uv >= 0.0) && all(uv <= 1.0))
                                        return extrusionDist;

                                    return sqrt(outside2D * outside2D + extrusionDist * extrusionDist);
                                }
                            """);

    }

    bool IGraphNodeOp.TryBuildCustomCode(CodeAssembleContext c)
    {
        c.AppendCall($"f{c}.w = (sdf2DExtruded{ShaderNode}(p{c}.xyz, {ShaderNode}Size, {ShaderNode}Scale, {ShaderNode}Height) + {ShaderNode}Offset);");
        c.AppendCall($"f{c}.xyz = p.w < 0.5 ? p{c}.xyz : 1;");

        return true;
    }

    void IGraphNodeOp.AppendShaderResources(ref List<ShaderGraphNode.SrvBufferReference> list)
    {
        if (_srv == null)
            return;

        // Skip if already added
        foreach (var x in list)
        {
            if (x.Srv == _srv)
                return;
        }

        list.Add(new ShaderGraphNode.SrvBufferReference($"Texture2D<float> {ShaderNode}SdfImage", _srv));
    }

    private ShaderResourceView? _srv;


    [Input(Guid = "bd8e1e6a-65bf-431c-a0f1-38c7858af0f5")]
    public readonly InputSlot<ShaderResourceView> SdfImageSrv = new();

    [GraphParam]
    [Input(Guid = "b9c106bb-8a49-47ca-ace5-3ba0d1373bf1")]
    public readonly InputSlot<Vector2> Size = new();
    
    [GraphParam]
    [Input(Guid = "5D256E1E-7189-4239-A1DD-717D909382A0")]
    public readonly InputSlot<float> Scale = new();
    
    [GraphParam]
    [Input(Guid = "7C1B8678-3961-43E7-9F61-DF4C82FB5C63")]
    public readonly InputSlot<float> Offset = new();

    [GraphParam]
    [Input(Guid = "D9F8D441-F6A3-411B-A3D2-34A7D729DAB5")]
    public readonly InputSlot<float> Height = new();

}