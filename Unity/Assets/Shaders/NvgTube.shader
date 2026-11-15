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
   sampler2D _MainTex, _Other, _DirectNearL, _DirectNearR, _EyeRegionL, _EyeRegionR;
   float _OtherShare, _GunSolidHalves, _DirectAimRight;
   float4 frag (v2f_img i) : SV_Target {
    float4 c = tex2D(_MainTex, i.uv);
    float share = _OtherShare;
    // The experiment's two eyes (_MainTex the left's, _Other the right's), the gun not see-through: the aiming eye's
    // alone where its own gun or arm is.
    // And the goggle's picture not see-through either: where one eye's own eyepiece's picture is, that eye's alone;
    // where both eyes' are, the aiming eye's (the two pictures never laid over each other).
    if (_GunSolidHalves > 0.5) {
     float aim = _DirectAimRight > 0.5 ? 1 : 0;
     bool inL = tex2D(_EyeRegionL, i.uv).r > 0.5, inR = tex2D(_EyeRegionR, i.uv).r > 0.5;
     if ((_DirectAimRight > 0.5 ? tex2D(_DirectNearR, i.uv).r : tex2D(_DirectNearL, i.uv).r) >= 0.5 || inL && inR)
      share = aim;
     else if (inL)
      share = 0;
     else if (inR)
      share = 1;
    }
    c.rgb = lerp(c.rgb, tex2D(_Other, i.uv).rgb, share);
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
   sampler2D _MainTex, _Other, _AimLensRegion;
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
    // Only inside the aiming eye's own eyepiece's picture region (from where that eye stands): outside it the eye
    // looks past the eyepiece and sees the sight as it is.
    bool tubed = _AimBare < 0.5 && tex2D(_AimLensRegion, i.uv).r > 0.5;
    float3 seen = tubed ? dot(laid.rgb, float3(0.2126, 0.7152, 0.0722)) * _ScopeDim * _Phosphor.rgb : laid.rgb;
    return float4(lerp(before.rgb, seen, _OtherShare), laid.a);
   }
   ENDCG
  }
  // 3: the experiment: one eye's frame after the game's post-processing, from the frame made of the two eyes half each
  // (_MainTex) and both eyes' lit frames before it (_EyePre this eye's, _OtherPre the other's): the blend scaled, at each
  // pixel, by how this eye's stands to the two's mean (the same where they agree: the scene).
  Pass {
   Cull Off ZWrite Off ZTest Always
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex, _EyePre, _OtherPre, _DirectNearL, _DirectNearR;
   float _EyeSide, _GunSolid, _DirectAimRight;
   float4 frag (v2f_img i) : SV_Target {
    float4 blend = tex2D(_MainTex, i.uv);
    float3 eye = tex2D(_EyePre, i.uv).rgb, other = tex2D(_OtherPre, i.uv).rgb;
    // This eye's share of the blend, as NvgDirect pass 16 made it.
    float share = 0.5;
    if (_GunSolid > 0.5 && (_DirectAimRight > 0.5 ? tex2D(_DirectNearR, i.uv).r : tex2D(_DirectNearL, i.uv).r) >= 0.5)
     share = (_EyeSide > 0.5) == (_DirectAimRight > 0.5) ? 1 : 0;
    float3 mean = share * eye + (1 - share) * other;
    // No fixed floor: a night scene's lit frame lies far below any. Where the two are both black, black.
    float3 ratio = mean > 1e-9 ? clamp(eye / max(mean, 1e-9), 0, 8) : float3(0, 0, 0);
    return float4(blend.rgb * ratio, blend.a);
   }
   ENDCG
  }
  // 4: a picture as it is, opaque (the experiment's probes, drawn over the frame).
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
 }
}
