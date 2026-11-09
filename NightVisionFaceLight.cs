using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EFTNewCamera;

// The eyepieces' light on the wearer's face: each lens throws the picture it shows back toward the eye, and seen from
// outside it lights the eye sockets, the bridge of the nose and the cheeks. Every point of the lens sends its light
// over the eyepiece's field of view, so at the face, an eye relief out, the light is the lens's disc spread by that
// field: full where every part of the field comes from the lens, fading out over a rim as wide as the field is there
// (the share of each point's cone that the lens fills). A spot light on each lens's axis, behind it, throws that
// profile from its cookie, sized to the face's distance; the GPNVG-18's two lenses of an eyecup throw the "8" between
// them. In the colour of the tube's phosphor as the picture is drawn (BRNVG's, else the game's filter's) and as bright
// as the tube is lit (warming up, dying away); none for thermal goggles, their display kept dim at night. For the main
// camera and the eyes, which see it on the eyecups' rims and the housing by the lenses; not for the thermal, IR or
// optic cameras.
internal static partial class NightVisionEyepieces
{
    private const float FaceLightRange = 0.15f;       // m, beyond the lens
    private const float FieldOfView = 40f;            // degrees, an eyepiece's
    private const float FaceLightIntensity = 10f;
    private static readonly Color ThermalScreen = new Color(0.85f, 0.9f, 1f);

    private static Light?[] _faceLights = System.Array.Empty<Light?>();
    private static float[] _faceLightBehind = System.Array.Empty<float>();   // each light's way behind its lens (m, unscaled)
    private static readonly Dictionary<int, Texture2D> FaceCookies = new Dictionary<int, Texture2D>();   // by lens radius (0.01 mm)
    private static FieldInfo? _phosphorTint;
    private static bool _phosphorResolved;

    // One for each lens of its own (an eyecup's single lens listed twice, a monocular's four times, get one).
    private static void MakeFaceLights()
    {
        _faceLights = new Light?[_lensNodes.Length];
        _faceLightBehind = new float[_lensNodes.Length];
        float half = FieldOfView * 0.5f * Mathf.Deg2Rad;
        for (int i = 0; i < _lensNodes.Length; i++)
        {
            bool repeated = false;
            for (int j = 0; j < i; j++)
                repeated |= _device.Tubes[j].Centre == _device.Tubes[i].Centre;
            if (repeated)
                continue;

            // The lens's centre on the floor, in its node's frame (forward along the axis toward the eye); the light
            // as far behind it as the lens is wide for the field, and its cone reaching the light's rim at the face.
            Vector4 lens = _lensFloors[i];
            float radius = lens.w, spread = EyeRelief * Mathf.Tan(half), behind = radius / Mathf.Tan(half);
            var holder = new GameObject("face light") { layer = _lensNodes[i].gameObject.layer };
            holder.transform.SetParent(_lensNodes[i], false);
            holder.transform.localPosition = new Vector3(lens.x, lens.y, lens.z - behind);
            _faceLightBehind[i] = behind;
            Light light = holder.AddComponent<Light>();
            light.type = LightType.Spot;
            light.spotAngle = 2f * Mathf.Atan((radius + spread) / (behind + EyeRelief)) * Mathf.Rad2Deg;
            light.cookie = FaceCookie(radius, spread);
            light.shadows = LightShadows.None;
            light.renderMode = LightRenderMode.ForcePixel;
            light.enabled = false;
            _faceLights[i] = light;
        }
    }

    // As each camera culls: lit for the main camera and the eyes.
    private static void LightFace(Camera camera)
    {
        bool lit = Active && _body != null && !_device.Thermal && camera == Camera.main && !NvgDump.NoFaceLight;
        Color color = lit ? PhosphorColor() : Color.black;
        if (lit)
            _phosphorOf[_device.Node] = color;    // the player's other models show it so (NightVisionPreviewMounts)
        float scale = _body != null ? _body.lossyScale.x : 1f;
        for (int i = 0; i < _faceLights.Length; i++)
        {
            Light? light = _faceLights[i];
            if (light == null)
                continue;
            float power = i < _power.Length ? _power[i] : 0f;
            light.enabled = lit && power > 0.01f;
            if (!light.enabled)
                continue;
            light.color = color;
            light.intensity = FaceLightIntensity * power;
            light.range = (_faceLightBehind[i] + FaceLightRange) * scale;
        }
    }

    // The colour the picture is drawn in, at its brightest component 1.
    private static Color PhosphorColor()
    {
        Color color = ThermalScreen;
        if (!_device.Thermal)
        {
            BSG.CameraEffects.NightVision? nightVision = BodyVisibility.Cameras?.NightVision;
            Behaviour? brnvg = UseBrnvg ? NightRenderer(nightVision) : null;
            if (!_phosphorResolved)
            {
                _phosphorResolved = true;
                System.Type? type = BodyVisibility.NightRendererType;
                _phosphorTint = type != null ? AccessTools.Field(type, "phosphorTint") : null;
            }
            if (brnvg != null && _phosphorTint?.GetValue(brnvg) is Color tint)
                color = tint;
            else if (nightVision != null)
                color = nightVision.Color;
        }
        float brightest = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
        return brightest > 1e-4f ? color / brightest : Color.white;
    }

    // The light across the spot's cone at the face: at r from the axis, the share of a disc of the field's spread
    // there (centred at r) that the lens's disc covers, against the most it can.
    private static Texture2D FaceCookie(float radius, float spread)
    {
        int key = Mathf.RoundToInt(radius * 1e5f);
        if (FaceCookies.TryGetValue(key, out Texture2D cached) && cached != null)
            return cached;

        const int size = 256;
        var cookie = new Texture2D(size, size, TextureFormat.Alpha8, false)
        {
            name = "Justachae NVG face light", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave,
        };
        var pixels = new Color32[size * size];
        float half = size * 0.5f, reach = radius + spread;
        float most = Mathf.PI * Mathf.Min(radius, spread) * Mathf.Min(radius, spread);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = Mathf.Sqrt((x + 0.5f - half) * (x + 0.5f - half) + (y + 0.5f - half) * (y + 0.5f - half)) / half;
                float share = u >= 1f ? 0f : Overlap(radius, spread, u * reach) / most;
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(255 * Mathf.Clamp01(share)));
            }
        }
        cookie.SetPixels32(pixels);
        cookie.Apply(false, true);
        FaceCookies[key] = cookie;
        return cookie;
    }

    // The area two discs share, of radii a and b, their centres d apart.
    private static float Overlap(float a, float b, float d)
    {
        if (d >= a + b)
            return 0f;
        if (d <= Mathf.Abs(a - b))
            return Mathf.PI * Mathf.Min(a, b) * Mathf.Min(a, b);
        float alpha = Mathf.Acos(Mathf.Clamp((d * d + a * a - b * b) / (2f * d * a), -1f, 1f));
        float beta = Mathf.Acos(Mathf.Clamp((d * d + b * b - a * a) / (2f * d * b), -1f, 1f));
        return a * a * (alpha - Mathf.Sin(2f * alpha) * 0.5f) + b * b * (beta - Mathf.Sin(2f * beta) * 0.5f);
    }
}
