using EFT;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// The game's night-vision ambient, seen only through the lenses. While the game's filter is on, it lights the scene by
// the level's night-vision ambient in place of its own (LevelSettings: the NightVision sky, equator and ground colours
// and intensity, put in RenderSettings as each camera culls): every surface gets more light, the dark ones too, which no
// gain can give (a gain only multiplies the light already there). With the eyepieces in, the frame is the naked eye's
// and keeps the level's own ambient (NightVisionPreCullPostfix); the difference the night-vision one makes is drawn into
// the IR layer, which only the lenses' picture is made with (ForTube), from the frame's G-buffer: each surface's diffuse
// colour, its occlusion, and the two ambients' difference along its normal, as Unity's deferred shading lights a surface
// by the ambient (ShadeSH9). The game's own ambient pass (AmbientLight: the sky's, from the time of day, shaded indoors)
// is not the night-vision one and is left as it is.
// The two ambients are the game's own: RenderSettings' ambient probe as Unity works it out from each set of colours,
// read once a level (and again if the level's settings change), not each frame.
// For the game's picture only: BRNVG's takes the night-vision ambient out on purpose, and keeps its own look.
// A sight's camera renders before the FPS camera's filter switches the ambient (as in the game, whose sight picture
// through the goggle has the level's own ambient too).
internal static partial class NightVisionEyepieces
{
    private static readonly int[] NightAmbientIds =
    {
        Shader.PropertyToID("_NvShAr"), Shader.PropertyToID("_NvShAg"), Shader.PropertyToID("_NvShAb"),
        Shader.PropertyToID("_NvShBr"), Shader.PropertyToID("_NvShBg"), Shader.PropertyToID("_NvShBb"),
        Shader.PropertyToID("_NvShC"),
    };
    private const int NightAmbientPass = 4;               // NvgIrLight.shader

    private static MaterialPropertyBlock? _nightAmbientBlock;
    private static LevelSettings? _nightAmbientLevel;
    private static Color _nightAmbientKeySky, _nightAmbientKeyEquator, _nightAmbientKeyGround, _nightAmbientKeyNightSky,
        _nightAmbientKeyNightEquator, _nightAmbientKeyNightGround;
    private static float _nightAmbientKeyIntensity = -1f, _nightAmbientKeyNightIntensity = -1f;
    private static AmbientMode _nightAmbientKeyMode;
    private static bool _nightAmbientAny;
    private static SphericalHarmonicsL2 _nightAmbientDelta, _nightAmbientDay, _nightAmbientNight;

    // Whether the night-vision ambient's difference is drawn into the IR layer this frame (worked out anew when the
    // level or its settings change).
    private static bool NightAmbientWanted()
    {
        if (UseBrnvg || _irPlain == null)
            return false;
        LevelSettings? level = Comfort.Common.Singleton<LevelSettings>.Instantiated ? Comfort.Common.Singleton<LevelSettings>.Instance : null;
        if (level == null)
            return false;
        if (level != _nightAmbientLevel || level.SkyColor != _nightAmbientKeySky || level.EquatorColor != _nightAmbientKeyEquator
            || level.GroundColor != _nightAmbientKeyGround || level.AmbientIntensity != _nightAmbientKeyIntensity || level.AmbientMode != _nightAmbientKeyMode
            || level.NightVisionSkyColor != _nightAmbientKeyNightSky || level.NightVisionEquatorColor != _nightAmbientKeyNightEquator
            || level.NightVisionGroundColor != _nightAmbientKeyNightGround || level.NightVisionAmbientIntensity != _nightAmbientKeyNightIntensity)
            MeasureNightAmbient(level);
        return _nightAmbientAny;
    }

    // The two ambients' probes, as Unity works them out from the level's colours, and their difference.
    private static void MeasureNightAmbient(LevelSettings level)
    {
        _nightAmbientLevel = level;
        _nightAmbientKeySky = level.SkyColor;
        _nightAmbientKeyEquator = level.EquatorColor;
        _nightAmbientKeyGround = level.GroundColor;
        _nightAmbientKeyIntensity = level.AmbientIntensity;
        _nightAmbientKeyMode = level.AmbientMode;
        _nightAmbientKeyNightSky = level.NightVisionSkyColor;
        _nightAmbientKeyNightEquator = level.NightVisionEquatorColor;
        _nightAmbientKeyNightGround = level.NightVisionGroundColor;
        _nightAmbientKeyNightIntensity = level.NightVisionAmbientIntensity;

        AmbientMode mode = RenderSettings.ambientMode;
        Color sky = RenderSettings.ambientSkyColor, equator = RenderSettings.ambientEquatorColor, ground = RenderSettings.ambientGroundColor,
            flat = RenderSettings.ambientLight;
        float intensity = RenderSettings.ambientIntensity;
        try
        {
            SetAmbient(level.AmbientMode, level.SkyColor, level.EquatorColor, level.GroundColor, level.AmbientIntensity);
            _nightAmbientDay = RenderSettings.ambientProbe;
            // As LevelSettings puts the night-vision ambient in (the mode as it is).
            SetAmbient(level.AmbientMode, level.NightVisionSkyColor, level.NightVisionEquatorColor, level.NightVisionGroundColor, level.NightVisionAmbientIntensity);
            _nightAmbientNight = RenderSettings.ambientProbe;
        }
        finally
        {
            RenderSettings.ambientMode = mode;
            RenderSettings.ambientSkyColor = sky;
            RenderSettings.ambientEquatorColor = equator;
            RenderSettings.ambientGroundColor = ground;
            RenderSettings.ambientLight = flat;
            RenderSettings.ambientIntensity = intensity;
        }
        _nightAmbientDelta = _nightAmbientNight + _nightAmbientDay * -1f;
        _nightAmbientAny = _nightAmbientDelta != new SphericalHarmonicsL2();

        // Packed as Unity packs a probe for ShadeSH9 (as AmbientLight.SetSH does).
        _nightAmbientBlock ??= new MaterialPropertyBlock();
        SphericalHarmonicsL2 sh = _nightAmbientDelta;
        for (int i = 0; i < 3; i++)
        {
            _nightAmbientBlock.SetVector(NightAmbientIds[i], new Vector4(sh[i, 3], sh[i, 1], sh[i, 2], sh[i, 0] - sh[i, 6]));
            _nightAmbientBlock.SetVector(NightAmbientIds[3 + i], new Vector4(sh[i, 4], sh[i, 5], sh[i, 6] * 3f, sh[i, 7]));
        }
        _nightAmbientBlock.SetVector(NightAmbientIds[6], new Vector4(sh[0, 8], sh[1, 8], sh[2, 8], 1f));
        NewCameraPlugin.Log?.LogInfo($"NVG eyepieces: night-vision ambient {(_nightAmbientAny ? "differs" : "is the same")} on this level"
            + $" (light along up {AmbientUp(_nightAmbientDay)} by day, {AmbientUp(_nightAmbientNight)} with night vision).");
    }

    private static void SetAmbient(AmbientMode mode, Color sky, Color equator, Color ground, float intensity)
    {
        RenderSettings.ambientMode = mode;
        RenderSettings.ambientEquatorColor = equator;
        RenderSettings.ambientGroundColor = ground;
        RenderSettings.ambientSkyColor = sky;
        RenderSettings.ambientLight = sky;
        RenderSettings.ambientIntensity = intensity;
    }

    // A probe's light toward the sky (as the log reads it).
    private static string AmbientUp(SphericalHarmonicsL2 sh)
    {
        var colors = new Color[1];
        sh.Evaluate(new[] { Vector3.up }, colors);
        return $"({colors[0].r:F3}, {colors[0].g:F3}, {colors[0].b:F3})";
    }

    // Into the IR layer with the lights (the buffer after the lighting, on the layer).
    private static void DrawNightAmbient(CommandBuffer buffer)
    {
        buffer.DrawProcedural(Matrix4x4.identity, _irPlain!, NightAmbientPass, MeshTopology.Triangles, 3, 1, _nightAmbientBlock);
    }
}
