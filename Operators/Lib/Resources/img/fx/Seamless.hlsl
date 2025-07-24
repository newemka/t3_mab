cbuffer ParamConstants : register(b0)
{
    float EdgeFallOff;
    float TillingMode; // 0 = Horizontal only, 1 = Vertical only, 2 = Both
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
    float4 OriginalColor = Image.Sample(texSampler, input.texCoord);
  
    
    // Handle Horizontal Seam (TillingMode 0)
    if (TillingMode == 0 )
    {
     
        if (input.texCoord.y < 0.5)
        {
            // Top half - blend with bottom edge
            float2 bottomCoord = float2(input.texCoord.x, input.texCoord.y + 0.5);
            float4 bottomSample = Image.Sample(texSampler, bottomCoord);
            
            // Blend factor: 1 at y=0, 0 at y=0.5
            float blendFactor = 1.0 - (input.texCoord.y * 2.0);
            blendFactor = smoothstep(0.0, EdgeFallOff, blendFactor);
            
            OriginalColor = lerp(OriginalColor, bottomSample, blendFactor);
        }
        else
        {
            // Bottom half - blend with top edge
            float2 topCoord = float2(input.texCoord.x, input.texCoord.y - 0.5);
            float4 topSample = Image.Sample(texSampler, topCoord);
            
            // Blend factor: 0 at y=0.5, 1 at y=1.0
            float blendFactor = (input.texCoord.y - 0.5) * 2.0;
            blendFactor = smoothstep(0.0, EdgeFallOff, blendFactor);
            
            OriginalColor = lerp(OriginalColor, topSample, blendFactor);
        }
    
    }
    // Handle Vertical Seam (TillingMode 1)
    else{
        if (input.texCoord.x < 0.5)
        {
            // Left half - blend with right edge
            float2 rightCoord = float2(input.texCoord.x + 0.5, input.texCoord.y);
            float4 rightSample = Image.Sample(texSampler, rightCoord);
            
            // Blend factor: 1 at x=0, 0 at x=0.5
            float blendFactor = 1.0 - (input.texCoord.x * 2.0);
            blendFactor = smoothstep(0.0, EdgeFallOff, blendFactor);
            
            OriginalColor = lerp(OriginalColor, rightSample, blendFactor);
        }
        else
        {
            // Right half - blend with left edge
            float2 leftCoord = float2(input.texCoord.x - 0.5, input.texCoord.y);
            float4 leftSample = Image.Sample(texSampler, leftCoord);
            
            // Blend factor: 0 at x=0.5, 1 at x=1.0
            float blendFactor = (input.texCoord.x - 0.5) * 2.0;
            blendFactor = smoothstep(0.0, EdgeFallOff, blendFactor);
            
            OriginalColor = lerp(OriginalColor, leftSample, blendFactor);
        }
    }


    


  
    return OriginalColor;
    
}