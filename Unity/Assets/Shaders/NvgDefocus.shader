// The objective's defocus (NightVisionDefocus.cs): a point d from a lens of aperture D focused at s spread over a disc
// of even light, diameter D·|1/d − 1/s| seen from the lens.
// _DefocusLens: x 1/s; y a disc's radius in quarter-size pixels per unit of |1/d − 1/s| (half the aperture over the
// quarter-size pixel's angle); z how far the objective is ahead of the camera; w the largest radius drawn.
// What is near: spread by more than a quarter-size pixel. A magnified sight's lens (its picture at infinity) is not.
// Pass 1: what is near, at a quarter of the size, premultiplied by how much of the sixteen pixels under it it covers.
// Pass 2: its discs' radius (the mean of what is near among the sixteen).
// Pass 4: each quarter-size pixel drawn as its disc (procedural, six vertices a pixel), added.
// Pass 5: the frame with what is near laid over it solid: its spread colour where it is and out to where its discs
//         reach, fading there as they thin out (twice their cover: solid to the edge of the thing itself).
// Pass 6: how much of each part of the frame is spread (0 to 1), for the tube's grain (EaseGrain).
// Passes 0 and 3 are left empty, so the others keep their numbers.
Shader "Justachae/NVG/Defocus" {
 Properties { _MainTex ("frame", 2D) = "black" {} }
 CGINCLUDE
  #include "UnityCG.cginc"
  sampler2D _MainTex;
  float4 _MainTex_TexelSize;
  UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
  Texture2D _ScopeLens, _NearAcc, _Src, _Coc;
  SamplerState sampler_linear_clamp, sampler_point_clamp;
  float _ScopeOn;
  float4 _DefocusLens;
  float2 _TanHalf;
  float4 _Grid;   // the quarter-size grid's width and height; z −1 where a render texture's up is flipped

  bool InLens(float2 uv) {
   return _ScopeOn > 0.5 && _ScopeLens.SampleLevel(sampler_linear_clamp, uv, 0).r > 0.5;
  }
  // A disc's radius (quarter-size pixels) for what the frame shows at uv: none through a sight's lens. What the frame
  // has nearer than a centimetre from the objective, or behind it (the objective's place is the model's far end, an
  // estimate; a gun's rail came that near when aimed and was left sharp), is spread as if a centimetre off: the most.
  float Radius(float2 uv) {
   if (InLens(uv))
    return 0;
   float z = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
   float2 ray = (uv * 2 - 1) * _TanHalf;
   float d = z * sqrt(1 + dot(ray, ray)) - _DefocusLens.z;
   return min(_DefocusLens.y * abs(1 / max(d, 0.01) - _DefocusLens.x), _DefocusLens.w);
  }
  bool IsNear(float r) { return r > 1; }
 ENDCG
 SubShader {
  Cull Off ZWrite Off ZTest Always
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   float4 frag (v2f_img i) : SV_Target { return 0; }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.5
   float4 frag (v2f_img i) : SV_Target {
    float2 t = _MainTex_TexelSize.xy;
    float4 sum = 0;
    for (int y = 0; y < 4; y++)
     for (int x = 0; x < 4; x++) {
      float2 uv = i.uv + (float2(x, y) - 1.5) * t;
      if (IsNear(Radius(uv)))
       sum += float4(tex2D(_MainTex, uv).rgb, 1);
     }
    return sum / 16;
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.5
   float4 frag (v2f_img i) : SV_Target {
    float2 t = _MainTex_TexelSize.xy;
    float sum = 0, count = 0;
    for (int y = 0; y < 4; y++)
     for (int x = 0; x < 4; x++) {
      float r = Radius(i.uv + (float2(x, y) - 1.5) * t);
      if (IsNear(r)) {
       sum += r;
       count += 1;
      }
     }
    return count > 0 ? sum / count : 0;
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   float4 frag (v2f_img i) : SV_Target { return 0; }
   ENDCG
  }
  Pass {
   Blend One One
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #pragma target 3.5
   struct v2f {
    float4 pos : SV_POSITION;
    float2 off : TEXCOORD0;
    nointerpolation float4 col : TEXCOORD1;
    nointerpolation float r : TEXCOORD2;
   };
   static const float2 Corners[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1) };
   v2f vert (uint id : SV_VertexID) {
    v2f o;
    uint quad = id / 6, corner = id % 6;
    uint w = (uint)_Grid.x;
    float2 cell = float2(quad % w, quad / w) + 0.5;
    float2 uv = cell / _Grid.xy;
    float4 c = _Src.SampleLevel(sampler_point_clamp, uv, 0);
    float r = max(_Coc.SampleLevel(sampler_point_clamp, uv, 0).r, 0.5);
    float2 off = Corners[corner] * (r + 1);
    float2 p = (cell + off) / _Grid.xy * 2 - 1;
    p.y *= _Grid.z;
    o.pos = c.a > 1e-5 ? float4(p, 0.5, 1) : float4(2, 2, 2, 1);
    o.off = off;
    o.col = c;
    o.r = r;
    return o;
   }
   // The area under a disc of radius r whose edge falls off over a pixel (as drawn below), so the light is kept.
   float Area(float r) {
    float a = max(r - 0.5, 0), b = r + 0.5;
    return UNITY_PI * a * a + 2 * UNITY_PI * (b * b * b / 6 - b * a * a / 2 + a * a * a / 3);
   }
   float4 frag (v2f i) : SV_Target {
    float cover = saturate(i.r + 0.5 - length(i.off));
    return i.col * (cover / Area(i.r));
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.5
   float4 frag (v2f_img i) : SV_Target {
    float4 f = tex2D(_MainTex, i.uv);
    float4 n = _NearAcc.SampleLevel(sampler_linear_clamp, i.uv, 0);
    if (n.a < 1e-4)
     return f;
    float3 spread = n.rgb / n.a;
    // Where the thing itself is, its spread colour only (the sharp thing under it is not seen); out from it, over the
    // sharp rest as its discs thin out.
    float solid = IsNear(Radius(i.uv)) ? 1 : saturate(2 * n.a);
    return float4(lerp(f.rgb, spread, solid), f.a);
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   float4 frag (v2f_img i) : SV_Target { return saturate(2 * _NearAcc.SampleLevel(sampler_linear_clamp, i.uv, 0).a); }
   ENDCG
  }
 }
}
