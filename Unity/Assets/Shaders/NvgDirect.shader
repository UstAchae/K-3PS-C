// The device, drawn apart for each eye into G-buffers of its own, put into the main camera's: the left's on the left, the
// right's on the right, eased from one to the other across the strip in the middle, as the composite does the eyes' views.
// What is seen through a lens is left out (as NvgEyeOptics does), so the scene stays there for the lenses' picture.
// The device is drawn with a near plane of its own: its depth is turned into the main camera's here, what is nearer than
// that camera's near plane kept at it, so nothing of it is cut off and the main camera's own plane is left as it is.
Shader "Justachae/NVG/Direct" {
 CGINCLUDE
 #include "UnityCG.cginc"
 sampler2D _DirectL0, _DirectL1, _DirectL2, _DirectL3, _DirectR0, _DirectR1, _DirectR2, _DirectR3, _DirectLens0, _DirectLens1;
 UNITY_DECLARE_DEPTH_TEXTURE(_DirectLD);
 UNITY_DECLARE_DEPTH_TEXTURE(_DirectRD);
 float _DirectSplit, _DirectStrip, _DirectBehind;
 float _DirectNoEmission;   // temporary: a switch in F12
 float4x4 _EyeInvVP0, _EyeInvVP1;
 float4 _TubeField, _TubeAxis0, _TubeAxis1, _TubeAxis2, _TubeAxis3;
 float4 _DirectInset;    // how far past the lens's edge the device goes, as a share of the frame
 float4 _DirectPlanes;   // x its near plane's 1/depth, y its far plane's, z the main camera's near plane's, w its far plane's

 // 1/depth from the device's own depth.
 float inverseDepth (float raw) {
  #if !defined(UNITY_REVERSED_Z)
  raw = 1 - raw;
  #endif
  return raw * (_DirectPlanes.x - _DirectPlanes.y) + _DirectPlanes.y;
 }

 // The main camera's depth at that.
 float mainDepth (float inverse) {
  float d = saturate((inverse - _DirectPlanes.w) / (_DirectPlanes.z - _DirectPlanes.w));
  #if !defined(UNITY_REVERSED_Z)
  d = 1 - d;
  #endif
  return d;
 }

 struct v2f { float4 pos : SV_POSITION; float4 screen : TEXCOORD0; };

 v2f vert (float4 v : POSITION) {
  v2f o;
  o.pos = float4(v.xy, UNITY_NEAR_CLIP_VALUE, 1);
  o.screen = ComputeScreenPos(o.pos);
  return o;
 }

 // Well within a lens: all round this pixel too. The device goes on a few pixels past the lens's edge, under the
 // composite's glass: the main camera's frame is jittered and upscaled, the lens's edge is not, and a gap between the two
 // showed the bare scene, flickering.
 // By what the composite shows there: a lens's map as far as its tube's field reaches (NvgEyes.cginc), not the map alone,
 // which goes on past it over the housing.
 bool open (sampler2D map, float2 uv, float4 fields) {
  return dot(tex2D(map, uv) * fields, 1) > 0.01;
 }

 bool within (sampler2D map, float2 uv, float4x4 inverse) {
  float4 far = mul(inverse, float4(uv * 2 - 1, 1, 1));
  float3 ray = normalize(far.xyz / far.w);
  float4 fields = float4(smoothstep(_TubeField.x, _TubeField.y, dot(ray, _TubeAxis0.xyz)),
      smoothstep(_TubeField.x, _TubeField.y, dot(ray, _TubeAxis1.xyz)),
      smoothstep(_TubeField.x, _TubeField.y, dot(ray, _TubeAxis2.xyz)),
      smoothstep(_TubeField.x, _TubeField.y, dot(ray, _TubeAxis3.xyz)));
  if (!open(map, uv, fields)) return false;
  float2 d = _DirectInset.xy;
  return open(map, uv + float2(d.x, 0), fields) && open(map, uv - float2(d.x, 0), fields)
      && open(map, uv + float2(0, d.y), fields) && open(map, uv - float2(0, d.y), fields)
      && open(map, uv + d, fields) && open(map, uv - d, fields)
      && open(map, uv + float2(d.x, -d.y), fields) && open(map, uv + float2(-d.x, d.y), fields);
 }

 // Drawn there.
 bool shown (float raw) {
  #if defined(UNITY_REVERSED_Z)
  return raw > 0;
  #else
  return raw < 1;
  #endif
 }

 // How much of the device is here, and how far over to the right eye's it is. As the eyes' pictures were eased into each other:
 // the left eye's on the left of the line, the right's on the right, each by its share across the strip about it, and through a
 // lens that eye has nothing of the device. All that goes past the line is gone.
 float coverage (float2 uv, float rawL, float rawR, out float toRight) {
  float t = smoothstep(_DirectSplit - _DirectStrip * 0.5, _DirectSplit + _DirectStrip * 0.5, uv.x);
  toRight = t;
  bool left = t < 1 && shown(rawL) && !within(_DirectLens0, uv, _EyeInvVP0), right = t > 0 && shown(rawR) && !within(_DirectLens1, uv, _EyeInvVP1);
  if (left && right) return 1;
  if (left) { toRight = 0; return 1 - t; }
  if (right) { toRight = 1; return t; }
  return 0;
 }

 // A half fading away is kept whole at some pixels and left out at the others, more of them left out the further it fades. Mixed
 // into the G-buffer it had no depth of its own: the lights took it for what lay behind, and the strip ended in a hard edge.
 bool kept (float2 pixel, float c) {
  if (c >= 1) return true;
  if (c <= 0) return false;
  static const float grid[16] = { 0.5, 8.5, 2.5, 10.5, 12.5, 4.5, 14.5, 6.5, 3.5, 11.5, 1.5, 9.5, 15.5, 7.5, 13.5, 5.5 };
  int x = (int)fmod(pixel.x, 4), y = (int)fmod(pixel.y, 4);
  return c * 16 > grid[y * 4 + x];
 }

 // Whether this pixel has the device, and whose: each pixel one eye's whole, more of them the other's across the strip. Evened over
 // the pattern once all is lit (pass 4), that is the two eyes' lit pictures eased into each other, as the eyes' cameras were.
 bool pick (float2 pixel, float2 uv, float rawL, float rawR, out float toRight) {
  float c = coverage(uv, rawL, rawR, toRight);
  if (c <= 0)
   return false;
  if (toRight > 0 && toRight < 1) {
   toRight = kept(pixel, toRight) ? 1 : 0;
   return true;
  }
  return kept(pixel, c);
 }
 ENDCG
 SubShader {
  Pass {
   Cull Off ZWrite On ZTest LEqual
   // Lit as anything the main camera draws, as the device is when it is up. All of the stencil: the other bits are what lay behind it (the gun, the hands), and the light passes go by them.
   Stencil { Ref [_DirectStencilRef] WriteMask 255 Comp Always Pass Replace }
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #pragma target 3.5
   struct output {
    float4 g0 : SV_Target0;
    float4 g1 : SV_Target1;
    float4 g2 : SV_Target2;
    float4 g3 : SV_Target3;
    float depth : SV_Depth;
   };

   output frag (v2f i) {
    float2 uv = i.screen.xy / i.screen.w;
    float rawL = SAMPLE_DEPTH_TEXTURE(_DirectLD, uv), rawR = SAMPLE_DEPTH_TEXTURE(_DirectRD, uv);
    float toRight;
    if (!pick(i.pos.xy, uv, rawL, rawR, toRight))
     discard;
    output o;
    o.g0 = lerp(tex2D(_DirectL0, uv), tex2D(_DirectR0, uv), toRight);
    o.g1 = lerp(tex2D(_DirectL1, uv), tex2D(_DirectR1, uv), toRight);
    o.g2 = lerp(tex2D(_DirectL2, uv), tex2D(_DirectR2, uv), toRight);
    o.g3 = lerp(tex2D(_DirectL3, uv), tex2D(_DirectR3, uv), toRight);
    if (_DirectNoEmission > 0.5) o.g3.rgb = 0;
    o.depth = mainDepth(lerp(inverseDepth(rawL), inverseDepth(rawR), toRight));
    return o;
   }
   ENDCG
  }
  // 1: unused, the passes keep their numbers.
  Pass { ColorMask 0 ZWrite Off ZTest Never CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   float4 frag (v2f i) : SV_Target { return 0; }
   ENDCG
  }
  // 2: the device's surface again, once the game's decals are done: they are thrown on whatever the depth holds, and what is
  // put in after the G-buffer is not marked as what they leave alone. The dirt of the ground lay on it, other with each move.
  Pass {
   Cull Off ZWrite Off ZTest Always
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #pragma target 3.5
   struct surface { float4 g0 : SV_Target0; float4 g1 : SV_Target1; float4 g2 : SV_Target2; };
   surface frag (v2f i) {
    float2 uv = i.screen.xy / i.screen.w;
    float toRight;
    if (!pick(i.pos.xy, uv, SAMPLE_DEPTH_TEXTURE(_DirectLD, uv), SAMPLE_DEPTH_TEXTURE(_DirectRD, uv), toRight))
     discard;
    surface o;
    o.g0 = lerp(tex2D(_DirectL0, uv), tex2D(_DirectR0, uv), toRight);
    o.g1 = lerp(tex2D(_DirectL1, uv), tex2D(_DirectR1, uv), toRight);
    o.g2 = lerp(tex2D(_DirectL2, uv), tex2D(_DirectR2, uv), toRight);
    return o;
   }
   ENDCG
  }
  // 3: unused, the passes keep their numbers.
  Pass { ColorMask 0 ZWrite Off ZTest Never CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   float4 frag (v2f i) : SV_Target { return 0; }
   ENDCG
  }
  // 4: where a half is fading, once all is lit: its pixels kept and left out (kept) evened into one fade, over the pattern's width.
  Pass {
   Cull Off ZWrite Off ZTest Always
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #pragma target 3.5
   sampler2D _DirectLitNow;
   float4 _DirectTexel;
   float4 frag (v2f i) : SV_Target {
    float2 uv = i.screen.xy / i.screen.w;
    float toRight;
    float c = coverage(uv, SAMPLE_DEPTH_TEXTURE(_DirectLD, uv), SAMPLE_DEPTH_TEXTURE(_DirectRD, uv), toRight);
    float t = smoothstep(_DirectSplit - _DirectStrip * 0.5, _DirectSplit + _DirectStrip * 0.5, uv.x);
    if (c <= 0 || t <= 0 || t >= 1)
     discard;
    float3 sum = 0;
    for (int y = -2; y < 2; y++)
     for (int x = -2; x < 2; x++)
      sum += tex2Dlod(_DirectLitNow, float4(uv + (float2(x, y) + 0.5) * _DirectTexel.xy, 0, 0)).rgb;
    return float4(sum / 16, 1);
   }
   ENDCG
  }
  // 5: the device's own motion on the screen, as the game works it out for what it draws: where this bit of the mesh stood from the
  // eye last frame against where it stands now. Over each half's own depth, only where that half is the one seen.
  Pass {
   Cull Off ZWrite Off ZTest LEqual Offset -1, -64
   CGPROGRAM
   #pragma vertex motionVert
   #pragma fragment frag
   #pragma target 3.5
   float4x4 _DirectNowVP, _DirectOldVP, _DirectOldM;
   float _DirectHalf;
   struct m2f { float4 pos : SV_POSITION; float4 screen : TEXCOORD0; float4 now : TEXCOORD1; float4 old : TEXCOORD2; };
   m2f motionVert (float4 v : POSITION) {
    m2f o;
    o.pos = UnityObjectToClipPos(v);
    o.screen = ComputeScreenPos(o.pos);
    o.now = mul(_DirectNowVP, mul(unity_ObjectToWorld, v));
    o.old = mul(_DirectOldVP, mul(_DirectOldM, v));
    return o;
   }
   float4 frag (m2f i) : SV_Target {
    float2 uv = i.screen.xy / i.screen.w;
    float toRight;
    if (!pick(i.pos.xy, uv, SAMPLE_DEPTH_TEXTURE(_DirectLD, uv), SAMPLE_DEPTH_TEXTURE(_DirectRD, uv), toRight) || (_DirectHalf < 0.5) != (toRight < 0.5))
     discard;
    float2 now = (i.now.xy / i.now.w + 1) * 0.5, old = (i.old.xy / i.old.w + 1) * 0.5;
    // By the camera's own matrices, up being up: no turning over, as the game's motion goes the same way in both directions.
    return float4(now - old, 0, 1);
   }
   ENDCG
  }
  // 6: bit 0 of the stencil the game left under its own drawing of the device.
  Pass {
   Cull Off ZWrite Off ZTest LEqual Offset -1, -64
   Blend One One
   ColorMask R
   Stencil { Ref 255 ReadMask 1 Comp Equal Pass Keep Fail Keep ZFail Keep }
   CGPROGRAM
   #pragma vertex stencilVert
   #pragma fragment frag
   float4 stencilVert (float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v); }
   float4 frag () : SV_Target { return 0.00392157; }
   ENDCG
  }
  // 7: bit 1 of the stencil the game left under its own drawing of the device.
  Pass {
   Cull Off ZWrite Off ZTest LEqual Offset -1, -64
   Blend One One
   ColorMask R
   Stencil { Ref 255 ReadMask 2 Comp Equal Pass Keep Fail Keep ZFail Keep }
   CGPROGRAM
   #pragma vertex stencilVert
   #pragma fragment frag
   float4 stencilVert (float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v); }
   float4 frag () : SV_Target { return 0.00784314; }
   ENDCG
  }
  // 8: bit 2 of the stencil the game left under its own drawing of the device.
  Pass {
   Cull Off ZWrite Off ZTest LEqual Offset -1, -64
   Blend One One
   ColorMask R
   Stencil { Ref 255 ReadMask 4 Comp Equal Pass Keep Fail Keep ZFail Keep }
   CGPROGRAM
   #pragma vertex stencilVert
   #pragma fragment frag
   float4 stencilVert (float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v); }
   float4 frag () : SV_Target { return 0.01568627; }
   ENDCG
  }
  // 9: bit 3 of the stencil the game left under its own drawing of the device.
  Pass {
   Cull Off ZWrite Off ZTest LEqual Offset -1, -64
   Blend One One
   ColorMask R
   Stencil { Ref 255 ReadMask 8 Comp Equal Pass Keep Fail Keep ZFail Keep }
   CGPROGRAM
   #pragma vertex stencilVert
   #pragma fragment frag
   float4 stencilVert (float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v); }
   float4 frag () : SV_Target { return 0.03137255; }
   ENDCG
  }
  // 10: bit 4 of the stencil the game left under its own drawing of the device.
  Pass {
   Cull Off ZWrite Off ZTest LEqual Offset -1, -64
   Blend One One
   ColorMask R
   Stencil { Ref 255 ReadMask 16 Comp Equal Pass Keep Fail Keep ZFail Keep }
   CGPROGRAM
   #pragma vertex stencilVert
   #pragma fragment frag
   float4 stencilVert (float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v); }
   float4 frag () : SV_Target { return 0.06274510; }
   ENDCG
  }
  // 11: bit 5 of the stencil the game left under its own drawing of the device.
  Pass {
   Cull Off ZWrite Off ZTest LEqual Offset -1, -64
   Blend One One
   ColorMask R
   Stencil { Ref 255 ReadMask 32 Comp Equal Pass Keep Fail Keep ZFail Keep }
   CGPROGRAM
   #pragma vertex stencilVert
   #pragma fragment frag
   float4 stencilVert (float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v); }
   float4 frag () : SV_Target { return 0.12549020; }
   ENDCG
  }
  // 12: bit 6 of the stencil the game left under its own drawing of the device.
  Pass {
   Cull Off ZWrite Off ZTest LEqual Offset -1, -64
   Blend One One
   ColorMask R
   Stencil { Ref 255 ReadMask 64 Comp Equal Pass Keep Fail Keep ZFail Keep }
   CGPROGRAM
   #pragma vertex stencilVert
   #pragma fragment frag
   float4 stencilVert (float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v); }
   float4 frag () : SV_Target { return 0.25098039; }
   ENDCG
  }
  // 13: bit 7 of the stencil the game left under its own drawing of the device.
  Pass {
   Cull Off ZWrite Off ZTest LEqual Offset -1, -64
   Blend One One
   ColorMask R
   Stencil { Ref 255 ReadMask 128 Comp Equal Pass Keep Fail Keep ZFail Keep }
   CGPROGRAM
   #pragma vertex stencilVert
   #pragma fragment frag
   float4 stencilVert (float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v); }
   float4 frag () : SV_Target { return 0.50196078; }
   ENDCG
  }
 }
}
