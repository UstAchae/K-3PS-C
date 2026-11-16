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
  // 1: the frame and another (_Other: the bare eye's near things, TwoRenders.cs), mixed by _OtherShare where the other
  // has something (its alpha); never over the goggle (the camera's own depth nearer than _DeviceReach).
  Pass {
   Cull Off ZWrite Off ZTest Always
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex, _Other;
   float _OtherShare, _DeviceReach;
   UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
   float4 frag (v2f_img i) : SV_Target {
    float4 c = tex2D(_MainTex, i.uv);
    float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
    if (_DeviceReach > 0 && LinearEyeDepth(raw) < _DeviceReach)
     return c;
    float4 o = tex2D(_Other, i.uv);
    c.rgb = lerp(c.rgb, o.rgb, _OtherShare * saturate(o.a));
    return c;
   }
   ENDCG
  }
  // 2: a picture as it is, opaque (the experiment's probes, drawn over the frame).
  Pass {
   Cull Off ZWrite Off ZTest Always Blend Off
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex;
   float4 frag (v2f_img i) : SV_Target { return float4(tex2D(_MainTex, i.uv).rgb, 1); }
   ENDCG
  }
  // 3: a render of what is near (TwoRenders.cs): its picture, and in alpha whether something is there (its depth short of
  // _NearReach; the sky and what lies past the reach, nothing).
  Pass {
   Cull Off ZWrite Off ZTest Always Blend Off
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
   sampler2D _MainTex, _GunMask;
   float _NearReach, _UseGunMask;
   float4 frag (v2f_img i) : SV_Target {
    float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
    #if defined(UNITY_REVERSED_Z)
    bool none = raw <= 0;
    #else
    bool none = raw >= 1;
    #endif
    bool near = !none && LinearEyeDepth(raw) < _NearReach * 0.95;
    // Only where the gun itself is (_GunMask, pass 7): what shares its layers (a table, a wall) is not it.
    if (_UseGunMask > 0.5)
     near = near && tex2D(_GunMask, i.uv).r > 0.5;
    return float4(tex2D(_MainTex, i.uv).rgb, near ? 1 : 0);
   }
   ENDCG
  }
  // 4: the scene without the gun (_MainTex), the goggle eye's gun (_GunG) laid in by _ShareG where it is, weighted so
  // that once the bare eye's (_GunB) is laid over by _ShareB after (pass 1), each has its share where both are and
  // the scene keeps the rest; never over the goggle (the camera's own depth nearer than _DeviceReach).
  Pass {
   Cull Off ZWrite Off ZTest Always Blend Off
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex, _GunG, _GunB;
   float _ShareG, _ShareB, _DeviceReach;
   UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
   float4 frag (v2f_img i) : SV_Target {
    float4 s = tex2D(_MainTex, i.uv), g = tex2D(_GunG, i.uv);
    float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
    if (_DeviceReach > 0 && LinearEyeDepth(raw) < _DeviceReach)
     return s;
    float wG = _ShareG * saturate(g.a), wB = _ShareB * saturate(tex2D(_GunB, i.uv).a);
    float k = max(1 - wB, 1e-3);
    return float4((s.rgb * (1 - wG - wB) + g.rgb * wG) / k, s.a);
   }
   ENDCG
  }
  // 5: the camera's depth as it is (raw), kept (TwoRenders.cs: the goggle eye's gun render's).
  Pass {
   Cull Off ZWrite Off ZTest Always Blend Off
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
   float4 frag (v2f_img i) : SV_Target { return SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv); }
   ENDCG
  }
  // 6: the camera's depth (raw) with the goggle eye's gun's (_GunDepth, pass 5) where that is nearer: the depth the
  // objective's blur goes by, the gun laid into the frame though the frame was rendered without it.
  Pass {
   Cull Off ZWrite Off ZTest Always Blend Off
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
   sampler2D _GunDepth;
   float4 frag (v2f_img i) : SV_Target {
    float a = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv), b = tex2D(_GunDepth, i.uv).r;
    #if defined(UNITY_REVERSED_Z)
    return max(a, b);
    #else
    return min(a, b);
    #endif
   }
   ENDCG
  }
  // 7: the gun's own meshes (TwoRenders.cs), drawn into a mask where they are the nearest thing the camera has: 1.
  Pass {
   Cull Off ZWrite Off ZTest Always Blend Off
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
   struct v2f { float4 pos : SV_POSITION; float4 screen : TEXCOORD0; float depth : TEXCOORD1; };
   v2f vert (float4 v : POSITION) {
    v2f o;
    o.pos = UnityObjectToClipPos(v);
    o.screen = ComputeScreenPos(o.pos);
    o.depth = -UnityObjectToViewPos(v).z;
    return o;
   }
   float4 frag (v2f i) : SV_Target {
    float2 uv = i.screen.xy / i.screen.w;
    float scene = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
    clip(scene * 1.02 + 0.01 - i.depth);
    return 1;
   }
   ENDCG
  }
  // 8: a gun's render (_MainTex, its alpha the gun's mask) with the sight's lens kept sharp: where _LensMask is, _Sharp.
  Pass {
   Cull Off ZWrite Off ZTest Always Blend Off
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex, _Sharp, _LensMask;
   float4 frag (v2f_img i) : SV_Target {
    float4 c = tex2D(_MainTex, i.uv);
    if (tex2D(_LensMask, i.uv).r > 0.5)
     c.rgb = tex2D(_Sharp, i.uv).rgb;
    return c;
   }
   ENDCG
  }
  // 9: the frame without the bare eye's gun (TwoRenders.cs): where it is, its chessboard's other pixels (what lies behind)
  // in place of its own, for the eyepieces' picture (the goggle's eye does not see it).
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
   // Which pixels had the gun put in (NvgDirect.shader's chessboard), by the render's own pixels.
   bool gunPixel (float2 uv) {
    float2 p = floor(uv * _DirectGD_TexelSize.zw);
    return fmod(p.x + p.y, 2) < 0.5;
   }
   float4 frag (v2f_img i) : SV_Target {
    float4 c = tex2D(_MainTex, i.uv);
    if (!gunAt(i.uv) || !gunPixel(i.uv))
     return c;
    float2 d = 1.0 / _DirectGD_TexelSize.zw;
    float3 sum = tex2D(_MainTex, i.uv + float2(d.x, 0)).rgb + tex2D(_MainTex, i.uv - float2(d.x, 0)).rgb
        + tex2D(_MainTex, i.uv + float2(0, d.y)).rgb + tex2D(_MainTex, i.uv - float2(0, d.y)).rgb;
    return float4(sum * 0.25, c.a);
   }
   ENDCG
  }
  // 10: the bare eye's gun alone (TwoRenders.cs): its chessboard's pixels, its other pixels from the four next to them;
  // in alpha where it is seen.
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
   // Which pixels had the gun put in (NvgDirect.shader's chessboard), by the render's own pixels.
   bool gunPixel (float2 uv) {
    float2 p = floor(uv * _DirectGD_TexelSize.zw);
    return fmod(p.x + p.y, 2) < 0.5;
   }
   sampler2D _PreLens;
   float4 frag (v2f_img i) : SV_Target {
    float3 own = tex2D(_MainTex, i.uv).rgb;
    // The sight's lens laid in (it differs from the frame before it): the gun's.
    float3 laid = abs(own - tex2D(_PreLens, i.uv).rgb);
    if (max(laid.r, max(laid.g, laid.b)) > 1e-4)
     return float4(own, 1);
    if (!gunAt(i.uv))
     return 0;
    if (gunPixel(i.uv))
     return float4(own, 1);
    float2 d = 1.0 / _DirectGD_TexelSize.zw;
    float3 sum = 0;
    float n = 0;
    static const float2 steps[4] = { float2(1, 0), float2(-1, 0), float2(0, 1), float2(0, -1) };
    [unroll] for (int k = 0; k < 4; k++) {
     float2 at = i.uv + steps[k] * d;
     float w = gunAt(at) ? 1 : 0;
     sum += tex2D(_MainTex, at).rgb * w;
     n += w;
    }
    return n > 0 ? float4(sum / n, 1) : 0;
   }
   ENDCG
  }
  // 11: a lens's mesh, solid (TwoRenders.cs: where the bare eye's sight lens is).
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
  // 12: the frame with the bare eye's sight lens (_Other) laid over by _OtherShare where its mask (_LensMask) is.
  Pass {
   Cull Off ZWrite Off ZTest Always Blend Off
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex, _Other, _LensMask;
   float _OtherShare;
   float4 frag (v2f_img i) : SV_Target {
    float4 c = tex2D(_MainTex, i.uv);
    if (tex2D(_LensMask, i.uv).r > 0.5)
     c.rgb = lerp(c.rgb, tex2D(_Other, i.uv).rgb, _OtherShare);
    return c;
   }
   ENDCG
  }
  // 13: the frame (_MainTex, the eyepieces' picture made without the bare eye's gun) with that gun laid in (_Other, its
  // alpha where it is) by _GunShare, and over it, where its sight's lens is (_LensMask), the lens (_LensLayer) seen
  // through by _LensShare: what lies behind the lens is the frame, not the gun.
  Pass {
   Cull Off ZWrite Off ZTest Always Blend Off
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex, _Other, _LensLayer, _LensMask;
   float _GunShare, _LensShare;
   float4 frag (v2f_img i) : SV_Target {
    float4 c = tex2D(_MainTex, i.uv);
    if (tex2D(_LensMask, i.uv).r > 0.5)
     c.rgb = lerp(c.rgb, tex2D(_LensLayer, i.uv).rgb, _LensShare);
    else {
     float4 g = tex2D(_Other, i.uv);
     c.rgb = lerp(c.rgb, g.rgb, _GunShare * saturate(g.a));
    }
    return c;
   }
   ENDCG
  }
  // 14: the eyepieces' picture (_MainTex) with the bare eye's gun laid back where it is (_Other, the frame before the
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
   float _GunShare, _LensShare;
   float4 frag (v2f_img i) : SV_Target {
    float4 c = tex2D(_MainTex, i.uv);
    // The bare eye's sight's lens while it aims: only the sight's picture there for that eye.
    if (tex2D(_LensMask, i.uv).r > 0.5) {
     c.rgb = lerp(c.rgb, tex2D(_LensLayer, i.uv).rgb, _LensShare);
     return c;
    }
    // The bare eye's gun (pass 15: in alpha where it is), and its lasers as that eye sees them.
    float4 g = tex2D(_Other, i.uv);
    c.rgb = lerp(c.rgb, g.rgb, _GunShare * saturate(g.a));
    c.rgb += tex2D(_BareLasers, i.uv).rgb * _GunShare;
    return c;
   }
   ENDCG
  }
  // 15: the bare eye's gun whole, out of the frame it was put into on every other pixel (NvgDirect.shader, pass 13): its own
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
   float4 frag (v2f_img i) : SV_Target {
    if (!shownAt(SAMPLE_DEPTH_TEXTURE_LOD(_DirectGD, float4(i.uv, 0, 0))))
     return 0;
    if (gunAt(i.uv))
     return float4(tex2D(_MainTex, i.uv).rgb, 1);
    // The four next to it first (the other kind, on a chessboard); the corners only if none of those.
    float3 sum = 0, corners = 0;
    float n = 0, m = 0;
    [unroll] for (int k = 0; k < 8; k++) {
     float2 at = i.uv + around[k] * _MainTex_TexelSize.xy;
     if (gunAt(at)) {
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
  // 16: the frame as the goggle eye alone sees it: where the bare eye's gun was put in, made from the pixels next to it
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
   float4 frag (v2f_img i) : SV_Target {
    float4 c = tex2D(_MainTex, i.uv);
    if (!gunAt(i.uv))
     return c;
    // The four next to it first (the other kind, on a chessboard); the corners only if none of those.
    float3 sum = 0, corners = 0;
    float n = 0, m = 0;
    [unroll] for (int k = 0; k < 8; k++) {
     float2 at = i.uv + around[k] * _MainTex_TexelSize.xy;
     if (!gunAt(at)) {
      float3 v = tex2Dlod(_MainTex, float4(at, 0, 0)).rgb;
      if (k < 4) { sum += v; n += 1; }
      else { corners += v; m += 1; }
     }
    }
    if (n <= 0) { sum = corners; n = m; }
    return n > 0 ? float4(sum / n, c.a) : c;
   }
   ENDCG
  }
 }
}
