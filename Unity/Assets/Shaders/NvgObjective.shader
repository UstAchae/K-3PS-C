// The night-vision device's objective lens, before the picture reaches the tube: a wide-open lens focused far away,
// so what is near (the hands, the gun, its sights) is out of focus, the more the nearer it is: spread over a disc as
// wide, seen from the lens, as the aperture over its distance from it; here the blur's radius is a limit's share,
// half of it at a set distance from the lens, more the nearer and less the further, so that it grows along the gun.
// Made of smooth Gaussian blurs at a few sizes, at a quarter of the frame's size and smaller (nothing is gathered pixel
// by pixel, so nothing shows as noise or blocks), in two layers, as a lens makes them: what is near (blurred by more
// than a quarter-size pixel and a half) is cut out with how much of each pixel it covers and blurred as wide as it is
// spread (a disc of radius r as a Gaussian of spread r / 2), and laid over the rest (premultiplied: over), which keeps
// its own small blur. What lies behind a near thing's soft edge is seen sharp, only less of it; what its outline hid
// is filled in from the scene about it (the layers of what is not near), not the frame blurred whole, so that the
// scene beside it stays as it is however wide the blur. Step 3 of the eyepieces' picture (the steps are at the head of NightVisionComposite.cs), on the
// light that reaches the tube (step 2: NvgIrLight.shader pass 1).
// Pass 0: the frame at a quarter of its size, premultiplied by what of it is neither near nor a magnified sight's lens
//         (alpha): the scene behind what is near.
// Pass 1: a Gaussian across (_Step: one texel across), its spread a texel and a half. Pass 2: the same, up.
// Pass 3: the frame, with what is near laid over it.
// Pass 4: what is near, at a quarter of the size, premultiplied by how much of each of the sixteen pixels under it it
//         covers. Pass 5: its blur radius the same way (red: radius times cover, green: cover).
// Pass 6: how blurred each part of the frame is (0 to 1), at a quarter of the size.
// Pass 8: tritium vials' occlusion over their discs, an atlas tile each. Pass 9: their discs, added
//         (NightVisionTritium.cs, NightVisionTrace.cs).
// Pass 10: a magnified sight's lens into a mask (NightVisionScope.cs).
// Pass 7: the lenses' picture once the game's or BRNVG's filter has made it, its grain eased where the frame was
//         blurred (_Mask): the filter's noise, one pixel fine, made a blurred thing look textured and its edge hard.
Shader "Justachae/NVG/Objective" {
 Properties { _MainTex ("source", 2D) = "black" {} }
 CGINCLUDE
  #include "UnityCG.cginc"
  sampler2D _MainTex, _Mask;
  // The levels share one sampler: there are more of them than a pass may have samplers.
  Texture2D _All0, _All1, _All2, _All3, _All4, _Near0, _Near1, _Near2, _Near3, _Near4, _NearBlur;
  SamplerState sampler_linear_clamp;
  float4 _MainTex_TexelSize;
  float2 _Step;
  UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
  // x: the distance from the lens at which the blur is half its limit (m), or for a sight's picture its focus; y: 0, or
  // for a sight's picture its objective's radius in quarter-size pixels per radian; z: how far the objective is in
  // front of the camera (m); w: the blur's limit, its radius in quarter-size pixels.
  float4 _Lens;
  float2 _TanHalf;   // the tangents of half the frame's field across and up

  // Where a magnified sight's lens is (_scope, NightVisionScope.cs), its light already cut to what reaches the tube
  // (NvgIrLight.shader pass 1). Its picture is at infinity and sharp: it is no part of what is near (its blur 0), it is
  // laid back sharp, and its rim (the eyepiece's edge, as near as the rest of the sight) is blurred over its edge as
  // anything near is over what lies behind. The picture shows only within the sight's own field, so it is kept out of
  // the layers of everything blurred (pass 0) too: what lies behind the rim's soft edge, out of the lens, is the rim and
  // the scene about it, not the sight's picture spread out (a bright laser in it glowed as a bulge over the rim).
  Texture2D _ScopeLens;
  float _ScopeOn;
  bool InLens(float2 uv) {
   return _ScopeOn > 0.5 && _ScopeLens.SampleLevel(sampler_linear_clamp, uv, 0).r > 0.5;
  }
  // A layer of the scene behind (pass 0's, premultiplied) as its light; otherwise where none of it is about.
  float3 Behind(float4 c, float3 otherwise) {
   return c.a > 1e-3 ? c.rgb / c.a : otherwise;
  }

  float Blur(float2 uv) {
   if (InLens(uv))
    return 0;
   float z = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
   float2 ray = (uv * 2 - 1) * _TanHalf;
   float fromLens = max(z * sqrt(1 + dot(ray, ray)) - _Lens.z, 0.01);
   // A magnified sight's own picture (_Lens.y > 0, NightVisionScopeTritium.cs): its objective, focused at _Lens.x,
   // spreads a point at fromLens over a disc of radius its own radius times |1/fromLens - 1/_Lens.x| in angle; _Lens.y
   // is that radius in quarter-size pixels per unit of the difference, _Lens.w the most it is let grow.
   if (_Lens.y > 0)
    return min(_Lens.y * abs(1 / fromLens - 1 / _Lens.x), _Lens.w);
   return _Lens.w * (1 - exp(-0.6931472 * _Lens.x / fromLens));
  }

  // How much of a pixel is near: none blurred less than a quarter-size pixel and a half, all from three.
  float Near(float blur) { return saturate(blur / 1.5 - 1); }

  // The levels spread over 1.5, 3, 6, 12, 24 quarter-size pixels; a disc's radius is about twice a Gaussian's spread.
  float4 Level(float radius, float4 l0, float4 l1, float4 l2, float4 l3, float4 l4) {
   float level = log2(max(radius, 1e-4) / 3);
   float4 c = lerp(l0, l1, saturate(level));
   c = lerp(c, l2, saturate(level - 1));
   c = lerp(c, l3, saturate(level - 2));
   return lerp(c, l4, saturate(level - 3));
  }

  float4 Gaussian(float2 uv) {
   const float w[4] = { 0.266, 0.213, 0.110, 0.036 };
   float2 d = _Step * _MainTex_TexelSize.xy;
   float4 sum = tex2D(_MainTex, uv) * w[0];
   for (int k = 1; k < 4; k++)
    sum += (tex2D(_MainTex, uv + d * k) + tex2D(_MainTex, uv - d * k)) * w[k];
   return sum;
  }
 ENDCG
 SubShader {
  Cull Off ZWrite Off ZTest Always
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   float4 frag (v2f_img i) : SV_Target {
    // Four bilinear taps, each the mean of four, cover the sixteen pixels of the frame under this one.
    float2 texel = _MainTex_TexelSize.xy;
    float4 sum = 0;
    for (int y = 0; y < 2; y++)
     for (int x = 0; x < 2; x++) {
      float2 uv = i.uv + (float2(x, y) * 2 - 1) * texel;
      float behind = InLens(uv) ? 0 : 1 - Near(Blur(uv));
      sum += float4(tex2D(_MainTex, uv).rgb * behind, behind);
     }
    return sum / 4;
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   float4 frag (v2f_img i) : SV_Target { return Gaussian(i.uv); }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   float4 frag (v2f_img i) : SV_Target { return Gaussian(i.uv); }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.0
   float4 frag (v2f_img i) : SV_Target {
    float4 frame = tex2D(_MainTex, i.uv);
    float own = Blur(i.uv);
    // What is near around here, as wide as it is spread there (its radius averaged over what of it is about).
    float2 nearBlur = _NearBlur.Sample(sampler_linear_clamp, i.uv).rg;
    float radius = max(nearBlur.g > 1e-4 ? nearBlur.r / nearBlur.g : 0, 3);
    float4 near = Level(radius, _Near0.Sample(sampler_linear_clamp, i.uv), _Near1.Sample(sampler_linear_clamp, i.uv),
     _Near2.Sample(sampler_linear_clamp, i.uv), _Near3.Sample(sampler_linear_clamp, i.uv), _Near4.Sample(sampler_linear_clamp, i.uv));
    // What lies behind: the scene as the frame has it, what is not near blurred by its own small amount; where this
    // pixel is itself near, what it hides, from the scene about it as far as the near thing is spread.
    float4 all0 = _All0.Sample(sampler_linear_clamp, i.uv);
    float3 a0 = Behind(all0, frame.rgb);
    float3 still = lerp(frame.rgb, a0, saturate(min(own, 3) / 3));
    float3 hidden = Behind(Level(radius, all0, _All1.Sample(sampler_linear_clamp, i.uv), _All2.Sample(sampler_linear_clamp, i.uv),
     _All3.Sample(sampler_linear_clamp, i.uv), _All4.Sample(sampler_linear_clamp, i.uv)), a0);
    // A magnified sight's lens: its own picture, sharp.
    float3 behind = InLens(i.uv) ? frame.rgb : lerp(still, hidden, Near(own));
    // The near thing's light, spread, over what lies behind as much as it covers.
    float cover = saturate(near.a);
    float3 light = near.a > 1e-4 ? near.rgb / near.a : behind;
    return float4(lerp(behind, light, cover), frame.a);
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.0
   float4 frag (v2f_img i) : SV_Target {
    float4 sum = 0;
    for (int y = 0; y < 4; y++)
     for (int x = 0; x < 4; x++) {
      float2 uv = i.uv + (float2(x, y) - 1.5) * _MainTex_TexelSize.xy;
      sum += float4(tex2D(_MainTex, uv).rgb, 1) * Near(Blur(uv));
     }
    return sum / 16;
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.0
   float4 frag (v2f_img i) : SV_Target {
    float2 sum = 0;
    for (int y = 0; y < 4; y++)
     for (int x = 0; x < 4; x++) {
      float blur = Blur(i.uv + (float2(x, y) - 1.5) * _MainTex_TexelSize.xy);
      sum += float2(blur, 1) * Near(blur);
     }
    return float4(sum / 16, 0, 0);
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.0
   float4 frag (v2f_img i) : SV_Target {
    float2 nearBlur = _NearBlur.Sample(sampler_linear_clamp, i.uv).rg;
    float radius = max(nearBlur.g > 1e-4 ? nearBlur.r / nearBlur.g : 0, 3);
    float4 near = Level(radius, _Near0.Sample(sampler_linear_clamp, i.uv), _Near1.Sample(sampler_linear_clamp, i.uv),
     _Near2.Sample(sampler_linear_clamp, i.uv), _Near3.Sample(sampler_linear_clamp, i.uv), _Near4.Sample(sampler_linear_clamp, i.uv));
    return max(saturate(near.a), saturate(Blur(i.uv) / 3));
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.0
   float4 frag (v2f_img i) : SV_Target {
    float4 picture = tex2D(_MainTex, i.uv);
    float blurred = tex2D(_Mask, i.uv).r;
    if (blurred < 0.01)
     return picture;
    // Sixteen bilinear taps, each the mean of four, over the five by five pixels about this one.
    float3 soft = 0;
    for (int y = 0; y < 4; y++)
     for (int x = 0; x < 4; x++)
      soft += tex2D(_MainTex, i.uv + (float2(x, y) - 1.5) * _MainTex_TexelSize.xy).rgb;
    return float4(lerp(picture.rgb, soft / 16, blurred), picture.a);
   }
   ENDCG
  }
  // Passes 8 and 9 share these: the vials, the gun in hand's parts, and how the light of a vial gets to the aperture.
  CGINCLUDE
   float4 _Vials[16];     // xy the vial's place (0 to 1), z its disc's radius (pixels of the target), w its light
   float4 _VialTint[16];  // rgb its colour, a its depth (along the view, m)
   float4 _VialMasks[16]; // x, y: which traced parts (low and high sixteen) lie along its light
   float _VialTrace[16];  // 1 where the vial is on the gun in hand, whose parts are traced
   float _VialCount;
   float4 _Target;        // xy the target's size in pixels, z the objective's aperture radius (m)
  ENDCG
  // Pass 8: each vial's occlusion over its disc, into a tile of an atlas (4 x 4 tiles, one a vial, each 64 x 64 over
  // the disc's -1..1 each way). A disc's every point is lit through one point of the aperture, across from it (the lens
  // turns the picture over); how much of the vial's light gets there depends only on where on the aperture that is,
  // not on how large the disc is drawn, so it is worked out at a set size and looked up as the disc is drawn.
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 4.5
   // How far behind what the camera sees there a point is (m): above 0 it is hidden from the camera.
   float Behind(float3 p) {
    float2 uv = p.xy / p.z / _TanHalf * 0.5 + 0.5;
    if (any(uv < 0) || any(uv > 1))
     return -1;
    return p.z - LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(uv, 0, 0)));
   }
   // For vials not on the gun in hand (lying about, or held by others, a metre or more off, where the camera's view of
   // them is the objective's): whether the ray from the vial to a point of the aperture gets there, from the camera's
   // depth. A point of it hidden from the camera is not yet blocked (a thin blade hides a whole wedge of space behind
   // it): the ray is blocked where it passes out through a surface, from behind it to in front of it, found between
   // two steps and narrowed down by halves; or if it is still behind something at the lens itself.
   static const float ScopeReach = 0.6;             // how far ahead of the camera a sight's tube is looked for (m)
   float Ray(float3 vial, float3 aperture) {
    const int Steps = 16;
    float3 last = vial;
    float lastBehind = -1;
    [loop] for (int s = 1; s <= Steps; s++) {
     float3 p = lerp(vial, aperture, s / (float)Steps);
     // A magnified sight before the objective is a closed tube: light that would cross it near the objective goes
     // through the sight, into its own picture (NightVisionScopeTritium.cs), not straight to the objective.
     if (p.z < ScopeReach && InLens(p.xy / p.z / _TanHalf * 0.5 + 0.5))
      return 0;
     float behind = Behind(p);
     if (vial.z - p.z < 0.012)
      behind = -1;                                  // the vial's own post or notch, round it
     if (lastBehind > 0 && behind <= 0) {
      float3 a = last, b = p;
      [unroll] for (int h = 0; h < 5; h++) {
       float3 m = (a + b) * 0.5;
       if (Behind(m) > 0) a = m; else b = m;
      }
      if (Behind(a) < 0.006)
       return 0;
     }
     last = p;
     lastBehind = behind;
    }
    return lastBehind > 0 ? 0 : 1;
   }
   // The gun in hand's parts, each a hierarchy of boxes over its triangles in its own frame (NightVisionTrace.cs).
   struct TraceNode { float3 lo; int left; float3 hi; int count; };
   struct TraceTri { float3 a; float3 b; float3 c; };
   struct TraceInstance { float4 row0; float4 row1; float4 row2; int root; int3 pad; };
   StructuredBuffer<TraceNode> _TraceNodes;
   StructuredBuffer<TraceTri> _TraceTris;
   StructuredBuffer<TraceInstance> _TraceInstances;
   int _TraceCount;
   bool BoxHit(float3 o, float3 inv, float3 lo, float3 hi) {
    float3 t0 = (lo - o) * inv, t1 = (hi - o) * inv;
    float3 near = min(t0, t1), far = max(t0, t1);
    return max(max(near.x, near.y), max(near.z, 0)) <= min(min(far.x, far.y), min(far.z, 1));
   }
   bool TriHit(float3 o, float3 d, TraceTri t, float from) {
    float3 e1 = t.b - t.a, e2 = t.c - t.a;
    float3 p = cross(d, e2);
    float det = dot(e1, p);
    if (abs(det) < 1e-14)
     return false;
    float inv = 1 / det;
    float3 s = o - t.a;
    float u = dot(s, p) * inv;
    if (u < 0 || u > 1)
     return false;
    float3 q = cross(s, e1);
    float v = dot(d, q) * inv;
    if (v < 0 || u + v > 1)
     return false;
    float along = dot(e2, q) * inv;
    return along > from && along < 1;
   }
   // Whether the straight line from the vial to a point of the aperture (both in the view's frame) meets none of the
   // parts along its light (mask), past the vial's own post or notch round it.
   float Traced(float3 vial, float3 aperture, uint mask) {
    float from = 0.012 / length(aperture - vial);
    float3 way = aperture - vial;
    [loop] for (int k = 0; k < _TraceCount; k++) {
     if (((mask >> k) & 1u) == 0)
      continue;
     TraceInstance part = _TraceInstances[k];
     float3 o = float3(dot(part.row0, float4(vial, 1)), dot(part.row1, float4(vial, 1)), dot(part.row2, float4(vial, 1)));
     float3 d = float3(dot(part.row0.xyz, way), dot(part.row1.xyz, way), dot(part.row2.xyz, way));
     float3 inv = 1 / d;
     int stack[32];
     int top = 0;
     stack[top++] = part.root;
     [loop] while (top > 0) {
      TraceNode node = _TraceNodes[stack[--top]];
      if (!BoxHit(o, inv, node.lo, node.hi))
       continue;
      if (node.count > 0) {
       [loop] for (int t = 0; t < node.count; t++)
        if (TriHit(o, d, _TraceTris[node.left + t], from))
         return 0;
      } else if (top < 31) {
       stack[top++] = node.left;
       stack[top++] = node.left + 1;
      }
     }
    }
    return 1;
   }
   // How much of the vial sends light to this point of the aperture: the vial is not a point but a glowing face a
   // couple of millimetres across, so an edge that cuts its disc does so softly, over as much as the vial is wide;
   // sixteen points spread evenly over that face (a sunflower spiral).
   float4 frag (v2f_img i) : SV_Target {
    float2 tile = floor(i.uv * 4);
    int k = (int)(tile.y * 4 + tile.x);
    if (k >= (int)_VialCount)
     return 1;
    float2 across = frac(i.uv * 4) * 2 - 1;
    float4 v = _Vials[k];
    float depth = _VialTint[k].a;
    float3 vial = float3((v.xy * 2 - 1) * _TanHalf * depth, depth);
    float3 aperture = float3(-across * _Target.z, _Lens.z);
    bool traced = _VialTrace[k] > 0.5;
    uint mask = (uint)_VialMasks[k].x | ((uint)_VialMasks[k].y << 16);
    const float face = 0.001;                       // the vial's face's radius (m)
    float through = 0;
    [loop] for (int s = 0; s < 16; s++) {
     float r = face * sqrt((s + 0.5) / 16);
     float a = s * 2.3999632;
     float3 at = vial + float3(r * cos(a), r * sin(a), 0);
     through += traced ? Traced(at, aperture, mask) : Ray(at, aperture);
    }
    return through / 16;
   }
   ENDCG
  }
  // Pass 9: the vials' discs, added, each drawn over a square of its own three radii wide (NightVisionTritium.cs): a
  // disc as wide as the aperture spreads it, its edge a little soft, a little brighter toward the middle (the lens's
  // own aberration), with a faint glow about it as the tube spreads a bright light; as much of it lit as its tile of
  // the occlusion atlas has it.
  Pass {
   Blend One One
   ZTest Always ZWrite Off Cull Off
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   sampler2D _Occlusion;
   struct appdata { float4 vertex : POSITION; float4 uv : TEXCOORD0; };
   struct v2f { float4 pos : SV_POSITION; float3 uv : TEXCOORD0; };
   v2f vert (appdata a) {
    v2f o;
    o.pos = UnityObjectToClipPos(a.vertex);
    o.uv = a.uv.xyz;
    return o;
   }
   float4 frag (v2f i) : SV_Target {
    int k = (int)(i.uv.z + 0.5);
    float4 v = _Vials[k];
    float r = max(v.z, 1);
    float2 off = i.uv.xy;                           // across the disc, in its radii
    float d = length(off);
    if (d > 3)
     return 0;
    float2 across = d > 1 ? off / d : off;
    float2 local = clamp(across * 0.5 + 0.5, 0.5 / 64, 1 - 0.5 / 64);
    float through = tex2D(_Occlusion, (float2(k % 4, k / 4) + local) / 4).r;
    float disc = saturate((1 - d) / max(0.12, 1.5 / r) + 0.5);
    float core = 1 + 0.35 * saturate(1 - d * d);
    float glow = 0.12 * exp(-max(d - 1, 0) / 0.25);
    return float4(_VialTint[k].rgb * v.w * through * (disc * core + glow), 0);
   }
   ENDCG
  }
  // Pass 10: a sight's lens drawn into a mask, where the camera sees it: the lens the game shows the sight's picture
  // on may be its front one, well ahead of the eyepiece, and as the gun sways the eyepiece's rim, nearer, comes over
  // it; there the frame shows the rim, and the mask leaves it (the lens's own depth against the camera's).
  Pass {
   ZTest Always ZWrite Off Cull Off
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   struct v2f { float4 pos : SV_POSITION; float3 view : TEXCOORD0; };
   v2f vert (float4 vertex : POSITION) {
    v2f o;
    o.pos = UnityObjectToClipPos(vertex);
    float3 v = UnityObjectToViewPos(vertex);
    o.view = float3(v.xy, -v.z);                    // x right, y up, z ahead
    return o;
   }
   float4 frag (v2f i) : SV_Target {
    float2 uv = i.view.xy / i.view.z / _TanHalf * 0.5 + 0.5;
    float scene = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(uv, 0, 0)));
    float along = i.view.z * sqrt(1 + dot(i.view.xy / i.view.z, i.view.xy / i.view.z));
    float sceneAlong = scene * sqrt(1 + dot(i.view.xy / i.view.z, i.view.xy / i.view.z));
    if (sceneAlong < along - 0.004)
     discard;
    return 1;
   }
   ENDCG
  }
  }
}
