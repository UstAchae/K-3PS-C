// The eyepieces put together, right where BRNVG would draw its picture over the main camera's frame. _MainTex is
// the frame as BRNVG got it (the naked eye's scene) and _NightVision what BRNVG made of it (open lens, no mask):
// the tubes are 1x, so each line of sight through a lens shows the night-vision picture in its own direction.
// On the left half of the screen the left eye's view, on the right half the right eye's (with one camera both are
// where it is). Each eye drew its own half of the main view from its own place: the device alone (premultiplied,
// its coverage in alpha) and its lens map; the scene is the main camera's wherever the device does not cover it.
// A tube shows only its own field: a line of sight further off its axis than that finds no picture, so from the
// side a lens is dark glass.
// The eyepieces are lamps in the dark: their light, spread wide and soft (NvgGlow.shader), is laid back over
// everything, a glow on the glass and the eyecups around the lenses. It all follows the tubes' power: rising over a
// moment when switched on, mottled while the gain settles, and fading like phosphor when switched off.
// One pass only: the game's SSAA stage blits with every pass of a material.
// While the device flips with the filter off the eyes are put in at the SSAA stage instead, _NightVision black.
Shader "Justachae/NVG/Binocular" {
 Properties { _MainTex ("scene", 2D) = "black" {} }
 CGINCLUDE
 #include "NvgEyes.cginc"
 ENDCG
 SubShader {
  Cull Off ZWrite Off ZTest Always
  // 0: the eyepieces over the scene.
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.0
   float4 frag (v2f_img i) : SV_Target {
    float4 scene = tex2D(_MainTex, i.uv);
    if (_Mode < 0.5) return scene;
    float4 eye; float4 light; float cover, dark;
    eyeAt(i.uv, eye, light, cover, dark);
    // The glow falls on the eyecups and the housing about the lenses, not on the lenses' own picture (over it, it
    // only greyed the picture).
    float3 glow = tex2D(_Glow, i.uv).rgb * _Lamp.y * (1 - light.w);
    // The lenses' own surface as the eye drew it (the room reflected in it, highlights) over their picture, unless left
    // out: then all of the glass, to its rim, where the tube's field has faded the picture out. Where the line of sight
    // misses the exit pupil the glass has no picture behind it and is dark, as an eyepiece is seen from off its pupil:
    // the game's own glass (its green glow) is left out there too, as much as the light is missing.
    float3 device = eye.rgb * (1 - saturate(cover * _LensClear + (1 - _LensClear) * dark));
    // The scene only past the device and never through a lens: behind the glass is the tube, so where its picture
    // falls short (the line of sight off the pupil, between the two eyes' views) the glass is dark, not a window.
    return float4(device + light.rgb * _Lamp.x + glow + (1 - saturate(eye.a)) * (1 - cover) * scene.rgb, scene.a);
   }
   ENDCG
  }
 }
}
