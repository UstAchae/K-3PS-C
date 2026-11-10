// The objective's defocus (NightVisionDefocus.cs): a point d from a lens of aperture D focused at s spread over a disc
// of even light, diameter D·|1/d − 1/s| seen from the lens; nearer, easing toward a most (the lens's own grows without
// end, and a thing a few centimetres off is spread over the whole field).
// _DefocusLens: x 1/s; y a disc's radius in quarter-size pixels per unit of |1/d − 1/s| (half the aperture over the
// quarter-size pixel's angle); z how far the objective is ahead of the camera; w the radius it eases toward.
// What is near: spread by more than a quarter-size pixel. A magnified sight's lens (its picture at infinity) is not; nor
// what is seen through a sight's window or any opening: there the frame has what lies beyond.
// Pass 1: what is near, at a quarter of the size, premultiplied by how much of the sixteen pixels under it it covers.
// Pass 2: its discs' radius: red the radius times the cover, green the cover (halved as the levels are, the radius
//         stays the cover's mean).
// Pass 3: what is not near, the same way.
// Pass 4: each pixel of a level drawn as its disc (procedural, six vertices a pixel), added; only radii in its band.
// Pass 5: the frame as the lens lays it on the tube: what is near, spread, over what lies behind it, as solid as
//         its discs cover thickly, eased from a third (a thin thing gone). Behind a near thing the frame has only the
//         thing itself: what is about it, filled in from its edge inward (finest first), stands for what lies behind it.
// Pass 6: how much of each part of the frame is spread (0 to 1), for the tube's grain (EaseGrain).
// Pass 7: a level halved: four bilinear taps a texel apart, the four by four under it weighted as a tent, smoother
//         from frame to frame than the two by two alone.
// Pass 0 is left empty, so the others keep their numbers.
Shader "Justachae/NVG/Defocus" {
 Properties { _MainTex ("frame", 2D) = "black" {} }
 CGINCLUDE
  #include "UnityCG.cginc"
  sampler2D _MainTex;
  float4 _MainTex_TexelSize;
  UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
  Texture2D _ScopeLens, _Src, _Coc;
  Texture2D _NearAcc0, _NearAcc1, _NearAcc2, _NearAcc3, _Far0, _Far1, _Far2, _Far3;
  SamplerState sampler_linear_clamp, sampler_point_clamp;
  float _ScopeOn;
  float4 _DefocusLens;
  float2 _TanHalf;
  float4 _Grid;   // a level's width and height; z −1 where a render texture's up is flipped; w its pixel in quarter-size ones
  float2 _Band;   // the radii (quarter-size pixels) a level draws: above x, up to y

  bool InLens(float2 uv) {
   return _ScopeOn > 0.5 && _ScopeLens.SampleLevel(sampler_linear_clamp, uv, 0).r > 0.5;
  }
  // A disc's radius (quarter-size pixels) for what the frame shows at uv: none through a sight's lens.
  float Radius(float2 uv) {
   if (InLens(uv))
    return 0;
   float z = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
   float2 ray = (uv * 2 - 1) * _TanHalf;
   float d = max(z * sqrt(1 + dot(ray, ray)) - _DefocusLens.z, 0.001);
   float lens = _DefocusLens.y * abs(1 / d - _DefocusLens.x);
   return _DefocusLens.w * (1 - exp(-lens / _DefocusLens.w));
  }
  // How much of a point is near (spread by its disc), the rest left sharp: none below half a quarter-size pixel, all
  // from two and a half, eased between, so no line is seen where the spreading begins along a surface going away.
  float Nearness(float r) { return smoothstep(0.5, 2.5, r); }
  bool IsNear(float r) { return Nearness(r) > 0; }

  float4 Near(float2 uv) {
   float4 n = _NearAcc0.SampleLevel(sampler_linear_clamp, uv, 0) + _NearAcc1.SampleLevel(sampler_linear_clamp, uv, 0)
    + _NearAcc2.SampleLevel(sampler_linear_clamp, uv, 0) + _NearAcc3.SampleLevel(sampler_linear_clamp, uv, 0);
   return n.a > 1 ? n / n.a : n;
  }
  float4 Far(int level, float2 uv) {
   if (level <= 0) return _Far0.SampleLevel(sampler_linear_clamp, uv, 0);
   if (level == 1) return _Far1.SampleLevel(sampler_linear_clamp, uv, 0);
   if (level == 2) return _Far2.SampleLevel(sampler_linear_clamp, uv, 0);
   return _Far3.SampleLevel(sampler_linear_clamp, uv, 0);
  }
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
      sum += float4(tex2D(_MainTex, uv).rgb, 1) * Nearness(Radius(uv));
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
    float2 sum = 0;
    for (int y = 0; y < 4; y++)
     for (int x = 0; x < 4; x++) {
      float r = Radius(i.uv + (float2(x, y) - 1.5) * t);
      sum += float2(r, 1) * Nearness(r);
     }
    return float4(sum / 16, 0, 0);
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
    float4 sum = 0;
    for (int y = 0; y < 4; y++)
     for (int x = 0; x < 4; x++) {
      float2 uv = i.uv + (float2(x, y) - 1.5) * t;
      sum += float4(tex2D(_MainTex, uv).rgb, 1) * (1 - Nearness(Radius(uv)));
     }
    return sum / 16;
   }
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
    float2 coc = _Coc.SampleLevel(sampler_point_clamp, uv, 0).rg;
    float quarter = coc.y > 1e-5 ? coc.x / coc.y : 0;
    bool drawn = c.a > 1e-5 && quarter > _Band.x && quarter <= _Band.y;
    float r = max(quarter / _Grid.w, 0.5);   // in this level's pixels
    float2 off = Corners[corner] * (r + 1);
    float2 p = (cell + off) / _Grid.xy * 2 - 1;
    p.y *= _Grid.z;
    o.pos = drawn ? float4(p, 0.5, 1) : float4(2, 2, 2, 1);
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
    float4 n = Near(i.uv);
    if (n.a < 1e-4)
     return f;
    float r = Radius(i.uv);
    float3 behind = f.rgb;
    // Behind a near thing (where the frame has only the thing itself): what is about it, filled in from its edge
    // inward, from the finest level that has enough of it here to the coarsest: by its edge as sharp as the frame's
    // own just past it, so no line is left where a thin thing was; further in, blurred wider and wider.
    float near = Nearness(r);
    if (near > 0) {
     float3 fill = 0;
     float need = 1;
     for (int level = 0; level < 4 && need > 1e-3; level++) {
      float4 far = Far(level, i.uv);
      if (far.a < 1e-3)
       continue;
      float take = need * saturate(far.a * 2);
      fill += far.rgb / far.a * take;
      need -= take;
     }
     fill += (n.a > 1e-4 ? n.rgb / n.a : f.rgb) * need;
     behind = lerp(f.rgb, fill, near);
    }
    // How solid the spread thing shows, by how thickly its discs cover here (eased from 0.3 to all): a thick thing
    // solid within, its edge fading wide and soft; a thin one (its light spread thin, under a third) gone, its shape
    // with it; an opening's middle left to what lies beyond.
    float solid = smoothstep(0.3, 1, n.a);
    return float4(lerp(behind, n.rgb / n.a, solid), f.a);
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   float4 frag (v2f_img i) : SV_Target { return saturate(Near(i.uv).a); }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   float4 frag (v2f_img i) : SV_Target {
    float2 t = _MainTex_TexelSize.xy;
    return 0.25 * (tex2D(_MainTex, i.uv + float2(-t.x, -t.y)) + tex2D(_MainTex, i.uv + float2(t.x, -t.y))
     + tex2D(_MainTex, i.uv + float2(-t.x, t.y)) + tex2D(_MainTex, i.uv + float2(t.x, t.y)));
   }
   ENDCG
  }
 }
}
