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
   // The bare eye leading (TwoEyes.BareLeads; _BareOver: x how far, y that eye's side of the device's two views, 0 the
   // left's or 1 the right's): the device as that eye sees it stands before all the goggle's eye has, its eyepieces'
   // picture too, so where that view has the device (its own depth, NightVisionDirect.cs) the eyepieces are not laid.
   UNITY_DECLARE_DEPTH_TEXTURE(_DirectLD);
   UNITY_DECLARE_DEPTH_TEXTURE(_DirectRD);
   float4 _BareOver;
   // 1: nothing of the frame is shown around the eyepieces (sealed thermal goggles down, the frame the main camera's
   // own thermal one: all of it heat, the device drawn into it too; around the lenses is the inside of the cups, dark).
   float _SceneOff;
   float _Feather;   // of the frame's height: how far in from its rim an eyepiece laid over at half is eased out
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
    float3 seen = device + light.rgb * _Lamp.x + glow + (1 - saturate(eye.a)) * (1 - cover) * scene.rgb * (1 - _SceneOff);
    // The eyepieces (their picture and their glass) laid over the frame, whole but where the bare eye's view of the
    // device stands before them (below).
    // (_BareOver.z: how much of the eyepieces is laid over the frame, half while the bare eye leads; 0 not given: whole.)
    float share = _BareOver.z > 0 ? _BareOver.z : 1;
    // Laid over at less than whole (the bare eye leading), the eyepiece's rim is no longer hidden in the device's own
    // housing: its picture ended at a line against the bare eye's scene (the user's, 2026-11-27, 1.9.38: "这个圆有点太
    // 分明了吧"). An eyepiece a few centimetres before an eye that looks far off is far out of focus: its edge is eased
    // out over _Feather of the frame's height inward from the glass's rim, by how much glass lies about the pixel.
    if (share < 0.999) {
     float2 r = float2(_Feather * _MainTex_TexelSize.x / _MainTex_TexelSize.y, _Feather);
     float about = 0;
     for (int k = 0; k < 12; k++) {
      float a = k * 0.5235988;
      float2 at = i.uv + float2(cos(a), sin(a)) * r;
      about += step(0.02, max(tex2Dlod(_LeftGlass, float4(at, 0, 0)).r, tex2Dlod(_RightGlass, float4(at, 0, 0)).r));
     }
     for (int j = 0; j < 6; j++) {
      float a = j * 1.0471976 + 0.2617994;
      float2 at = i.uv + float2(cos(a), sin(a)) * r * 0.5;
      about += step(0.02, max(tex2Dlod(_LeftGlass, float4(at, 0, 0)).r, tex2Dlod(_RightGlass, float4(at, 0, 0)).r));
     }
     float soft = smoothstep(0.45, 1.0, about / 18.0);
     share *= lerp(1, soft, saturate((1 - share) * 2));
    }
    if (_BareOver.x > 0) {
     float raw = _BareOver.y > 0.5 ? SAMPLE_DEPTH_TEXTURE(_DirectRD, i.uv) : SAMPLE_DEPTH_TEXTURE(_DirectLD, i.uv);
     #if defined(UNITY_REVERSED_Z)
     float there = raw > 0 ? 1 : 0;
     #else
     float there = raw < 1 ? 1 : 0;
     #endif
     // (Its eyepiece's glass is of it too, though the model's own is not drawn: NvgDirect puts a dark one in there,
     // coverage.)
     float glassL = tex2D(_LeftGlass, i.uv).r, glassR = tex2D(_RightGlass, i.uv).r;
     there = max(there, (_BareOver.y > 0.5 ? glassR : glassL) > 0.02 ? 1 : 0);
     share *= 1 - _BareOver.x * there;
    }
    return float4(lerp(scene.rgb, seen, share), scene.a);
   }
   ENDCG
  }
 }
}
