// The game's night-vision picture in the lenses, made brighter as a tube's gain would make it, before the game's filter
// greens it: the light at each pixel (its luminance; the colour's mix kept) times _TubeGain, and, by _TubeCompress, the
// bright end pressed toward the tube's ceiling as its output saturates (c (1 - e^-x/c), which keeps the gain where it
// is dark) instead of going on straight. The ceiling (_TubeCeiling) is the light the game's filter shows as white: it
// multiplies the light again by the device's own intensity (2.27 for the AN/PVS-14), so with a ceiling of 1 what
// was pressed was white already. _TubeGain 1 with _TubeCompress 0 gives the frame unchanged.
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
   float _TubeCeiling;   // the light the game's filter shows as white

   float4 frag (v2f_img i) : SV_Target {
    float4 c = tex2D(_MainTex, i.uv);
    float light = dot(c.rgb, float3(0.2126, 0.7152, 0.0722));
    if (light <= 1e-6)
     return c;
    float straight = _TubeGain * light;
    float ceiling = max(_TubeCeiling, 1e-3);
    float saturated = ceiling * (1.0 - exp(-straight / ceiling));
    float now = lerp(straight, saturated, saturate(_TubeCompress));
    c.rgb *= now / light;
    return c;
   }
   ENDCG
  }
 }
}
