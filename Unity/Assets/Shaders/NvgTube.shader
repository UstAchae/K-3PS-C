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
   sampler2D _MainTex;
   float _NearReach;
   float4 frag (v2f_img i) : SV_Target {
    float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
    #if defined(UNITY_REVERSED_Z)
    bool none = raw <= 0;
    #else
    bool none = raw >= 1;
    #endif
    bool near = !none && LinearEyeDepth(raw) < _NearReach * 0.95;
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
 }
}
