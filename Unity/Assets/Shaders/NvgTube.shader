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
  // has something (its alpha).
  Pass {
   Cull Off ZWrite Off ZTest Always
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex, _Other;
   float _OtherShare;
   float4 frag (v2f_img i) : SV_Target {
    float4 c = tex2D(_MainTex, i.uv);
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
 }
}
