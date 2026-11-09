// Shared by NvgBinocular.shader and NvgGlow.shader: one eye's view of the eyepieces at a pixel of the main frame.
#include "UnityCG.cginc"
sampler2D _MainTex, _NightVision, _LeftEye, _RightEye, _LeftLens, _RightLens, _LeftGlass, _RightGlass, _Glow;
float4 _MainTex_TexelSize;
float4x4 _NightVisionVP;          // the main camera's view-projection (not jittered)
float4x4 _EyeInvVP0, _EyeInvVP1;  // each eye's half view, normalised device coordinates to world directions (its view turned, not moved)
float4 _EyePos0, _EyePos1;         // xyz each eye (world); w how strongly it sees the eyepieces' distortion
float4 _TubeAxis0, _TubeAxis1, _TubeAxis2, _TubeAxis3;   // where each tube looks (world)
float4 _TubeField;                // x cosine of the field's half angle, y of where it starts to fade
float4 _Power;                    // z frame
float4 _TubePower, _TubeMottle;   // each tube's output (0 off, 1 on), and how mottled while it settles
float _Overscan;                  // how far past the split each eye's view reaches, as a share of the frame
float _Split;                     // where across the frame the left eye's view gives way to the right's (0.5 with the camera between the eyes)
float4 _EyeSpan;                  // x the left eye's view's share of the frame's width, y the right's (1: the whole frame)
float4 _Monocular;                // x 1 for a monocular, whose one eye (y: 0 left, 1 right) sees the whole frame; the other half fades toward its edge, z as this power (0 not at all)
float4 _Lamp;                     // x the lenses' brightness, y the glow's, z the eyepieces' distortion, w the field angle it is given at (rad)
float _Mode;
float _LensClear;                 // 1: the lenses' own surface (its reflections, its highlights) left out over their picture

float3 nightVision (float3 ray) {
 float4 c = mul(_NightVisionVP, float4(ray, 0));
 float2 uv = c.xy / c.w * 0.5 + 0.5;
 float inside = step(0, c.w) * step(0, uv.x) * step(uv.x, 1) * step(0, uv.y) * step(uv.y, 1);
 return tex2D(_NightVision, uv).rgb * inside;
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
 if (through.x > 0) picture += through.x * nightVision(through_eyepiece(ray, _TubeAxis0, strength));
 if (through.y > 0) picture += through.y * nightVision(through_eyepiece(ray, _TubeAxis1, strength));
 if (through.z > 0) picture += through.z * nightVision(through_eyepiece(ray, _TubeAxis2, strength));
 if (through.w > 0) picture += through.w * nightVision(through_eyepiece(ray, _TubeAxis3, strength));
 light = float4(picture / max(dot(open, 1), 1), saturate(dot(open, 1)));
 float4 glass = right ? tex2D(_RightGlass, local) : tex2D(_LeftGlass, local);
 cover = smoothstep(0, 0.02, glass.r);
 // The light the glass would let through with the eye on the exit pupil (g): what is missing of it is the glass gone
 // dark, the eye off the pupil or looking too far off the tube's axis.
 dark = saturate(glass.g - light.w);
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
  eye *= shown;
  light *= shown;
  cover *= shown;
  dark *= shown;
  return;
 }
 if (toRight <= 0) { eyeSide(uv, false, eye, light, cover, dark); return; }
 if (toRight >= 1) { eyeSide(uv, true, eye, light, cover, dark); return; }
 float4 leftEye, rightEye; float4 leftLight, rightLight; float leftCover, rightCover, leftDark, rightDark;
 eyeSide(uv, false, leftEye, leftLight, leftCover, leftDark);
 eyeSide(uv, true, rightEye, rightLight, rightCover, rightDark);
 eye = lerp(leftEye, rightEye, toRight);
 light = lerp(leftLight, rightLight, toRight);
 cover = lerp(leftCover, rightCover, toRight);
 dark = lerp(leftDark, rightDark, toRight);
}

void eyeAt (float2 uv, out float4 eye, out float4 light, out float cover) {
 float dark;
 eyeAt(uv, eye, light, cover, dark);
}
