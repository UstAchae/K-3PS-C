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
  // 1: the frame and another (_Other, the bare eye's view: NightVisionOtherEye.cs), mixed by _OtherShare.
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
  // 2: a sight's lens as the two eyes see it (SightLensHalves.cs): _MainTex the frame with the lens laid in, _Other
  // without. Where the lens is (the two differ): the aiming eye's view, its picture, mixed by _OtherShare with the other
  // eye's (what lay behind); the game lays it in after the eyepieces' picture is made, so through an eyepiece it is made
  // the tube's here. Elsewhere the frame as it is.
  Pass {
   Cull Off ZWrite Off ZTest Always
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex, _Other;
   float _OtherShare, _AimBare, _ScopeDim;
   float4 _Phosphor;
   float4 frag (v2f_img i) : SV_Target {
    float4 laid = tex2D(_MainTex, i.uv), before = tex2D(_Other, i.uv);
    float3 d = abs(laid.rgb - before.rgb);
    if (max(d.r, max(d.g, d.b)) < 1e-4)
     return laid;
    // The aiming eye's view of it: bare, as the game draws it; through its eyepiece, the tube's picture of it (the
    // light in the tube's colour, dimmed by the sight's exit pupil on the goggle's objective). Half each with the other
    // eye's view (what lay behind), as everywhere.
    float3 seen = _AimBare > 0.5 ? laid.rgb : dot(laid.rgb, float3(0.2126, 0.7152, 0.0722)) * _ScopeDim * _Phosphor.rgb;
    return float4(lerp(before.rgb, seen, _OtherShare), laid.a);
   }
   ENDCG
  }
 }
}
