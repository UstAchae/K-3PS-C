// The IR layer: what IR lights and lasers add to the scene, kept apart from the frame so that only the lenses show it.
// 0: an IR spot light on the frame's surfaces, as the game's deferred pass lights them (Unity's standard BRDF on the
//    G-buffer, the light's cookie and falloff), added into the layer. Shadowed lights are drawn right after their
//    shadow map, which is then the current target's: the view-projection still set is the one it was drawn with.
// 1: the light that reaches the tube (ForTube): the frame with the layer added (visible lasers are in the frame at their
//    own light, which the tube amplifies); over a magnified sight's lens looked through, the layer left out and the sight's light at the
//    share of it that reaches the tube (_ScopeLight), the one place it is cut.
// 2: lasers drawn apart added onto the upscaled frame.
// 3: the layer held over frames as TAA holds a frame: this frame's (its jitter taken out) blended into the last
//    frame's, followed along the motion vectors and kept within what this frame has about the pixel. The layer is
//    drawn from the jittered G-buffer but added after the upscale, which does not steady it.
Shader "Justachae/NVG/IrLight" {
 Properties { _MainTex ("frame", 2D) = "black" {} }
 SubShader {
  Cull Off ZWrite Off ZTest Always
  Pass {
   Blend One One
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #pragma target 4.5
   #pragma multi_compile_local __ IR_SHADOWS IR_SOFT_SHADOWS
   #include "UnityCG.cginc"
   #include "UnityPBSLighting.cginc"
   #include "UnityGBuffer.cginc"
   #include "UnityShadowLibrary.cginc"

   sampler2D _CameraGBufferTexture0, _CameraGBufferTexture1, _CameraGBufferTexture2;
   sampler2D_float _CameraDepthTexture;
   sampler2D _IrCookie, _IrFalloff;
   float4x4 _IrClipToWorld;     // the main camera's (as it drew the G-buffer: jittered, into a texture)
   float4 _IrScreen;            // its size in pixels, and one over
   float4 _IrCamera;            // xyz where it is
   float4 _IrForward;           // xyz where it looks
   float4 _IrLightPos;          // xyz the light, w one over its range squared
   float4 _IrLightColor;        // linear
   float4x4 _IrWorldToLight;    // the light's frame, not scaled (z along the beam)
   float4 _IrSpot;              // x the cotangent of half the spot angle; y one less the shadow's strength
   #if defined(IR_SHADOWS) || defined(IR_SOFT_SHADOWS)
   UNITY_DECLARE_SHADOWMAP(_IrShadowMap);
   #endif

   struct v2f { float4 pos : SV_POSITION; };
   v2f vert (uint id : SV_VertexID) {
    float2 p = float2((id << 1) & 2, id & 2);
    v2f o; o.pos = float4(p * 2 - 1, 0, 1); return o;
   }

   #if defined(IR_SHADOWS) || defined(IR_SOFT_SHADOWS)
   // As Unity samples a spot light's shadow (UnitySampleShadowmap): one comparison, or the 3x3 tent of 4 bilinear ones.
   half irShadow (float3 coord) {
    #if defined(IR_SOFT_SHADOWS)
     float w, h; _IrShadowMap.GetDimensions(w, h);
     float4 texel = float4(1 / w, 1 / h, w, h);
     float2 tentCenter = coord.xy * texel.zw;
     float2 fetchCenter = floor(tentCenter + 0.5);
     float2 offset = tentCenter - fetchCenter;
     float4 weightsU, weightsV;
     _UnityInternalGetWeightPerTexel_3TexelsWideTriangleFilter(offset.x, weightsU);
     _UnityInternalGetWeightPerTexel_3TexelsWideTriangleFilter(offset.y, weightsV);
     float2 fetchU = weightsU.xz + weightsU.yw, fetchV = weightsV.xz + weightsV.yw;
     float2 offU = (weightsU.yw / fetchU + float2(-1.5, 0.5)) * texel.xx;
     float2 offV = (weightsV.yw / fetchV + float2(-1.5, 0.5)) * texel.yy;
     float2 origin = fetchCenter * texel.xy;
     half s = fetchU.x * fetchV.x * UNITY_SAMPLE_SHADOW(_IrShadowMap, float3(origin + float2(offU.x, offV.x), coord.z));
     s += fetchU.y * fetchV.x * UNITY_SAMPLE_SHADOW(_IrShadowMap, float3(origin + float2(offU.y, offV.x), coord.z));
     s += fetchU.x * fetchV.y * UNITY_SAMPLE_SHADOW(_IrShadowMap, float3(origin + float2(offU.x, offV.y), coord.z));
     s += fetchU.y * fetchV.y * UNITY_SAMPLE_SHADOW(_IrShadowMap, float3(origin + float2(offU.y, offV.y), coord.z));
     return s;
    #else
     return UNITY_SAMPLE_SHADOW(_IrShadowMap, coord);
    #endif
   }
   #endif

   half4 frag (v2f i) : SV_Target {
    float2 uv = i.pos.xy * _IrScreen.zw;
    float depth = tex2Dlod(_CameraDepthTexture, float4(uv, 0, 0)).r;
    // Nothing drawn there (reversed depth: the far plane at 0).
    if (depth <= 0) return 0;
    float4 w = mul(_IrClipToWorld, float4(uv.x * 2 - 1, 1 - uv.y * 2, depth, 1));
    float3 wpos = w.xyz / w.w;

    float3 local = mul(_IrWorldToLight, float4(wpos, 1)).xyz;
    if (local.z <= 0) return 0;
    // Unity's spot cookie: through the spot angle's perspective, mirrored (unity_WorldToLight's w is -z).
    float2 cookie = 0.5 - 0.5 * _IrSpot.x * local.xy / local.z;
    float3 tolight = _IrLightPos.xyz - wpos;
    float atten = tex2Dlod(_IrCookie, float4(cookie, 0, 0)).w;
    atten *= tex2Dlod(_IrFalloff, float4((dot(tolight, tolight) * _IrLightPos.w).xx, 0, 0)).r;
    if (atten <= 0) return 0;

    #if defined(IR_SHADOWS) || defined(IR_SOFT_SHADOWS)
     float4 s = mul(UNITY_MATRIX_VP, float4(wpos, 1));
     float3 coord = float3(s.x / s.w * 0.5 + 0.5, 0.5 - s.y / s.w * 0.5, s.z / s.w);
     half shadow = lerp(_IrSpot.y, 1, irShadow(coord));
     float fadeDist = UnityComputeShadowFadeDistance(wpos, dot(wpos - _IrCamera.xyz, _IrForward.xyz));
     atten *= saturate(shadow + UnityComputeShadowFade(fadeDist));
    #endif

    half4 g0 = tex2Dlod(_CameraGBufferTexture0, float4(uv, 0, 0));
    half4 g1 = tex2Dlod(_CameraGBufferTexture1, float4(uv, 0, 0));
    half4 g2 = tex2Dlod(_CameraGBufferTexture2, float4(uv, 0, 0));
    UnityStandardData data = UnityStandardDataFromGbuffer(g0, g1, g2);
    float3 eyeVec = normalize(wpos - _IrCamera.xyz);
    half oneMinusReflectivity = 1 - SpecularStrength(data.specularColor.rgb);
    UnityLight light;
    light.color = _IrLightColor.rgb * atten;
    light.dir = normalize(tolight);
    UnityIndirect ind;
    UNITY_INITIALIZE_OUTPUT(UnityIndirect, ind);
    ind.diffuse = 0;
    ind.specular = 0;
    half4 res = UNITY_BRDF_PBS(data.diffuseColor, data.specularColor, oneMinusReflectivity, data.smoothness, data.normalWorld, -eyeVec, light, ind);
    return half4(res.rgb, 0);
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex, _IrLayer, _ScopeLens;
   float _ScopeOn, _ScopeLight;
   float4 frag (v2f_img i) : SV_Target {
    float4 frame = tex2D(_MainTex, i.uv);
    // A magnified sight's lens (_ScopeOn, NightVisionScope.cs): the sight's own picture there, with the lasers and IR its
    // camera sees, at the share of its light that reaches the tube; the layer's, past the sight as seen from the eye, left out.
    if (_ScopeOn > 0.5 && tex2D(_ScopeLens, i.uv).r > 0.5)
     return float4(frame.rgb * _ScopeLight, frame.a);
    return float4(frame.rgb + tex2D(_IrLayer, i.uv).rgb, frame.a);
   }
   ENDCG
  }
  Pass {
   Blend One One
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex;
   float4 frag (v2f_img i) : SV_Target { return float4(tex2D(_MainTex, i.uv).rgb, 0); }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex, _IrHistory, _CameraMotionVectorsTexture;
   float4 _MainTex_TexelSize;
   float4 _IrJitter;           // xy this frame's jitter (uv), z 1 with a history to follow, w how much of this frame goes in
   float4 frag (v2f_img i) : SV_Target {
    float2 uv = i.uv - _IrJitter.xy;
    float3 c = tex2D(_MainTex, uv).rgb;
    float3 lo = c, hi = c;
    [unroll] for (int y = -1; y <= 1; y++)
     [unroll] for (int x = -1; x <= 1; x++) {
      float3 n = tex2D(_MainTex, uv + float2(x, y) * _MainTex_TexelSize.xy).rgb;
      lo = min(lo, n);
      hi = max(hi, n);
     }
    float2 prev = i.uv - tex2D(_CameraMotionVectorsTexture, i.uv).xy;
    if (_IrJitter.z < 0.5 || any(prev < 0) || any(prev > 1))
     return float4(c, 0);
    float3 h = clamp(tex2D(_IrHistory, prev).rgb, lo, hi);
    return float4(lerp(h, c, _IrJitter.w), 0);
   }
   ENDCG
  }
 }
}
