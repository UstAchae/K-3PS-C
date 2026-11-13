// Shared by NvgBinocular.shader and NvgGlow.shader: one eye's view of the eyepieces at a pixel of the main frame.
#include "UnityCG.cginc"
sampler2D _MainTex, _NightVision, _Fused, _LeftEye, _RightEye, _LeftLens, _RightLens, _LeftGlass, _RightGlass, _Glow;
float4 _MainTex_TexelSize;
float4x4 _NightVisionVP;          // the main camera's view-projection (not jittered)
float4x4 _EyeInvVP0, _EyeInvVP1;  // each eye's half view, normalised device coordinates to world directions (its view turned, not moved)
float4 _EyePos0, _EyePos1;         // xyz each eye (world); w how strongly it sees the eyepieces' distortion
float4 _TubeAxis0, _TubeAxis1, _TubeAxis2, _TubeAxis3;   // where each tube looks (world)
float4 _TubeField;                // x cosine of the field's half angle, y of where it starts to fade
float4 _TubeFused;                // 1 for each tube that shows _Fused (another mod's picture laid into the tube's) instead of _NightVision
float _FuseOver;                  // 1: the right eye's lenses (the fused ones) laid over the left eye's where both reach
float _FuseSoft;                  // how far in from the right lens's edge it fades into the left's there (uv of the frame's height)
float4 _Power;                    // z frame
float4 _TubePower, _TubeMottle;   // each tube's output (0 off, 1 on), and how mottled while it settles
float _Overscan;                  // how far past the split each eye's view reaches, as a share of the frame
float _Split;                     // where across the frame the left eye's view gives way to the right's (0.5 with the camera between the eyes)
float4 _EyeSpan;                  // x the left eye's view's share of the frame's width, y the right's (1: the whole frame)
float4 _Monocular;                // x 1 for a monocular, whose one eye (y: 0 left, 1 right) sees the whole frame; the other half fades toward its edge, z as this power (0 not at all)
float4 _Lamp;                     // x the lenses' brightness, y the glow's, z the eyepieces' distortion, w the field angle it is given at (rad)
float _Mode;
float _LensClear;                 // 1: the lenses' own surface (its reflections, its highlights) left out over their picture

float4 _FusedRect;                // the other mod's picture (EyepieceFusion): xy its centre, zw its half width and height (uv); z 0: none

float3 nightVision (float3 ray, float fused) {
 float4 c = mul(_NightVisionVP, float4(ray, 0));
 float2 uv = c.xy / c.w * 0.5 + 0.5;
 float inside = step(0, c.w) * step(0, uv.x) * step(uv.x, 1) * step(0, uv.y) * step(uv.y, 1);
 float3 tube = tex2D(_NightVision, uv).rgb;
 // Within the fused picture's rectangle, it whatever lens is looked through: one whole rectangle, not cut where the
 // left eye's view takes over or at the edge of the right lens.
 float inRect = _FusedRect.z > 0 ? step(abs(uv.x - _FusedRect.x), _FusedRect.z) * step(abs(uv.y - _FusedRect.y), _FusedRect.w) : 0;
 if (fused > 0.5 || inRect > 0.5) tube = tex2D(_Fused, uv).rgb;
 return tube * inside;
}

float field (float3 ray, float4 axis) { return smoothstep(_TubeField.x, _TubeField.y, dot(ray, axis.xyz)); }

// An eyepiece's pincushion distortion: its magnification grows toward the edge of its field, so a line of sight that
// far off the tube's axis looks out a little nearer to it, and what lies there is drawn out larger. It shows the
// more the further back the eye stands (strength).
float3 through_eyepiece (float3 ray, float4 axis, float strength) {
 float c = clamp(dot(ray, axis.xyz), -1, 1);
 float3 across = ray - axis.xyz * c;
 float s = length(across);
 if (s < 1e-5) return ray;
 float angle = acos(c), t = angle / _Lamp.w;
 float seen = angle * (1 - _Lamp.z * strength * t * t);
 return axis.xyz * cos(seen) + across / s * sin(seen);
}

float hash (float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }
float blotches (float2 uv) {
 float2 cell = uv * float2(9, 5) + _Power.z * 0.37;
 float2 i = floor(cell), f = frac(cell);
 f = f * f * (3 - 2 * f);
 return lerp(lerp(hash(i), hash(i + float2(1, 0)), f.x), lerp(hash(i + float2(0, 1)), hash(i + 1), f.x), f.y);
}

// One eye's view at a pixel of the main frame: the device it drew, and the light reaching it through the lenses (its
// w how much of the pixel a lens lets light through, less toward the edge of the tube's field); cover how much of the
// pixel the lenses' glass covers (its own map: the glass whether or not light reaches it through the exit pupil).
// Each eye's view reaches a little past the split (_Overscan), so the two can be blended there.
void eyeSide (float2 uv, bool right, out float4 eye, out float4 light, out float cover, out float dark) {
 float span = right ? _EyeSpan.y : _EyeSpan.x;
 float2 local = float2((uv.x - (right ? 1 - span : 0)) / span, uv.y);
 eye = right ? tex2D(_RightEye, local) : tex2D(_LeftEye, local);
 float4 far = mul(right ? _EyeInvVP1 : _EyeInvVP0, float4(local * 2 - 1, 1, 1));
 float3 ray = normalize(far.xyz / far.w);
 float4 lens = right ? tex2D(_RightLens, local) : tex2D(_LeftLens, local);
 float4 open = lens * float4(field(ray, _TubeAxis0), field(ray, _TubeAxis1), field(ray, _TubeAxis2), field(ray, _TubeAxis3));
 float4 through = open * _TubePower * lerp(1, 0.35 + 0.9 * blotches(uv), _TubeMottle);
 float strength = right ? _EyePos1.w : _EyePos0.w;
 float3 picture = 0;
 if (through.x > 0) picture += through.x * nightVision(through_eyepiece(ray, _TubeAxis0, strength), _TubeFused.x);
 if (through.y > 0) picture += through.y * nightVision(through_eyepiece(ray, _TubeAxis1, strength), _TubeFused.y);
 if (through.z > 0) picture += through.z * nightVision(through_eyepiece(ray, _TubeAxis2, strength), _TubeFused.z);
 if (through.w > 0) picture += through.w * nightVision(through_eyepiece(ray, _TubeAxis3, strength), _TubeFused.w);
 light = float4(picture / max(dot(open, 1), 1), saturate(dot(open, 1)));
 float4 glass = right ? tex2D(_RightGlass, local) : tex2D(_LeftGlass, local);
 cover = smoothstep(0, 0.02, glass.r);
 // The light the glass would let through with the eye on the exit pupil (g): what is missing of it is the glass gone
 // dark, the eye off the pupil or looking too far off the tube's axis.
 dark = saturate(glass.g - light.w);
}

// How much of the right eye's fused lenses lies about this pixel: their lens map averaged over a ring _FuseSoft
// across (and the pixel itself), so 0.5 on the lens's edge, 1 a ring's width inside it.
float fusedCover (float2 uv) {
 float span = _EyeSpan.y;
 float2 local = float2((uv.x - (1 - span)) / span, uv.y);
 float aspect = _MainTex_TexelSize.z / max(_MainTex_TexelSize.w, 1);
 float2 reach = float2(_FuseSoft / aspect / span, _FuseSoft);
 float4 m = tex2Dlod(_RightLens, float4(local, 0, 0)) * _TubeFused;
 float sum = max(max(m.x, m.y), max(m.z, m.w));
 for (int k = 0; k < 12; k++) {
  float a = k * 0.5235988;
  m = tex2Dlod(_RightLens, float4(local + float2(cos(a), sin(a)) * reach, 0, 0)) * _TubeFused;
  sum += max(max(m.x, m.y), max(m.z, m.w));
 }
 return sum / 13;
}

// This pixel's eye: the left eye's on the left half, the right eye's on the right, eased from one to the other across
// the strip in the middle that both views reach, so the two eyes' slightly different views leave no crease there.
// A monocular's one eye everywhere, fading over the other eye's half: that eye sees past the device, and the two
// views are seen as one, the device the fainter the further out toward that eye's edge.
void eyeAt (float2 uv, out float4 eye, out float4 light, out float cover, out float dark) {
 float toRight = smoothstep(_Split - _Overscan * 0.5, _Split + _Overscan * 0.5, uv.x);
 if (_Monocular.x > 0.5) {
  bool right = _Monocular.y > 0.5;
  eyeSide(uv, right, eye, light, cover, dark);
  float shown = smoothstep(0, 0.5 + _Overscan * 0.5, right ? uv.x : 1 - uv.x);
  shown = _Monocular.z > 0 ? pow(max(shown, 1e-5), _Monocular.z) : 1;
  // Only the housing fades: the eyepiece's own picture (and its glass, which keeps the scene from showing through it) is
  // the tube's, seen whole wherever the lens is; faded with the housing, part of the eyepiece showed the bare scene.
  eye *= shown;
  return;
 }
 // The right eye's lenses carry another mod's picture (EyepieceFusion): where the two eyes' lenses meet, the right
 // eye's is laid over the left's, so that picture runs on across the middle into the left lens's circle instead of
 // being cut where the left eye's view takes over. Both lenses show the same scene; only the fused picture tells them
 // apart. Past the right lens, the left eye's view as ever.
 if (_FuseOver > 0.5) {
  float4 leftEye, rightEye; float4 leftLight, rightLight; float leftCover, rightCover, leftDark, rightDark;
  eyeSide(uv, false, leftEye, leftLight, leftCover, leftDark);
  eyeSide(uv, true, rightEye, rightLight, rightCover, rightDark);
  eye = lerp(leftEye, rightEye, toRight);
  float4 under = leftLight * (1 - toRight);
  // Over the left lens's picture the right lens fades out toward its edge (a ring's width inside it), so neither its
  // rim nor the fused picture's edge shows as a hard line across the left circle; where nothing lies beneath, as ever.
  float soft = saturate((fusedCover(uv) - 0.5) * 2);
  float4 over = rightLight * lerp(1, soft, saturate(under.w));
  light = over + under * (1 - over.w);
  cover = max(rightCover, lerp(leftCover, rightCover, toRight));
  dark = min(lerp(leftDark, rightDark, toRight), 1 - rightLight.w);
  return;
 }
 if (toRight <= 0) { eyeSide(uv, false, eye, light, cover, dark); return; }
 if (toRight >= 1) { eyeSide(uv, true, eye, light, cover, dark); return; }
 float4 leftEye, rightEye; float4 leftLight, rightLight; float leftCover, rightCover, leftDark, rightDark;
 eyeSide(uv, false, leftEye, leftLight, leftCover, leftDark);
 eyeSide(uv, true, rightEye, rightLight, rightCover, rightDark);
 eye = lerp(leftEye, rightEye, toRight);
 // Where the lenses' pictures meet, neither gives way to dark: a lens the other eye has nothing of counts for nothing, each shows by
 // how much light it has, and at the line's middle the brighter sets how bright it is. So no place shows where the two lenses cross.
 float a = leftLight.w * (1 - toRight), b = rightLight.w * toRight;
 if (a + b <= 0) {
  light = lerp(leftLight, rightLight, toRight);
  cover = lerp(leftCover, rightCover, toRight);
  dark = lerp(leftDark, rightDark, toRight);
  return;
 }
 float weighted = (a * leftLight.w + b * rightLight.w) / (a + b);
 float shown = lerp(weighted, max(leftLight.w, rightLight.w), 4 * toRight * (1 - toRight));
 light = float4(((1 - toRight) * leftLight.rgb + toRight * rightLight.rgb) / (a + b) * shown, shown);
 cover = max(leftCover, rightCover);
 dark = min(leftDark, rightDark);
}

void eyeAt (float2 uv, out float4 eye, out float4 light, out float cover) {
 float dark;
 eyeAt(uv, eye, light, cover, dark);
}
