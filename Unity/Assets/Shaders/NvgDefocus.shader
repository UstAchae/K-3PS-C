// The objective's defocus as a lens makes it (NightVisionDefocus.cs): a point d from a lens of aperture D focused at s
// spread over a disc of even light, diameter D·|1/d − 1/s| seen from the lens; the light moved, not made.
// _DefocusLens: x 1/s; y a disc's radius in quarter-size pixels per unit of |1/d − 1/s| (half the aperture over the
// quarter-size pixel's angle); z how far the objective is ahead of the camera; w the largest radius drawn.
// The player's own: nearer in the frame than in the scene drawn without them (_BehindDepth, distance along the view).
// Pass 0: (in the second camera) its depth, as distance along the view.
// Pass 1: what is the player's own, at a quarter of the size, premultiplied by how much of the sixteen pixels it covers.
// Pass 2: the discs' radii: red the player's own (their mean), green the world's (what lies behind them where they are).
// Pass 3: the world spread more than a quarter-size pixel, premultiplied the same way.
// Pass 4: each quarter-size pixel of a layer drawn as its disc (procedural, six vertices a pixel), added.
// Pass 5: the frame as the lens lays it on the tube: the player's own spread over the near world spread over the sharp
//         rest, which where the player's own were is the scene behind them.
// Pass 6: how much of each part of the frame is spread (0 to 1), for the tube's grain (EaseGrain).
Shader "Justachae/NVG/Defocus" {
 Properties { _MainTex ("frame", 2D) = "black" {} }
 CGINCLUDE
  #include "UnityCG.cginc"
  sampler2D _MainTex;
  float4 _MainTex_TexelSize;
  UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
  Texture2D _Behind, _BehindDepth, _ScopeLens, _NearAcc, _WorldAcc, _Src, _Coc;
  SamplerState sampler_linear_clamp, sampler_point_clamp;
  float _ScopeOn;
  float4 _DefocusLens;
  float2 _TanHalf;
  float _CocChannel;
  float4 _Grid;   // the quarter-size grid's width and height; z −1 where a render texture's up is flipped

  bool InLens(float2 uv) {
   return _ScopeOn > 0.5 && _ScopeLens.SampleLevel(sampler_linear_clamp, uv, 0).r > 0.5;
  }
  float ZFrame(float2 uv) { return LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv)); }
  float ZBehind(float2 uv) { return _BehindDepth.SampleLevel(sampler_point_clamp, uv, 0).r; }
  // From the objective along the line of sight through uv, for a point z along the view.
  float FromLens(float z, float2 uv) {
   float2 ray = (uv * 2 - 1) * _TanHalf;
   return z * sqrt(1 + dot(ray, ray)) - _DefocusLens.z;
  }
  float Radius(float d) { return d <= 0.003 ? 0 : min(_DefocusLens.y * abs(1 / d - _DefocusLens.x), _DefocusLens.w); }
  // The player's own: nearer here than in the scene drawn without them.
  bool Own(float2 uv, float z) { return z < ZBehind(uv) - max(0.01, 0.02 * z); }
  // Seen through the objective: in front of it.
  bool Seen(float2 uv, float z) { return FromLens(z, uv) > 0.003; }
 ENDCG
 SubShader {
  Cull Off ZWrite Off ZTest Always
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   float4 frag (v2f_img i) : SV_Target { return ZFrame(i.uv); }
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
      if (InLens(uv))
       continue;
      float z = ZFrame(uv);
      if (Own(uv, z) && Seen(uv, z))
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
    float own = 0, ownCount = 0, world = 0;
    for (int y = 0; y < 4; y++)
     for (int x = 0; x < 4; x++) {
      float2 uv = i.uv + (float2(x, y) - 1.5) * t;
      if (InLens(uv))
       continue;
      float z = ZFrame(uv);
      bool mine = Own(uv, z);
      if (mine && Seen(uv, z)) {
       own += Radius(FromLens(z, uv));
       ownCount += 1;
      }
      world += Radius(FromLens(mine ? ZBehind(uv) : z, uv));
     }
    return float4(ownCount > 0 ? own / ownCount : 0, world / 16, 0, 0);
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
      if (InLens(uv))
       continue;
      float z = ZFrame(uv);
      bool mine = Own(uv, z);
      if (Radius(FromLens(mine ? ZBehind(uv) : z, uv)) <= 1)
       continue;
      float3 c = mine ? _Behind.SampleLevel(sampler_linear_clamp, uv, 0).rgb : tex2D(_MainTex, uv).rgb;
      sum += float4(c, 1);
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
    float2 radii = _Coc.SampleLevel(sampler_point_clamp, uv, 0).rg;
    float r = max(_CocChannel > 0.5 ? radii.y : radii.x, 0.5);
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
    float3 sharp = f.rgb;
    if (!InLens(i.uv) && Own(i.uv, ZFrame(i.uv)))
     sharp = _Behind.SampleLevel(sampler_linear_clamp, i.uv, 0).rgb;
    float4 n = _NearAcc.SampleLevel(sampler_linear_clamp, i.uv, 0);
    if (n.a > 1)
     n /= n.a;
    float4 w = _WorldAcc.SampleLevel(sampler_linear_clamp, i.uv, 0);
    if (w.a > 1)
     w /= w.a;
    return float4(n.rgb + (1 - n.a) * (w.rgb + (1 - w.a) * sharp), f.a);
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   float4 frag (v2f_img i) : SV_Target {
    float n = _NearAcc.SampleLevel(sampler_linear_clamp, i.uv, 0).a;
    float w = _WorldAcc.SampleLevel(sampler_linear_clamp, i.uv, 0).a;
    return saturate(max(n, w));
   }
   ENDCG
  }
 }
}
