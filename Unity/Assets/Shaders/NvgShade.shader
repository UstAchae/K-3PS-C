// The insides of a sealed eyecup (the T-7's, the N-15's) going dark as the device comes down against the face: once
// the game has lit the frame, its light on what lies within each eyecup (the lens and the cup's inner wall up to its
// flared rim, found by the eyecup's shape measured from the model about its axis) is kept to _ShadeKeep, the sky's
// reflection and the near glints with the rest. The outer wall, facing away from the axis, is left as it is. What the
// lens itself lights in there is put back: the lens a disc of light on its floor, sending it out over its field and,
// scattered, a little to every side; and the face closing the cup at its rim, lit by the lens straight on, a disc as
// wide as the rim sending a share of that back down into the cup every way. Each surface within the cup gets what it
// sees of either disc (taken at points over it, each by its angle from the disc, its angle onto the surface and its
// distance), as a matt surface of the colour the G-buffer gives it.
Shader "Justachae/NVG/Shade" {
 SubShader {
  Pass {
   Cull Off ZWrite Off ZTest Always
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #pragma target 3.5
   #include "UnityCG.cginc"
   sampler2D _CameraDepthTexture, _CameraGBufferTexture0, _CameraGBufferTexture2;
   sampler2D _ShadeNow;          // the frame as lit
   sampler2D _ShadePlace, _ShadeFacing;   // where the eyes put the device in themselves: its place from the eye (w 1 where drawn) and its normal
   float _ShadePlaced;          // 1 where those are given (an eye drawing the device itself), 0 to work it out from the depth
   float _ShadeLeftCup;         // which eyecup is the left eye's half's, where they are given
   float4x4 _ShadeInvProj;      // this camera's projection, inverted (not jittered)
   float4x4 _ShadeViewToWorld;  // its view turned to the world's axes (no translation)
   float4 _ShadeCup0, _ShadeCup1;     // xyz each eyecup's lens centre from this camera; w 1 where there is one
   float4 _ShadeAxis0, _ShadeAxis1;   // each eyecup's axis, toward the eye
   float4 _ShadeShape;          // x the floor below the centre, y the eyecup's top above it (m)
   float4 _ShadeRadii0, _ShadeRadii1;   // the cup's inner radius at 0, 5, 10, 20 mm and at 30, 40 mm above the centre (m)
   float _ShadeKeep;            // how much of the light is kept within the eyecups
   float4 _ShadeLens;           // x the lens's radius (m), y the cosine of half its field, z its edge's softness (as a cosine), w the share scattered to every side
   float4 _ShadeLight0, _ShadeLight1;   // each eyecup's lens's light (linear rgb, its radiance)
   float _ShadeSkin;            // the share of the light on the face it sends back

   struct v2f { float4 pos : SV_POSITION; };
   v2f vert (uint id : SV_VertexID) {
    v2f o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * 2 - 1, 0, 1);
    return o;
   }

   // The cup's inner radius at t above the lens centre (measured at 0, 5, 10, 20, 30 and 40 mm, in between straight).
   float cupRadius (float t) {
    return t < 0.005 ? lerp(_ShadeRadii0.x, _ShadeRadii0.y, saturate(t / 0.005))
         : t < 0.01 ? lerp(_ShadeRadii0.y, _ShadeRadii0.z, (t - 0.005) / 0.005)
         : t < 0.02 ? lerp(_ShadeRadii0.z, _ShadeRadii0.w, (t - 0.01) / 0.01)
         : t < 0.03 ? lerp(_ShadeRadii0.w, _ShadeRadii1.x, (t - 0.02) / 0.01)
         : lerp(_ShadeRadii1.x, _ShadeRadii1.y, saturate((t - 0.03) / 0.01));
   }

   // Whether the surface at p (normal n) lies within the eyecup about centre c and axis a, facing in.
   bool within (float3 p, float3 n, float3 c, float3 a) {
    float3 d = p - c;
    float t = dot(d, a);
    if (t < -_ShadeShape.x - 0.002 || t > _ShadeShape.y + 0.002) return false;
    float3 across = d - a * t;
    float r = length(across);
    float radius = cupRadius(t);
    if (r > radius + 0.0015) return false;
    // The inner wall faces the axis; the lens, the floor and the steps within the cup face the eye (the steps of the
    // rings round the tube below the lens face it too, out by the wall); the outer wall faces away from both.
    return r < 1e-4 || dot(n, -across / r) > 0.3 || dot(n, a) > 0.5 && (r < radius * 0.8 || t > 0.0005 && r < radius);
   }

   // What a disc of light (centre o, facing a, radius r) sheds on the surface at p facing n: irradiance. field: the
   // cosine of half the cone it sends most into (-1: every way alike).
   float3 discLight (float3 p, float3 n, float3 o, float3 a, float r, float3 radiance, float field) {
    float3 u = normalize(abs(a.y) < 0.9 ? cross(a, float3(0, 1, 0)) : cross(a, float3(1, 0, 0)));
    float3 v = cross(a, u);
    float area = 3.14159265 * r * r / 19;
    float sum = 0;
    // 19 points spread evenly over the disc: its centre, a ring of 6 and a ring of 12, each standing for an equal share.
    [unroll] for (int k = 0; k < 19; k++) {
     float ring = k == 0 ? 0 : k < 7 ? 0.5 : 0.8660254;
     float turn = k == 0 ? 0 : k < 7 ? (k - 1) * 1.0471976 : (k - 7) * 0.5235988 + 0.2617994;
     float3 q = o + r * ring * (u * cos(turn) + v * sin(turn));
     float3 d = p - q;
     float dist2 = max(dot(d, d), 1e-6);
     float3 dir = d * rsqrt(dist2);
     float out_ = dot(dir, a), onto = dot(n, -dir);
     if (out_ <= 0 || onto <= 0) continue;
     float share = field < -0.5 ? 1 : smoothstep(field - _ShadeLens.z, field + _ShadeLens.z, out_) + _ShadeLens.w;
     sum += share * out_ * onto / dist2;
    }
    return radiance * sum * area;
   }

   // The lens about centre c (axis a, on the floor below it) and the face it lights at the cup's rim.
   float3 lensLight (float3 p, float3 n, float3 c, float3 a, float3 radiance) {
    float3 lens = c - a * _ShadeShape.x;
    float r = _ShadeLens.x, up = _ShadeShape.x + _ShadeShape.y;
    // On the face, straight over the lens: a disc's light on its axis.
    float3 onFace = radiance * 3.14159265 * r * r / (r * r + up * up);
    return discLight(p, n, lens, a, r, radiance, _ShadeLens.y)
         + discLight(p, n, c + a * _ShadeShape.y, -a, cupRadius(_ShadeShape.y), onFace * _ShadeSkin / 3.14159265, -1);
   }

   float4 frag (v2f i) : SV_Target {
    float2 uv = i.pos.xy / _ScreenParams.xy;
    float4 now = tex2D(_ShadeNow, uv);
    float3 p, n;
    bool may0 = true, may1 = true;
    if (_ShadePlaced > 0.5) {
     float4 placed = tex2D(_ShadePlace, uv);
     if (placed.w < 0.5) return now;
     p = placed.xyz;
     n = normalize(tex2D(_ShadeFacing, uv).xyz);
     // Each half stands from its own eye: only its own eyecup is where it is, the other's coming to the same place only as the two are
     // brought together. One eyecup alone (a device with one) is both's.
     if (_ShadeCup1.w > 0) {
      may0 = (placed.w < 1.5) == (_ShadeLeftCup < 0.5);
      may1 = !may0;
     }
    } else {
     float depth = LinearEyeDepth(tex2D(_CameraDepthTexture, uv).r);
     float4 v = mul(_ShadeInvProj, float4(uv * 2 - 1, 1, 1));
     float3 ray = v.xyz / v.w;
     p = mul((float3x3)_ShadeViewToWorld, ray * (depth / -ray.z));
     n = normalize(tex2D(_CameraGBufferTexture2, uv).xyz * 2 - 1);
    }
    bool in0 = may0 && _ShadeCup0.w > 0 && within(p, n, _ShadeCup0.xyz, _ShadeAxis0.xyz);
    bool in1 = may1 && _ShadeCup1.w > 0 && within(p, n, _ShadeCup1.xyz, _ShadeAxis1.xyz);
    if (!in0 && !in1) return now;
    float3 lit = (in0 ? lensLight(p, n, _ShadeCup0.xyz, _ShadeAxis0.xyz, _ShadeLight0.rgb) : 0)
               + (in1 ? lensLight(p, n, _ShadeCup1.xyz, _ShadeAxis1.xyz, _ShadeLight1.rgb) : 0);
    float3 albedo = tex2D(_CameraGBufferTexture0, uv).rgb;
    return float4(now.rgb * _ShadeKeep + albedo / 3.14159265 * lit * (1 - _ShadeKeep), now.a);
   }
   ENDCG
  }

  // 1: where an eye puts the device in itself (NightVisionPrecise.cs), its place from the eye (its object-to-world is
  // from the eye there) and its normal, the nearest of the pieces kept by a depth of their own.
  Pass {
   Cull Off ZWrite On ZTest LEqual
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   struct v2f { float4 pos : SV_POSITION; float3 place : TEXCOORD0; float3 n : TEXCOORD1; };
   v2f vert (float4 v : POSITION, float3 n : NORMAL) {
    v2f o;
    o.pos = UnityObjectToClipPos(v);
    o.place = mul(unity_ObjectToWorld, v).xyz;
    o.n = UnityObjectToWorldNormal(n);
    return o;
   }
   struct O { float4 place : SV_Target0; float4 n : SV_Target1; };
   O frag (v2f i) { O o; o.place = float4(i.place, 1); o.n = float4(normalize(i.n), 0); return o; }
   ENDCG
  }
 }
}
