// FluentGlassEffect.fx
// Compact backdrop shader for UWP chrome surfaces.

Texture2D input : register(t0);
SamplerState samplerState : register(s0);

cbuffer Constants : register(b0)
{
    float2 offset;
    float blurAmount;
    float saturation;
    float4 tintColor;
    float tintOpacity;
    float luminosityOpacity;
    float alpha;
}

float3 SaturateColor(float3 color, float amount)
{
    float gray = dot(color, float3(0.2126, 0.7152, 0.0722));
    return lerp(float3(gray, gray, gray), color, amount);
}

float4 main(float2 uv : TEXCOORD) : SV_Target
{
    float2 stepUv = offset * max(blurAmount, 0.0);

    float4 color = input.Sample(samplerState, uv) * 0.120;
    color += input.Sample(samplerState, uv + float2(-1.0,  0.0) * stepUv) * 0.100;
    color += input.Sample(samplerState, uv + float2( 1.0,  0.0) * stepUv) * 0.100;
    color += input.Sample(samplerState, uv + float2( 0.0, -1.0) * stepUv) * 0.100;
    color += input.Sample(samplerState, uv + float2( 0.0,  1.0) * stepUv) * 0.100;
    color += input.Sample(samplerState, uv + float2(-1.0, -1.0) * stepUv) * 0.075;
    color += input.Sample(samplerState, uv + float2( 1.0, -1.0) * stepUv) * 0.075;
    color += input.Sample(samplerState, uv + float2(-1.0,  1.0) * stepUv) * 0.075;
    color += input.Sample(samplerState, uv + float2( 1.0,  1.0) * stepUv) * 0.075;
    color += input.Sample(samplerState, uv + float2(-2.0,  0.0) * stepUv) * 0.045;
    color += input.Sample(samplerState, uv + float2( 2.0,  0.0) * stepUv) * 0.045;
    color += input.Sample(samplerState, uv + float2( 0.0, -2.0) * stepUv) * 0.045;
    color += input.Sample(samplerState, uv + float2( 0.0,  2.0) * stepUv) * 0.045;

    color.rgb = SaturateColor(color.rgb, saturation);

    float luminance = dot(color.rgb, float3(0.2126, 0.7152, 0.0722));
    float3 luminosity = lerp(color.rgb, float3(luminance, luminance, luminance), luminosityOpacity);
    color.rgb = lerp(luminosity, tintColor.rgb, tintOpacity);
    color.a = saturate(alpha);
    return color;
}
