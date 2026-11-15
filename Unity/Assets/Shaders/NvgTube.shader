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
  // 1: the frame and another (_Other: the other eye's, TwoRenderTest.cs), mixed by _OtherShare.
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
    c.rgb = lerp(c.rgb, tex2D(_Other, i.uv).rgb, _OtherShare);
    return c;
   }
   ENDCG
  }
  // 2: the camera's depth as distance (m) along its view, the sky far off (TwoRenderTest.cs).
  Pass {
   Cull Off ZWrite Off ZTest Always
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
   float4 frag (v2f_img i) : SV_Target {
    float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
    #if defined(UNITY_REVERSED_Z)
    bool sky = raw <= 0;
    #else
    bool sky = raw >= 1;
    #endif
    return sky ? 1e6 : LinearEyeDepth(raw);
   }
   ENDCG
  }
  // 3: the second eye's scene (_MainTex), rendered no further than _FarFill: past it (its depth there or beyond), the
  // first eye's (_Other), where that one too is that far (_OtherDepth, pass 2); two eyes see so far alike.
  Pass {
   Cull Off ZWrite Off ZTest Always
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
   sampler2D _MainTex, _Other, _OtherDepth;
   float _FarFill;
   float4 frag (v2f_img i) : SV_Target {
    float4 c = tex2D(_MainTex, i.uv);
    float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
    #if defined(UNITY_REVERSED_Z)
    bool past = raw <= 0 || LinearEyeDepth(raw) >= _FarFill * 0.96;
    #else
    bool past = raw >= 1 || LinearEyeDepth(raw) >= _FarFill * 0.96;
    #endif
    if (past && tex2D(_OtherDepth, i.uv).r >= _FarFill * 0.9)
     c.rgb = tex2D(_Other, i.uv).rgb;
    return c;
   }
   ENDCG
  }
 }
}
