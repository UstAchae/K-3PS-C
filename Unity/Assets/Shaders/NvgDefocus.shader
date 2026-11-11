// The objective's defocus (NightVisionDefocus.cs): a point d from a lens of aperture D focused at s spread over a disc
// of even light, diameter D·|1/d − 1/s| seen from the lens; nearer, easing toward a most (the lens's own grows without
// end, and a thing a few centimetres off is spread over the whole field).
// _DefocusLens: x 1/s; y a disc's radius in quarter-size pixels per unit of |1/d − 1/s| (half the aperture over the
// quarter-size pixel's angle); z how far the objective is ahead of the camera; w the radius it eases toward.
// What is near: spread by more than a quarter-size pixel. A magnified sight's lens (its picture at infinity) is not; nor
// what is seen through a sight's window or any opening: there the frame has what lies beyond.
// Pass 1: what is near, at a quarter of the size, premultiplied by how much of the sixteen pixels under it it covers
//         times how near it is (Front: the nearer the more).
// Pass 2: its discs' radius: red the radius times the cover, green the cover (halved as the levels are, the radius
//         stays the cover's mean).
// Pass 3: what is not near, the same way.
// Pass 4: each pixel of a level drawn as its disc (procedural, six vertices a pixel), added; only radii in its band.
// Pass 5: the frame as the lens lays it on the tube: what is near, spread, over what lies behind it, as solid as
//         its own shape blurred wide (_Shape: a soft outline, a thin thing gone). Behind a near thing the frame has only the
//         thing itself: what is about it, filled in from its edge inward (finest first), stands for what lies behind it.
// Pass 6: how much of each part of the frame is spread (0 to 1), for the tube's grain (EaseGrain).
// Pass 8: a Gaussian of four texels' spread along _Step (the near shape's soft outline, NightVisionDefocus.cs).
// Pass 7: a level halved: four bilinear taps a texel apart, the four by four under it weighted as a tent, smoother
//         from frame to frame than the two by two alone.
// Pass 9: the frame at a quarter of the size, and how near (alpha). Pass 10: the disc's radius (r), how near (g),
//         how near times Front (b). Pass 11: a square min (_Mode 0) or max (1) of red along _Step, _Radius texels.
// Pass 12: 1 where green is above _Thresh. Pass 13: red times _Scale, added. Pass 14: the tuner's layers (_Which 0
//         the thick parts' colour by weight, 1 the thin parts', 2 the thick parts' cover and what they close about
//         (r) and the thin parts' (g)), each only its band's share (_BandHat). Pass 15: a Gaussian of _Sigma texels
//         along _Step. Pass 16: added as it is. Pass 17: the mean of this frame's and the last frame's (_Prev).
// Pass 0 is left empty, so the others keep their numbers.
Shader "Justachae/NVG/Defocus" {
 Properties { _MainTex ("frame", 2D) = "black" {} }
 CGINCLUDE
  #include "UnityCG.cginc"
  sampler2D _MainTex;
  float4 _MainTex_TexelSize;
  UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
  Texture2D _ScopeLens, _Src, _Coc;
  Texture2D _NearAcc0, _NearAcc1, _NearAcc2, _NearAcc3, _Far0, _Far1, _Far2, _Far3, _Shape, _ShapeWide, _Cover, _CoverWide, _FarSoft;
  float2 _Step;   // a Gaussian's direction, in texels
  SamplerState sampler_linear_clamp, sampler_point_clamp;
  float _ScopeOn;
  float _DefocusShow;   // TEMP DIAG: 0 the frame as made; 1 what stands behind; 2 the discs' colour; 3 how solid
  float4 _DefocusLens;
  float2 _TanHalf;
  float4 _Grid;   // a level's width and height; z −1 where a render texture's up is flipped; w its pixel in quarter-size ones
  float2 _Band;   // the radii (quarter-size pixels) a level draws: above x, up to y
  // The look chosen on the tuner page (_build/tuner.html), all at a quarter of the frame's size:
  Texture2D _LThickW, _MaskW;
  Texture2D _QColor, _QRad, _Thick, _Closed, _LThickB, _LThinB, _LThinF, _MaskB, _MaskF, _Seam;
  float _Radius, _Mode, _Sigma, _Which, _Thresh, _Scale, _ThinNear;
  float3 _BandHat;   // a band of radii: rising from x to its middle y, falling to z (x < 0: whole below; z huge: whole above)
  float _EncIn;      // 1: this band also takes what the near parts close about

  bool InLens(float2 uv) {
   return _ScopeOn > 0.5 && _ScopeLens.SampleLevel(sampler_linear_clamp, uv, 0).r > 0.5;
  }
  // The frame's own texel (set by NightVisionDefocus.cs): Unity's _CameraDepthTexture_TexelSize is a global that the
  // other cameras that make a depth texture (the eyes', the sight's) set to their own size, and from frame to frame the
  // reach of the nearest-depth taps below changed with it, and every near thing's outline flickered.
  float4 _DepthTexel;
  float4 _DepthJitter;   // where in the depth the picture's point is, from it (UV): the camera's jitter this frame
  // A disc's radius (quarter-size pixels) for what the frame shows at uv: none through a sight's lens. Taken from the
  // nearest depth within two pixels each way: along a near thing's outline the frame's colour is the thing's and what
  // lies beyond mixed (smoothed, scaled), while its depth is one or the other, and a ring of the thing's colour was
  // left out of what is near and stayed sharp.
  // Through a magnified sight: its lens and the dark ring of its eyepiece about it (the sight's own, what one aims in)
  // stay as they are: no blur within a margin (_ScopeMargin, of the frame's height) of the lens.
  float _ScopeMargin, _Aspect;
  // How much of a magnified sight is about uv, 0 to 1, eased: the lens's mask counted over the margin about uv (its
  // own edge is stepped and stepped differently frame to frame: taken as it was, a jagged ring about the lens's picture
  // was blurred and flickered while still).
  Texture2D _ScopeSoft;   // the lens's mask at a quarter of the size, blurred over the margin (NightVisionDefocus.cs)
  float ScopeNear(float2 uv) {
   if (_ScopeOn < 0.5) return 0;
   return smoothstep(0.03, 0.3, _ScopeSoft.SampleLevel(sampler_linear_clamp, uv, 0).r);
  }
  float Radius(float2 uv) {
   float scope = ScopeNear(uv);
   if (scope > 0.999)
    return 0;
   float2 o = 2 * _DepthTexel.xy;
   float2 picture = uv;
   uv += _DepthJitter.xy;
   float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
   float z = LinearEyeDepth(raw);
   z = min(z, LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv + float2(o.x, o.y))));
   z = min(z, LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv + float2(-o.x, o.y))));
   z = min(z, LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv + float2(o.x, -o.y))));
   z = min(z, LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv + float2(-o.x, -o.y))));
   float2 ray = (picture * 2 - 1) * _TanHalf;
   float d = max(z * sqrt(1 + dot(ray, ray)) - _DefocusLens.z, 0.001);
   float lens = _DefocusLens.y * abs(1 / d - _DefocusLens.x);
   return _DefocusLens.w * (1 - exp(-lens / _DefocusLens.w)) * (1 - scope);
  }
  // The radius of the depth's own texel (its index in the depth texture, as it is), at the picture's point it shows
  // (its centre less the jitter): what is nearest in it and its four neighbours one texel out each way diagonally.
  float4 _DepthSize;   // the depth texture's width, height, and one over each
  float RadiusTexel(float2 texel, out float2 picture) {
   float2 du = (texel + 0.5) * _DepthSize.zw;
   picture = du - _DepthJitter.xy;
   float scope = ScopeNear(picture);
   if (scope > 0.999)
    return 0;
   float2 o = _DepthSize.zw;
   float z = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, du));
   z = min(z, LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, du + float2(o.x, o.y))));
   z = min(z, LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, du + float2(-o.x, o.y))));
   z = min(z, LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, du + float2(o.x, -o.y))));
   z = min(z, LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, du + float2(-o.x, -o.y))));
   float2 ray = (picture * 2 - 1) * _TanHalf;
   float d = max(z * sqrt(1 + dot(ray, ray)) - _DefocusLens.z, 0.001);
   float lens = _DefocusLens.y * abs(1 / d - _DefocusLens.x);
   return _DefocusLens.w * (1 - exp(-lens / _DefocusLens.w)) * (1 - scope);
  }
  // How much of a point is near (spread by its disc), the rest left sharp: none below half a quarter-size pixel, all
  // from two and a half, eased between, so no line is seen where the spreading begins along a surface going away.
  float Nearness(float r) { return smoothstep(0.5, 2.5, r); }
  bool IsNear(float r) { return Nearness(r) > 0; }
  // How much a near point counts in the colour of what is near about it: the nearer the more (the square of its
  // disc's radius against the most), so a nearer thing stands in front of what is behind it when both are blurred
  // together: a bright floor a metre off showed through a dark gun a third of that away.
  float Front(float r) { float k = r / _DefocusLens.w; return k * k + 0.02; }

  float4 Near(float2 uv) {
   float4 n = _NearAcc0.SampleLevel(sampler_linear_clamp, uv, 0) + _NearAcc1.SampleLevel(sampler_linear_clamp, uv, 0)
    + _NearAcc2.SampleLevel(sampler_linear_clamp, uv, 0) + _NearAcc3.SampleLevel(sampler_linear_clamp, uv, 0);
   return n.a > 1 ? n / n.a : n;
  }
  float4 Far(int level, float2 uv) {
   if (level <= 0) return _Far0.SampleLevel(sampler_linear_clamp, uv, 0);
   if (level == 1) return _Far1.SampleLevel(sampler_linear_clamp, uv, 0);
   if (level == 2) return _Far2.SampleLevel(sampler_linear_clamp, uv, 0);
   return _Far3.SampleLevel(sampler_linear_clamp, uv, 0);
  }
 ENDCG
 SubShader {
  Cull Off ZWrite Off ZTest Always
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   float4 frag (v2f_img i) : SV_Target { return 0; }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.5
   float4 frag (v2f_img i) : SV_Target {
    float2 t = _MainTex_TexelSize.xy;
    float4 sum = 0;
    for (int y = 0; y < 4; y++)
     for (int x = 0; x < 4; x++) {
      float2 uv = i.uv + (float2(x, y) - 1.5) * t;
      float r = Radius(uv);
      sum += float4(tex2D(_MainTex, uv).rgb, 1) * Nearness(r) * Front(r);
     }
    return sum / 16;
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.5
   float4 frag (v2f_img i) : SV_Target {
    float2 t = _MainTex_TexelSize.xy;
    float2 sum = 0;
    for (int y = 0; y < 4; y++)
     for (int x = 0; x < 4; x++) {
      float r = Radius(i.uv + (float2(x, y) - 1.5) * t);
      sum += float2(r, 1) * Nearness(r);
     }
    return float4(sum / 16, 0, 0);
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.5
   float4 frag (v2f_img i) : SV_Target {
    float2 t = _MainTex_TexelSize.xy;
    float4 sum = 0;
    for (int y = 0; y < 4; y++)
     for (int x = 0; x < 4; x++) {
      float2 uv = i.uv + (float2(x, y) - 1.5) * t;
      sum += float4(tex2D(_MainTex, uv).rgb, 1) * (1 - Nearness(Radius(uv)));
     }
    return sum / 16;
   }
   ENDCG
  }
  Pass {
   Blend One One
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #pragma target 3.5
   struct v2f {
    float4 pos : SV_POSITION;
    float2 off : TEXCOORD0;
    nointerpolation float4 col : TEXCOORD1;
    nointerpolation float r : TEXCOORD2;
   };
   static const float2 Corners[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1) };
   v2f vert (uint id : SV_VertexID) {
    v2f o;
    uint quad = id / 6, corner = id % 6;
    uint w = (uint)_Grid.x;
    float2 cell = float2(quad % w, quad / w) + 0.5;
    float2 uv = cell / _Grid.xy;
    float4 c = _Src.SampleLevel(sampler_point_clamp, uv, 0);
    float2 coc = _Coc.SampleLevel(sampler_point_clamp, uv, 0).rg;
    float quarter = coc.y > 1e-5 ? coc.x / coc.y : 0;
    bool drawn = c.a > 1e-5 && quarter > _Band.x && quarter <= _Band.y;
    float r = max(quarter / _Grid.w, 0.5);   // in this level's pixels
    float2 off = Corners[corner] * (r + 1);
    float2 p = (cell + off) / _Grid.xy * 2 - 1;
    p.y *= _Grid.z;
    o.pos = drawn ? float4(p, 0.5, 1) : float4(2, 2, 2, 1);
    o.off = off;
    o.col = c;
    o.r = r;
    return o;
   }
   // The area under a disc of radius r whose edge falls off over a pixel (as drawn below), so the light is kept.
   float Area(float r) {
    float a = max(r - 0.5, 0), b = r + 0.5;
    return UNITY_PI * a * a + 2 * UNITY_PI * (b * b * b / 6 - b * a * a / 2 + a * a * a / 3);
   }
   float4 frag (v2f i) : SV_Target {
    float cover = saturate(i.r + 0.5 - length(i.off));
    return i.col * (cover / Area(i.r));
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.5
   float4 frag (v2f_img i) : SV_Target {
    float4 f = tex2D(_MainTex, i.uv);
    if (_DefocusShow > 3.5) return float4(Radius(i.uv) / _DefocusLens.w, InLens(i.uv) ? 1 : 0, 0, 1);   // TEMP DIAG
    // Through a magnified sight's lens and about it (its eyepiece's ring): its own picture, as it is, eased out.
    float scopeKeep = ScopeNear(i.uv);
    if (scopeKeep > 0.999)
     return f;
    // The look chosen on the tuner page, laid over the frame:
    //  colour: the thick parts (blurred a little), the thin parts (blurred wide) and a wider wash of the thin parts,
    //  each weighted by how near (Front), averaged together;
    //  how solid: the thick parts' cover (and what they close about) blurred a little, and the thin parts' wash, the
    //  more of it shown the less there is (a / (a + 0.3 (1 - a))); the thin parts themselves go into what lies behind.
    float4 ck = _LThickB.SampleLevel(sampler_linear_clamp, i.uv, 0);
    float4 cn = _LThinB.SampleLevel(sampler_linear_clamp, i.uv, 0);
    float4 cf = _LThinF.SampleLevel(sampler_linear_clamp, i.uv, 0);
    float wsum = ck.a + cn.a + cf.a;
    float3 colour = wsum > 1e-6 ? (ck.rgb + cn.rgb + cf.rgb) / wsum : f.rgb;
    // How solid the near parts show: as with a table behind them. There the table is near too and blurred with them,
    // and they show by their share of the weight against it (the gun's Front ten times the table's), so their colour
    // reaches out further and fuller than their cover alone; a far scene counts here as that table (0.08 a point,
    // Front at a quarter of the most), by how much of it is about. By cover alone, against a far scene the gun showed
    // thinner and spread less than with the table behind it.
    float cover = saturate(_MaskB.SampleLevel(sampler_linear_clamp, i.uv, 0).r);
    // The share taken from the layers blurred wider still (DefocusShareSigma): over a far scene the share lifts a
    // little of the gun about to most of it, and taken from the narrow blur it lifted a pixel of the gun's stepped
    // outline flipping with the breath into a whole patch pulsing; over the wider blur a pixel is as little as it is.
    float wa = _LThickW.SampleLevel(sampler_linear_clamp, i.uv, 0).a;
    float wcover = saturate(_MaskW.SampleLevel(sampler_linear_clamp, i.uv, 0).r);
    float ak = max(cover, wa / max(wa + 0.08 * (1 - wcover), 1e-6));
    float a2 = saturate(_MaskF.SampleLevel(sampler_linear_clamp, i.uv, 0).g);
    float af = a2 / max(a2 + 0.3 * (1 - a2), 1e-6);
    float shown = 1 - (1 - ak) * (1 - af);
    // What lies behind: the frame as it is; where the frame has a near thing itself, and a little about it, what is
    // about it filled in (finest first).
    // How near, from the quarter-size layer (over a tent of the frame's pixels), not the frame's own depth: the depth's
    // edges stepped a pixel this way and that from frame to frame while the colour stood still, and each near thing's
    // outline (and a far target's, a little near) flickered between the frame and what is filled in behind it.
    float near = _QRad.SampleLevel(sampler_linear_clamp, i.uv, 0).g;
    float k = max(near, saturate(_Seam.SampleLevel(sampler_linear_clamp, i.uv, 0).r * 1.5));
    float3 fill = 0;
    float found = 0, need = 1;
    for (int level = 0; level < 4 && need > 1e-3; level++) {
     float4 far = Far(level, i.uv);
     if (far.a < 1e-3)
      continue;
     float take = need * saturate(far.a * 2);
     fill += far.rgb / far.a * take;
     found += take;
     need -= take;
    }
    fill = found > 1e-3 ? lerp(colour, fill / found, saturate(found * 2)) : colour;
    float3 behind = lerp(f.rgb, fill, k);
    if (_DefocusShow > 2.5) return float4(shown.xxx, 1);   // TEMP DIAG
    if (_DefocusShow > 1.5) return float4(colour, 1);
    if (_DefocusShow > 0.5) return float4(behind, 1);
    return float4(lerp(lerp(behind, colour, shown), f.rgb, scopeKeep), f.a);
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   float4 frag (v2f_img i) : SV_Target { return saturate(_Cover.SampleLevel(sampler_linear_clamp, i.uv, 0).r); }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   float4 frag (v2f_img i) : SV_Target {
    float2 t = _MainTex_TexelSize.xy;
    return 0.25 * (tex2D(_MainTex, i.uv + float2(-t.x, -t.y)) + tex2D(_MainTex, i.uv + float2(t.x, -t.y))
     + tex2D(_MainTex, i.uv + float2(-t.x, t.y)) + tex2D(_MainTex, i.uv + float2(t.x, t.y)));
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   float4 frag (v2f_img i) : SV_Target {
    float2 d = _Step * _MainTex_TexelSize.xy;
    float4 sum = 0;
    float total = 0;
    for (int k = -6; k <= 6; k++) {
     float w = exp(-k * k / 32.0);
     sum += tex2D(_MainTex, i.uv + d * k) * w;
     total += w;
    }
    return sum / total;
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.5
   float4 frag (v2f_img i) : SV_Target {
    float2 t = _MainTex_TexelSize.xy;
    // A tent over eight by eight of the frame's pixels (twice the quarter-size pixel), not a box of four: as the gun
    // swayed by less than a quarter-size pixel a box's mean stepped, and the blurred gun moved in steps (jitter).
    float4 sum = 0;
    float total = 0;
    for (int y = 0; y < 8; y++)
     for (int x = 0; x < 8; x++) {
      float2 o = float2(x, y) - 3.5;
      float w = (4 - abs(o.x)) * (4 - abs(o.y));
      float2 uv = i.uv + o * t;
      sum += float4(tex2D(_MainTex, uv).rgb, Nearness(Radius(uv))) * w;
      total += w;
     }
    return sum / total;
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.5
   float4 frag (v2f_img i) : SV_Target {
    float2 t = _MainTex_TexelSize.xy;
    // The radius of what is near under the pixel (its mean, weighted by how near), how much of the pixel is near (the
    // mean): the mean of all of it gave a pixel half gun and half far scene a middle radius, the nearest one jumped as a
    // single pixel of the gun's outline flipped.
    // Over the depth's own texels (not the frame's pixels, each taking the texel nearest it): each texel at the
    // picture's point it shows this frame (its centre less the jitter), weighted by a tent of four of the frame's
    // pixels about this pixel's middle at that point. Taken by the frame's pixels, which texel each took jumped as the
    // jitter moved by part of a texel, and what was near flickered at every outline.
    float2 reach = 4 * t;
    float2 first = floor((i.uv + _DepthJitter.xy - reach) * _DepthSize.xy - 0.5);
    float r = 0, near = 0, total = 0;
    for (int y = 0; y < 7; y++)
     for (int x = 0; x < 7; x++) {
      float2 picture;
      float ri = RadiusTexel(first + float2(x, y), picture);
      float2 o = abs(picture - i.uv) / reach;
      float w = saturate(1 - o.x) * saturate(1 - o.y);
      float ni = Nearness(ri) * w;
      r += ri * ni;
      near += ni;
      total += w;
     }
    r = near > 1e-4 ? r / near : 0;   // the mean radius of what is near under the pixel: it moves on as smoothly as that does
    near /= max(total, 1e-6);
    return float4(r, near, near * Front(r), 0);
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.5
   float4 frag (v2f_img i) : SV_Target {
    float2 d = _Step * _MainTex_TexelSize.xy;
    float v = tex2D(_MainTex, i.uv).r;
    for (int k = 1; k <= 48; k++) {
     if (k > _Radius) break;
     float a = tex2D(_MainTex, i.uv + d * k).r, b = tex2D(_MainTex, i.uv - d * k).r;
     v = _Mode > 0.5 ? max(v, max(a, b)) : min(v, min(a, b));
    }
    return v;
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   float4 frag (v2f_img i) : SV_Target { float g = tex2D(_MainTex, i.uv).g; return _Thresh < 0 ? g : (g > _Thresh ? 1 : 0); }
   ENDCG
  }
  Pass {
   Blend One One
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   float4 frag (v2f_img i) : SV_Target { return tex2D(_MainTex, i.uv).r * _Scale; }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.5
   float4 frag (v2f_img i) : SV_Target {
    float4 c = _QColor.SampleLevel(sampler_point_clamp, i.uv, 0);
    float4 q = _QRad.SampleLevel(sampler_point_clamp, i.uv, 0);
    float near = q.g;
    float thick = _Thick.SampleLevel(sampler_point_clamp, i.uv, 0).r;
    float gate = smoothstep(0, 1, saturate((q.r - _ThinNear) / 2));
    float tn = near * (1 - thick) * gate, th = near - tn;
    // this band's share of the point, by its radius (the bands' hats sum to one)
    float rr = q.r;
    float wb = rr <= _BandHat.y ? (_BandHat.x < 0 ? 1 : saturate((rr - _BandHat.x) / (_BandHat.y - _BandHat.x)))
     : (_BandHat.z > 1e5 ? 1 : saturate((_BandHat.z - rr) / (_BandHat.z - _BandHat.y)));
    th *= wb;
    float w = near * (q.r / _DefocusLens.w * q.r / _DefocusLens.w + 0.02);
    float enc = max(0, _Closed.SampleLevel(sampler_point_clamp, i.uv, 0).r - near);
    // About the aiming point (the frame's middle) the gaps are left open: a sight's window, what one aims through,
    // shows the scene as it is.
    float aim = length((i.uv - 0.5) * float2(_Aspect, 1));
    enc *= smoothstep(0.06, 0.09, aim);
    if (_Which < 0.5) return float4(c.rgb * th * w, th * w);
    if (_Which < 1.5) return float4(c.rgb * tn * w, tn * w);
    return float4(th + (_EncIn > 0.5 ? enc : 0), tn, 0, 0);
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.5
   float4 frag (v2f_img i) : SV_Target {
    float2 d = _Step * _MainTex_TexelSize.xy;
    float4 sum = tex2D(_MainTex, i.uv);
    float total = 1;
    float reach = min(3 * _Sigma, 48);
    for (int k = 1; k <= 48; k++) {
     if (k > reach) break;
     float w = exp(-k * k / (2 * _Sigma * _Sigma));
     sum += (tex2D(_MainTex, i.uv + d * k) + tex2D(_MainTex, i.uv - d * k)) * w;
     total += 2 * w;
    }
    return sum / total;
   }
   ENDCG
  }
  Pass {
   Blend One One
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   float4 frag (v2f_img i) : SV_Target { return tex2D(_MainTex, i.uv); }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   Texture2D _Prev;
   float4 frag (v2f_img i) : SV_Target {
    return 0.5 * (tex2D(_MainTex, i.uv) + _Prev.SampleLevel(sampler_linear_clamp, i.uv, 0));
   }
   ENDCG
  }
 }
}
