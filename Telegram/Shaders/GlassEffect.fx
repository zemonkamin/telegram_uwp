// GlassEffect.fx
// Неполосатое градиентное стеклянное размытие для раскрытого профильного баннера.
// Без синусоидального distortion: blur + smooth alpha-gradient + мягкое затемнение книзу.

Texture2D input : register(t0);
SamplerState samplerState : register(s0);

cbuffer Constants : register(b0)
{
    float2 offset;       // размер одного texel в UV, например 1.0 / textureSize
    float blurAmount;   // множитель радиуса blur
    float transparency; // итоговая прозрачность нижней части
    float gradientStart;
    float gradientEnd;
}

float SmoothGradient(float y)
{
    float t = saturate((y - gradientStart) / max(gradientEnd - gradientStart, 0.001));
    return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
}

float4 main(float2 uv : TEXCOORD) : SV_Target
{
    float2 stepUv = offset * max(blurAmount, 0.0);

    float4 color = input.Sample(samplerState, uv) * 0.12;
    color += input.Sample(samplerState, uv + float2(-1.0,  0.0) * stepUv) * 0.10;
    color += input.Sample(samplerState, uv + float2( 1.0,  0.0) * stepUv) * 0.10;
    color += input.Sample(samplerState, uv + float2( 0.0, -1.0) * stepUv) * 0.10;
    color += input.Sample(samplerState, uv + float2( 0.0,  1.0) * stepUv) * 0.10;
    color += input.Sample(samplerState, uv + float2(-1.0, -1.0) * stepUv) * 0.075;
    color += input.Sample(samplerState, uv + float2( 1.0, -1.0) * stepUv) * 0.075;
    color += input.Sample(samplerState, uv + float2(-1.0,  1.0) * stepUv) * 0.075;
    color += input.Sample(samplerState, uv + float2( 1.0,  1.0) * stepUv) * 0.075;
    color += input.Sample(samplerState, uv + float2(-2.0,  0.0) * stepUv) * 0.045;
    color += input.Sample(samplerState, uv + float2( 2.0,  0.0) * stepUv) * 0.045;
    color += input.Sample(samplerState, uv + float2( 0.0, -2.0) * stepUv) * 0.045;
    color += input.Sample(samplerState, uv + float2( 0.0,  2.0) * stepUv) * 0.045;

    float mask = SmoothGradient(uv.y);
    float darken = 0.32 * mask;
    color.rgb *= (1.0 - darken);
    color.a *= transparency * mask;
    return color;
}
