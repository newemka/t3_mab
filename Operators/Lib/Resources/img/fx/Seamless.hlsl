cbuffer ParamConstants : register(b0)
{
    float Falloff;
    float Mode;
}

cbuffer Resolution : register(b1)
{
    float TargetWidth;
    float TargetHeight;
}

struct vsOutput
{
    float4 position : SV_POSITION;
    float2 texCoord : TEXCOORD;
};

Texture2D<float4> Image : register(t0);
sampler texSampler : register(s0);

float4 psMain(vsOutput input) : SV_TARGET
{
    float width, height;
    Image.GetDimensions(width, height);

    float2 uv = input.texCoord;
    float4 Base = Image.Sample(texSampler, uv);

    float direction = uv.x;
    float2 shiftedUV = float2(uv.x + 0.5, uv.y); 

    if (Mode < 0.5) {
         shiftedUV = float2(uv.x, uv.y + 0.5);
         direction = uv.y;
    }

    float4 seamSample = Image.Sample(texSampler, shiftedUV);

    float blendFactor = abs(1.0 - (direction * 2.0)) ;  
    blendFactor = smoothstep(0.0, Falloff, blendFactor);
  
   return lerp(Base,seamSample,blendFactor);

}