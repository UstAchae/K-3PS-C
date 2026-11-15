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
 UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
 float _DirectSplit, _DirectStrip, _DirectBehind;
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

 // The main camera's depth at that. What is nearer than its near plane cannot be held as it is: all of it held at the plane, lit there, with
 // a hard edge where that began, the edge of the device flickered as the frame's jitter moved it. So what comes near is eased in toward
 // the plane instead, in its order, never reaching it.
 float mainDepth (float inverse) {
  float limit = _DirectPlanes.z, knee = limit * 0.7;
  if (inverse > knee)
   inverse = knee + (limit - knee) * (1 - exp((knee - inverse) / (limit - knee)));
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
  // Through either eye's lens here, nothing of the device: the lenses' picture is made from this frame, and the composite shows the
  // lens there whole.
  if ((t < 1 && within(_DirectLens0, uv, _EyeInvVP0)) || (t > 0 && within(_DirectLens1, uv, _EyeInvVP1))) return 0;
  bool left = t < 1 && shown(rawL), right = t > 0 && shown(rawR);
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
 // the pattern once all is lit (pass 2), that is the two eyes' lit pictures eased into each other, as the eyes' cameras were.
 // A monocular is before one eye only: toward the other eye's edge of the frame it fades out, as the composite once faded it (x 1 for
 // a monocular, y its eye: 0 left, 1 right, z the fade's power, 0 not at all).
 float4 _DirectMonocular;
 float monocularShown (float2 uv) {
  if (_DirectMonocular.x < 0.5 || _DirectMonocular.z <= 0)
   return 1;
  float s = smoothstep(0, 0.5 + _DirectStrip * 0.5, _DirectMonocular.y > 0.5 ? uv.x : 1 - uv.x);
  return pow(max(s, 1e-5), _DirectMonocular.z);
 }
 bool fadingAt (float2 uv) { return monocularShown(uv) < 0.999; }
 // Every other pixel, as on a chessboard: across a monocular's fade those have the device, the others what lies behind.
 bool checker (float2 pixel) { return fmod(floor(pixel.x) + floor(pixel.y), 2) < 0.5; }

 bool pick (float2 pixel, float2 uv, float rawL, float rawR, out float toRight) {
  float c = coverage(uv, rawL, rawR, toRight);
  if (c <= 0)
   return false;
  // A G-buffer has no see-through. Across a monocular's fade every other pixel has the device, whole, the eyes' halves mixed in by
  // their shares; once lit, each pixel gets the device's light from the pixels that have it and what lies behind from the others,
  // mixed by how much of the device shows (pass 2). Kept and left out by a coarser pattern and evened over it, the fade showed its
  // squares, what lay behind blurred with it.
  if (fadingAt(uv))
   return checker(pixel);
  if (toRight > 0 && toRight < 1) {
   toRight = kept(pixel, toRight) ? 1 : 0;
   return true;
  }
  return kept(pixel, c);
 }
 // The main camera's depth of this pixel's device.
 float pickedDepth (float rawL, float rawR, float toRight) {
  return mainDepth(lerp(inverseDepth(rawL), inverseDepth(rawR), toRight));
 }

 // Not behind what the main camera has there (the helmet's brim stands before the device), for what comes once the camera's depth
 // texture is made. Either eye's: across the strip each pixel may be the one's or the other's.
 bool notCovered (float2 uv, float rawL, float rawR) {
  float scene = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
  #if defined(UNITY_REVERSED_Z)
  return max(mainDepth(inverseDepth(rawL)), mainDepth(inverseDepth(rawR))) >= scene - 0.001;
  #else
  return min(mainDepth(inverseDepth(rawL)), mainDepth(inverseDepth(rawR))) <= scene + 0.001;
  #endif
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
    o.depth = mainDepth(lerp(inverseDepth(rawL), inverseDepth(rawR), toRight));
    return o;
   }
   ENDCG
  }
  // 1: the device's surface again, once the game's decals are done: they are thrown on whatever the depth holds, and what is
  // put in after the G-buffer is not marked as what they leave alone. The dirt of the ground lay on it, other with each move.
  Pass {
   // By the depth the device was put in with: what stands before it keeps its own surface.
   Cull Off ZWrite Off ZTest LEqual
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #pragma target 3.5
   struct surface { float4 g0 : SV_Target0; float4 g1 : SV_Target1; float4 g2 : SV_Target2; float depth : SV_Depth; };
   surface frag (v2f i) {
    float2 uv = i.screen.xy / i.screen.w;
    float rawL = SAMPLE_DEPTH_TEXTURE(_DirectLD, uv), rawR = SAMPLE_DEPTH_TEXTURE(_DirectRD, uv);
    float toRight;
    if (!pick(i.pos.xy, uv, rawL, rawR, toRight))
     discard;
    surface o;
    o.depth = pickedDepth(rawL, rawR, toRight);
    o.g0 = lerp(tex2D(_DirectL0, uv), tex2D(_DirectR0, uv), toRight);
    o.g1 = lerp(tex2D(_DirectL1, uv), tex2D(_DirectR1, uv), toRight);
    o.g2 = lerp(tex2D(_DirectL2, uv), tex2D(_DirectR2, uv), toRight);
    return o;
   }
   ENDCG
  }
  // 2: where a half is fading, once all is lit: its pixels kept and left out (kept) evened into one fade, over the pattern's width.
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
    float rawL = SAMPLE_DEPTH_TEXTURE(_DirectLD, uv), rawR = SAMPLE_DEPTH_TEXTURE(_DirectRD, uv);
    float c = coverage(uv, rawL, rawR, toRight);
    float t = smoothstep(_DirectSplit - _DirectStrip * 0.5, _DirectSplit + _DirectStrip * 0.5, uv.x);
    bool strip = t > 0 && t < 1, fading = fadingAt(uv);
    if (c <= 0 || !strip && !fading || !notCovered(uv, rawL, rawR))
     discard;
    if (fading) {
     // This pixel's own light, and the other kind's from the four next to it (all of the other kind on a chessboard). Next to a
     // pixel without the device, only those that have it there count.
     float3 own = tex2Dlod(_DirectLitNow, float4(uv, 0, 0)).rgb;
     bool device = checker(i.pos.xy);
     float3 other = 0;
     float count = 0;
     static const float2 steps[4] = { float2(1, 0), float2(-1, 0), float2(0, 1), float2(0, -1) };
     [unroll] for (int k = 0; k < 4; k++) {
      float2 at = uv + steps[k] * _DirectTexel.xy;
      float tn = smoothstep(_DirectSplit - _DirectStrip * 0.5, _DirectSplit + _DirectStrip * 0.5, at.x);
      bool has = tn < 1 && shown(SAMPLE_DEPTH_TEXTURE_LOD(_DirectLD, float4(at, 0, 0)))
          || tn > 0 && shown(SAMPLE_DEPTH_TEXTURE_LOD(_DirectRD, float4(at, 0, 0)));
      float w = device || has ? 1 : 0;
      other += tex2Dlod(_DirectLitNow, float4(at, 0, 0)).rgb * w;
      count += w;
     }
     if (count <= 0)
      return float4(own, 1);
     other /= count;
     return float4(lerp(device ? other : own, device ? own : other, c * monocularShown(uv)), 1);
    }
    float3 sum = 0;
    for (int y = -2; y < 2; y++)
     for (int x = -2; x < 2; x++)
      sum += tex2Dlod(_DirectLitNow, float4(uv + (float2(x, y) + 0.5) * _DirectTexel.xy, 0, 0)).rgb;
    return float4(sum / 16, 1);
   }
   ENDCG
  }
  // 3: the device's own motion on the screen, as the game works it out for what it draws: where this bit of the mesh stood from the
  // eye last frame against where it stands now. Over each half's own depth, only where that half is the one seen.
  Pass {
   Cull Off ZWrite Off ZTest Always
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
    float rawL = SAMPLE_DEPTH_TEXTURE(_DirectLD, uv), rawR = SAMPLE_DEPTH_TEXTURE(_DirectRD, uv);
    if (!pick(i.pos.xy, uv, rawL, rawR, toRight) || (_DirectHalf < 0.5) != (toRight < 0.5) || !notCovered(uv, rawL, rawR))
     discard;
    // Only what this eye's depth says is the front of the device here: that depth is read, so it cannot be the depth target too.
    float own = _DirectHalf < 0.5 ? rawL : rawR;
    if (abs(own - i.pos.z) > max(own * 0.002, 0.0000003))
     discard;
    float2 now = (i.now.xy / i.now.w + 1) * 0.5, old = (i.old.xy / i.old.w + 1) * 0.5;
    // By the camera's own matrices, up being up: no turning over, as the game's motion goes the same way in both directions.
    return float4(now - old, 0, 1);
   }
   ENDCG
  }
  // 4: bit 0 of the stencil the game left under its own drawing of the device.
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
  // 5: bit 1 of the stencil the game left under its own drawing of the device.
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
  // 6: bit 2 of the stencil the game left under its own drawing of the device.
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
  // 7: bit 3 of the stencil the game left under its own drawing of the device.
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
  // 8: bit 4 of the stencil the game left under its own drawing of the device.
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
  // 9: bit 5 of the stencil the game left under its own drawing of the device.
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
  // 10: bit 6 of the stencil the game left under its own drawing of the device.
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
  // 11: bit 7 of the stencil the game left under its own drawing of the device.
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
  // 12: for a sealed eyecup's dark (NvgShade.shader), where the device seen at each pixel stands from its own eye and which way it faces:
  // only what is seen there, so the same half as the surface, and nothing through a lens. w: 1 the left eye's half, 2 the right's.
  Pass {
   Cull Off ZWrite Off ZTest Always
   CGPROGRAM
   #pragma vertex placeVert
   #pragma fragment frag
   #pragma target 3.5
   float _DirectHalf;
   struct p2f { float4 pos : SV_POSITION; float4 screen : TEXCOORD0; float3 place : TEXCOORD1; float3 n : TEXCOORD2; };
   p2f placeVert (float4 v : POSITION, float3 n : NORMAL) {
    p2f o;
    o.pos = UnityObjectToClipPos(v);
    o.screen = ComputeScreenPos(o.pos);
    o.place = mul(unity_ObjectToWorld, v).xyz;
    o.n = UnityObjectToWorldNormal(n);
    return o;
   }
   struct placed { float4 place : SV_Target0; float4 n : SV_Target1; };
   placed frag (p2f i) {
    float2 uv = i.screen.xy / i.screen.w;
    float rawL = SAMPLE_DEPTH_TEXTURE(_DirectLD, uv), rawR = SAMPLE_DEPTH_TEXTURE(_DirectRD, uv);
    float toRight;
    if (!pick(i.pos.xy, uv, rawL, rawR, toRight) || (_DirectHalf < 0.5) != (toRight < 0.5))
     discard;
    float own = _DirectHalf < 0.5 ? rawL : rawR;
    if (abs(own - i.pos.z) > max(own * 0.002, 0.0000003))
     discard;
    placed o;
    o.place = float4(i.place, _DirectHalf + 1);
    o.n = float4(normalize(i.n), 0);
    return o;
   }
   ENDCG
  }
 }
}
