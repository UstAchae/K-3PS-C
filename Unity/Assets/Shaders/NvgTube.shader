// The game's night-vision picture in the lenses, made brighter as a tube's gain would make it, before the game's filter
// greens it: the light at each pixel (its luminance; the colour's mix kept) times _TubeGain, and, by _TubeCompress, the
// bright end pressed toward the tube's ceiling as its output saturates (1 - e^-x, which keeps the gain where it is dark)
// instead of going on straight. _TubeGain 1 with _TubeCompress 0 gives the frame unchanged.
// Step 4 of the eyepieces' picture (NightVisionComposite.cs), on the light that reaches the tube (step 2).
Shader "Justachae/NVG/Tube" {
 Properties { _MainTex ("", 2D) = "black" {} }
 SubShader {
  Pass {
   Cull Off ZWrite Off ZTest Always
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex;
   float _TubeGain;      // the gain
   float _TubeCompress;  // 0 straight, 1 the tube's saturation

   float4 frag (v2f_img i) : SV_Target {
    float4 c = tex2D(_MainTex, i.uv);
    float light = dot(c.rgb, float3(0.2126, 0.7152, 0.0722));
    if (light <= 1e-6)
     return c;
    float straight = _TubeGain * light;
    float saturated = 1.0 - exp(-straight);
    float now = lerp(straight, saturated, saturate(_TubeCompress));
    c.rgb *= now / light;
    return c;
   }
   ENDCG
  }
  // 1: a lens's mesh, solid (TwoRenders.cs: where the bare eye's sight lens is).
  Pass {
   Cull Off ZWrite Off ZTest Always Blend Off
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   float4 vert (float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v); }
   float4 frag () : SV_Target { return 1; }
   ENDCG
  }
  // 2: the eyepieces' picture (_MainTex) with the bare eye's gun laid back where it is (_Other, the frame before the
  // eyepieces: no night vision on it) by _GunShare, and its sight's lens (_LensLayer, where _LensMask is) over what lies
  // behind it by _LensShare.
  Pass {
   Cull Off ZWrite Off ZTest Always Blend Off
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.5
   #include "UnityCG.cginc"
   UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
   UNITY_DECLARE_DEPTH_TEXTURE(_DirectGD);
   float4 _DirectGD_TexelSize;
   float4 _DirectPlanes;
   sampler2D _MainTex;
   float4 _MainTex_TexelSize;
   // As NvgDirect.shader has them: the gun's own depth into the main camera's.
   float inverseDepth (float raw) {
    #if !defined(UNITY_REVERSED_Z)
    raw = 1 - raw;
    #endif
    return raw * (_DirectPlanes.x - _DirectPlanes.y) + _DirectPlanes.y;
   }
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
   bool shownAt (float raw) {
    #if defined(UNITY_REVERSED_Z)
    return raw > 0;
    #else
    return raw < 1;
    #endif
   }
   // The gun seen here (drawn here, and nothing the camera has before it: the device).
   bool gunAt (float2 uv) {
    float raw = SAMPLE_DEPTH_TEXTURE_LOD(_DirectGD, float4(uv, 0, 0));
    if (!shownAt(raw))
     return false;
    // What the camera has there is the gun put in (its depth is the gun's): not where it was left out (its sight's
    // lens), nor where something else stood before it.
    float scene = SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(uv, 0, 0)), gun = mainDepth(inverseDepth(raw));
    return abs(gun - scene) <= max(gun * 0.002, 0.00002);
   }
   sampler2D _Other, _LensLayer, _LensMask, _BareLasers;
   // The bare eye's sight's lens while the goggle eye aims: not over the goggle eye's gun (put in where it is, whole).
   sampler2D _GunWhich14, _AimLensMask;
   float _LensUnderAiming;
   float _GunShare, _LensShare;
   float4 frag (v2f_img i) : SV_Target {
    float4 c = tex2D(_MainTex, i.uv);
    // The bare eye's sight's lens while it aims: only the sight's picture there for that eye.
    // The goggle eye aiming through its sight: there it sees its sight's picture alone, nothing of the bare eye's laid over.
    if (tex2D(_AimLensMask, i.uv).r > 0.5)
     return c;
    if (tex2D(_LensMask, i.uv).r > 0.5 && (_LensUnderAiming <= 0 || tex2D(_GunWhich14, i.uv).r < 0.25 || tex2D(_GunWhich14, i.uv).r > 0.75)) {
     c.rgb = lerp(c.rgb, tex2D(_LensLayer, i.uv).rgb, _LensShare);
     return c;
    }
    // The bare eye's gun (pass 3: in alpha where it is), and its lasers as that eye sees them.
    // (Drawn unjittered: NightVisionDirect.DrawGun.)
    float2 at = i.uv;
    float4 g = tex2D(_Other, at);
    c.rgb = lerp(c.rgb, g.rgb, _GunShare * saturate(g.a));
    c.rgb += tex2D(_BareLasers, at).rgb * _GunShare;
    return c;
   }
   ENDCG
  }
  // 3: the bare eye's gun whole, out of the frame it was put into on every other pixel (NvgDirect.shader, pass 13): its own
  // pixels as they are, the others made from those next to them; in alpha where it is.
  Pass {
   Cull Off ZWrite Off ZTest Always Blend Off
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.5
   #include "UnityCG.cginc"
   UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
   UNITY_DECLARE_DEPTH_TEXTURE(_DirectGD);
   float4 _DirectGD_TexelSize;
   float4 _DirectPlanes;
   sampler2D _MainTex;
   float4 _MainTex_TexelSize;
   // As NvgDirect.shader has them: the gun's own depth into the main camera's.
   float inverseDepth (float raw) {
    #if !defined(UNITY_REVERSED_Z)
    raw = 1 - raw;
    #endif
    return raw * (_DirectPlanes.x - _DirectPlanes.y) + _DirectPlanes.y;
   }
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
   bool shownAt (float raw) {
    #if defined(UNITY_REVERSED_Z)
    return raw > 0;
    #else
    return raw < 1;
    #endif
   }
   // The gun seen here (drawn here, and nothing the camera has before it: the device).
   bool gunAt (float2 uv) {
    float raw = SAMPLE_DEPTH_TEXTURE_LOD(_DirectGD, float4(uv, 0, 0));
    if (!shownAt(raw))
     return false;
    // What the camera has there is the gun put in (its depth is the gun's): not where it was left out (its sight's
    // lens), nor where something else stood before it.
    float scene = SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(uv, 0, 0)), gun = mainDepth(inverseDepth(raw));
    return abs(gun - scene) <= max(gun * 0.002, 0.00002);
   }
   static const float2 around[8] = { float2(1, 0), float2(-1, 0), float2(0, 1), float2(0, -1), float2(1, 1), float2(-1, 1), float2(1, -1), float2(-1, -1) };
   float4 _GunTexel;   // the camera's own pixels (TwoRenders.FillApart: before the upscale)
   sampler2D _GunWhich;   // which eye's gun was put in here (NvgDirect.shader, pass 16): 1 the bare eye's, 0.5 the goggle eye's
   bool bareAt (float2 uv) { return tex2Dlod(_GunWhich, float4(uv, 0, 0)).r > 0.75; }
   float4 frag (v2f_img i) : SV_Target {
    if (!shownAt(SAMPLE_DEPTH_TEXTURE_LOD(_DirectGD, float4(i.uv, 0, 0))))
     return 0;
    if (bareAt(i.uv))
     return float4(tex2D(_MainTex, i.uv).rgb, 1);
    // The four next to it first (the other kind, on a chessboard); the corners only if none of those.
    float3 sum = 0, corners = 0;
    float n = 0, m = 0;
    [unroll] for (int k = 0; k < 8; k++) {
     float2 at = i.uv + around[k] * _GunTexel.xy;
     if (bareAt(at)) {
      float3 v = tex2Dlod(_MainTex, float4(at, 0, 0)).rgb;
      if (k < 4) { sum += v; n += 1; }
      else { corners += v; m += 1; }
     }
    }
    if (n <= 0) { sum = corners; n = m; }
    return n > 0 ? float4(sum / n, 1) : 0;
   }
   ENDCG
  }
  // 4: the frame as the goggle eye alone sees it: where the bare eye's gun was put in, made from the pixels next to it
  // (the goggle eye's).
  Pass {
   Cull Off ZWrite Off ZTest Always Blend Off
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.5
   #include "UnityCG.cginc"
   UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
   UNITY_DECLARE_DEPTH_TEXTURE(_DirectGD);
   float4 _DirectGD_TexelSize;
   float4 _DirectPlanes;
   sampler2D _MainTex;
   float4 _MainTex_TexelSize;
   // As NvgDirect.shader has them: the gun's own depth into the main camera's.
   float inverseDepth (float raw) {
    #if !defined(UNITY_REVERSED_Z)
    raw = 1 - raw;
    #endif
    return raw * (_DirectPlanes.x - _DirectPlanes.y) + _DirectPlanes.y;
   }
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
   bool shownAt (float raw) {
    #if defined(UNITY_REVERSED_Z)
    return raw > 0;
    #else
    return raw < 1;
    #endif
   }
   // The gun seen here (drawn here, and nothing the camera has before it: the device).
   bool gunAt (float2 uv) {
    float raw = SAMPLE_DEPTH_TEXTURE_LOD(_DirectGD, float4(uv, 0, 0));
    if (!shownAt(raw))
     return false;
    // What the camera has there is the gun put in (its depth is the gun's): not where it was left out (its sight's
    // lens), nor where something else stood before it.
    float scene = SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(uv, 0, 0)), gun = mainDepth(inverseDepth(raw));
    return abs(gun - scene) <= max(gun * 0.002, 0.00002);
   }
   static const float2 around[8] = { float2(1, 0), float2(-1, 0), float2(0, 1), float2(0, -1), float2(1, 1), float2(-1, 1), float2(1, -1), float2(-1, -1) };
   UNITY_DECLARE_DEPTH_TEXTURE(_DirectHD);
   float _GoggleShare;
   sampler2D _DirectGunLens;
   // The goggle eye's gun put in here (NvgDirect.shader, pass 13: its depth is what the camera has).
   float4 _GunTexel;   // the camera's own pixels (TwoRenders.FillApart: before the upscale)
   sampler2D _GunWhich;   // which eye's gun was put in here (NvgDirect.shader, pass 16): 1 the bare eye's, 0.5 the goggle eye's
   bool bareAt (float2 uv) { return tex2Dlod(_GunWhich, float4(uv, 0, 0)).r > 0.75; }
   bool goggleAt (float2 uv) { float w = tex2Dlod(_GunWhich, float4(uv, 0, 0)).r; return w > 0.25 && w < 0.75; }
   float4 frag (v2f_img i) : SV_Target {
    float4 c = tex2D(_MainTex, i.uv);
    bool bareHere = bareAt(i.uv), goggleHere = goggleAt(i.uv);
    bool goggleCovers = shownAt(SAMPLE_DEPTH_TEXTURE_LOD(_DirectHD, float4(i.uv, 0, 0)));
    // From the pixels next to it (the four first, the corners only if none of those): what lies behind the guns (those
    // with neither put in), and the goggle eye's gun (those with it).
    float3 behind4 = 0, behindC = 0, gun4 = 0, gunC = 0;
    float nb = 0, mb = 0, ng = 0, mg = 0;
    [unroll] for (int k = 0; k < 8; k++) {
     float2 at = i.uv + around[k] * _GunTexel.xy;
     float3 v = tex2Dlod(_MainTex, float4(at, 0, 0)).rgb;
     bool b = bareAt(at), g = goggleAt(at);
     if (!b && !g) { if (k < 4) { behind4 += v; nb += 1; } else { behindC += v; mb += 1; } }
     if (g) { if (k < 4) { gun4 += v; ng += 1; } else { gunC += v; mg += 1; } }
    }
    float3 own = goggleHere ? c.rgb : (ng > 0 ? gun4 / ng : (mg > 0 ? gunC / mg : c.rgb));
    // Behind: this pixel's own where neither gun is put in; else from around; where nothing around has neither, the
    // goggle eye's gun (both guns over it: nothing behind to be seen, the bare eye's laid over after).
    float3 behind = c.rgb;
    if (bareHere || goggleHere)
     behind = nb > 0 ? behind4 / nb : (mb > 0 ? behindC / mb : own);
    // Where the bare eye's gun was put in, what the goggle eye sees is not in the frame: left out (weight 0), filled from
    // around (pass 5), so that the objective's blur does not spread the bare eye's gun into the goggle's picture.
    // Under the bare eye's gun (put in on half the pixels) made from the goggle eye's own pixels next to it; filled from
    // around (pass 5) only where none is.
    float w = bareHere && nb + mb + ng + mg <= 0 ? 0 : 1;
    // Behind the aiming bare eye's sight lens (_DirectGunLens): that eye sees through its sight there, and what the goggle
    // eye's picture adds is what lies beyond, not its own gun (the user's, 2026-11-17).
    float share = tex2Dlod(_DirectGunLens, float4(i.uv, 0, 0)).r > 0.5 ? 0 : _GoggleShare;
    if (!goggleCovers || !goggleHere && ng <= 0 && mg <= 0)
     return float4(behind * w, w);
    return float4(lerp(behind, own, share) * w, w);
   }
   ENDCG
  }
  // 5: the goggle eye's frame with what was left out (pass 4: weight 0 in alpha, colours weighted) filled from around: from
  // the first smaller level of this picture (its mips, made in between) that has some of what was kept.
  Pass {
   Cull Off ZWrite Off ZTest Always Blend Off
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.5
   #include "UnityCG.cginc"
   sampler2D _MainTex;
   float4 frag (v2f_img i) : SV_Target {
    float4 s = tex2Dlod(_MainTex, float4(i.uv, 0, 0));
    if (s.a > 0.99)
     return float4(s.rgb / s.a, 1);
    // From the coarsest level to the finest, each laid over by how much of it was kept (the first level found alone came
    // in blocks that jumped as the gun moved, and the objective's blur spread them round it like heat).
    float3 filled = 0;
    float any = 0;
    for (int level = 11; level >= 0; level--) {
     float4 m = tex2Dlod(_MainTex, float4(i.uv, 0, level));
     if (m.a > 0.0001) {
      filled = any > 0 ? lerp(filled, m.rgb / m.a, saturate(m.a * 8)) : m.rgb / m.a;
      any = 1;
     }
    }
    return float4(filled, 1);
   }
   ENDCG
  }
  // 6: a copy of the frame with the goggle eye's gun's forward parts on it, laid back over the frame by that gun's share.
  Pass {
   Cull Off ZWrite Off ZTest Always Blend SrcAlpha OneMinusSrcAlpha
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex;
   float _PartsShare;
   sampler2D _DirectGunLens;   // (not behind the aiming bare eye's sight lens, as pass 4)
   float4 frag (v2f_img i) : SV_Target { return float4(tex2D(_MainTex, i.uv).rgb, tex2D(_DirectGunLens, i.uv).r > 0.5 ? 0 : _PartsShare); }
   ENDCG
  }
  // 7: the camera's depth as the goggle eye has it, weighted: where the bare eye's gun was put in (_GunWhich 1) left out, to be
  // filled from around (pass 8) as its picture is (pass 5): the objective's blur goes by the nearest depth about a pixel,
  // and the bare eye's gun's spread its blur round it.
  Pass {
   Cull Off ZWrite Off ZTest Always Blend Off
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.5
   #include "UnityCG.cginc"
   UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
   UNITY_DECLARE_DEPTH_TEXTURE(_DirectHD);
   sampler2D _GunWhich, _DirectGunLens;
   float4 frag (v2f_img i) : SV_Target {
    float which = tex2Dlod(_GunWhich, float4(i.uv, 0, 0)).r;
    bool goggleGun = which > 0.25 && which < 0.75, lens = tex2Dlod(_DirectGunLens, float4(i.uv, 0, 0)).r > 0.5;
    // (Behind the aiming bare eye's sight lens the goggle eye's gun is not seen, pass 4: its depth left out too.)
    float w = which > 0.75 || goggleGun && lens ? 0 : 1;
    // Where the goggle eye's gun stands but this pixel was given neither gun (the chessboard's other kind, the bare eye's
    // gun not there): the frame has what lies behind, far, and the gun's depth was a chessboard of near and far, which the
    // objective's blur, reading a pixel and its corners (all of one kind), took for far on half of them, and lost. Left
    // out too, filled from the gun's own pixels next to it.
    float rawH = SAMPLE_DEPTH_TEXTURE_LOD(_DirectHD, float4(i.uv, 0, 0));
    #if defined(UNITY_REVERSED_Z)
    bool goggleCovers = rawH > 0;
    #else
    bool goggleCovers = rawH < 1;
    #endif
    if (which < 0.25 && goggleCovers && !lens)
     w = 0;
    return float4(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(i.uv, 0, 0)) * w, w, 0, 0);
   }
   ENDCG
  }
  // 8: that depth filled from around (its mips made in between): from the first smaller level that has some of what was kept.
  Pass {
   Cull Off ZWrite Off ZTest Always Blend Off
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.5
   #include "UnityCG.cginc"
   sampler2D _MainTex;
   float4 frag (v2f_img i) : SV_Target {
    float2 s = tex2Dlod(_MainTex, float4(i.uv, 0, 0)).rg;
    if (s.y > 0.99)
     return s.x / s.y;
    // As the picture is filled (pass 5): coarsest to finest, each by how much of it was kept.
    float filled = 0, any = 0;
    for (int level = 11; level >= 0; level--) {
     float2 m = tex2Dlod(_MainTex, float4(i.uv, 0, level)).rg;
     if (m.y > 0.0001) {
      filled = any > 0 ? lerp(filled, m.x / m.y, saturate(m.y * 8)) : m.x / m.y;
      any = 1;
     }
    }
    return filled;
   }
   ENDCG
  }
  // 9: the bare eye's gun (pass 3, this frame's: its pixels drawn on half the chessboard, the others made from them) gathered
  // over frames: what it had last frame, followed along its own motion (_BareMotion) and kept within what this frame has about
  // the pixel, eased toward this frame's. It is laid over after the frame is smoothed over frames, so its light's grain and
  // the chessboard (turned over every frame) are smoothed here.
  Pass {
   Cull Off ZWrite Off ZTest Always Blend Off
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.5
   #include "UnityCG.cginc"
   sampler2D _MainTex, _BareHistory, _BareMotion, _GunWhich;
   float4 _GunTexel;
   float _BareHistoryOn;
   float4 frag (v2f_img i) : SV_Target {
    float4 now = tex2Dlod(_MainTex, float4(i.uv, 0, 0));
    if (now.a < 0.5 || _BareHistoryOn < 0.5)
     return now;
    float3 lo = now.rgb, hi = now.rgb;
    [unroll] for (int y = -1; y <= 1; y++)
     [unroll] for (int x = -1; x <= 1; x++) {
      float4 n = tex2Dlod(_MainTex, float4(i.uv + float2(x, y) * _GunTexel.xy, 0, 0));
      if (n.a > 0.5) {
       lo = min(lo, n.rgb);
       hi = max(hi, n.rgb);
      }
     }
    float2 was = i.uv - tex2Dlod(_BareMotion, float4(i.uv, 0, 0)).xy;
    if (any(was < 0) || any(was > 1))
     return now;
    float4 h = tex2Dlod(_BareHistory, float4(was, 0, 0));
    if (h.a < 0.5)
     return now;
    // This frame's own drawn pixels count more than those made from them.
    bool drawn = tex2Dlod(_GunWhich, float4(i.uv, 0, 0)).r > 0.75;
    return float4(lerp(clamp(h.rgb, lo, hi), now.rgb, drawn ? 0.3 : 0.15), now.a);
   }
   ENDCG
  }
 }
}
