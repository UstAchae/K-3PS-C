using UnityEngine;

namespace EFTNewCamera;

// TEMP TEST (the user's, 2026-11-15): with the goggle down or flipping, the frame seen from each eye (CameraPosition.AimEye
// either side of the head's middle), half each. The right eye rendered first, whole, before anything else renders
// (Application.onBeforeRender): its scene (before the eyepieces) and its depth kept at the eyepieces' image effect, and
// its finished frame. Then the camera's own render, from the left eye, only out to FarFill: past that the two eyes see
// alike (2 pixels apart at most), so there the right eye's scene is taken in, before the left eye's eyepieces are made
// (NightVisionComposite.cs). The left eye's finished frame mixed half and half with the right's. Each render is one eye,
// the camera itself: the device drawn into it and its lenses mapped from there, no two eyes' views split in one frame
// (NightVisionEyes.cs). The mean frame time is logged every PhaseSeconds.
internal static class TwoRenderTest
{
    internal static bool On = true;
    internal const float FarFill = 25f;   // m: the left eye's render goes no further
    private const float PhaseSeconds = 20f;
    private const int SettleFrames = 60;
    private const int HalvesPass = 1, DepthPass = 2, FillPass = 3;   // NvgTube.shader

    private static bool _pass, _hooked, _failed, _farSet, _farNoted;
    private static float _farBefore;
    private static int _firstDone = -100, _sceneDone = -100;
    private static RenderTexture? _firstFinal, _firstScene, _firstDepth;
    private static Material? _material;
    private static readonly int OtherId = Shader.PropertyToID("_Other"), OtherShareId = Shader.PropertyToID("_OtherShare");
    private static readonly int OtherDepthId = Shader.PropertyToID("_OtherDepth"), FarFillId = Shader.PropertyToID("_FarFill");

    private static float _phaseStart = -1f;
    private static int _phaseFrame, _frames;
    private static double _seconds;

    // With the goggle down, and while it flips down or up (the old two-eyes split never in between): otherwise the
    // game's one render, as ever.
    internal static bool Now => On && !_failed && NightVisionEyepieces.DownOrFlipping;

    // The camera across from the head's middle while the test is on: at the left eye (the right eye's render is placed
    // from there, OtherEyeOffset).
    internal static float? EyeOverride => Now ? -CameraPosition.AimEye : (float?)null;

    // Where the camera is placed from its place this frame: over at the right eye during the right eye's render.
    internal static Vector3 OtherEyeOffset(Quaternion turn) => _pass ? turn * new Vector3(2f * CameraPosition.AimEye, 0f, 0f) : Vector3.zero;

    internal static void Hook()
    {
        if (_hooked)
            return;
        _hooked = true;
        Application.onBeforeRender += RenderFirstEye;
    }

    // The right eye's render, whole, before the frame's own; then the camera's own (the left eye's) no further than FarFill.
    private static void RenderFirstEye()
    {
        if (!Now)
        {
            _phaseStart = -1f;
            return;
        }
        Measure();
        Camera? main = Camera.main;
        if (main == null || !main.isActiveAndEnabled)
            return;
        _pass = true;
        try
        {
            main.Render();
        }
        catch (System.Exception e)
        {
            _failed = true;
            NewCameraPlugin.Log?.LogError("Two render test: the right eye's render failed, not tried again: " + e);
        }
        finally
        {
            _pass = false;
        }
        if (_firstDone == Time.frameCount && main.farClipPlane > FarFill)
        {
            _farBefore = main.farClipPlane;
            main.farClipPlane = FarFill;
            _farSet = true;
        }
    }

    // From NewCameraPlugin.EndOfFrame: the camera's own reach given back.
    internal static void EndOfFrame()
    {
        if (!_farSet)
            return;
        _farSet = false;
        Camera? main = Camera.main;
        if (main != null)
            main.farClipPlane = _farBefore;
    }

    private static Material? MaterialFor(Shader? tubeShader)
    {
        if (tubeShader == null)
            return null;
        return _material ??= new Material(tubeShader) { name = "Justachae two eyes", hideFlags = HideFlags.DontSave };
    }

    private static RenderTexture Kept(ref RenderTexture? kept, RenderTexture like, RenderTextureFormat format, string name)
    {
        if (kept == null || kept.width != like.width || kept.height != like.height || kept.format != format)
        {
            if (kept != null)
            {
                kept.Release();
                Object.Destroy(kept);
            }
            kept = new RenderTexture(like.width, like.height, 0, format) { name = name, hideFlags = HideFlags.DontSave };
            kept.Create();
        }
        return kept;
    }

    // At the eyepieces' image effect, before the eyepieces: the right eye's scene and depth kept; the left eye's far part
    // filled from them.
    internal static void BeforeEyepieces(RenderTexture scene, Shader? tubeShader)
    {
        Material? material = MaterialFor(tubeShader);
        if (material == null)
            return;
        if (_pass)
        {
            Graphics.Blit(scene, Kept(ref _firstScene, scene, scene.format, "Justachae right eye scene"));
            Graphics.Blit(scene, Kept(ref _firstDepth, scene, RenderTextureFormat.RFloat, "Justachae right eye depth"), material, DepthPass);
            _sceneDone = Time.frameCount;
            return;
        }
        if (!Now || _sceneDone != Time.frameCount || !_farSet)
            return;
        if (!_farNoted)
        {
            _farNoted = true;
            Camera? main = Camera.main;
            NewCameraPlugin.Log?.LogInfo($"Two render test: the left eye rendered out to {(main != null ? main.farClipPlane : 0f):F0} m (the right eye's {_farBefore:F0} m), past that the right eye's scene.");
        }
        RenderTexture filled = RenderTexture.GetTemporary(scene.width, scene.height, 0, scene.format);
        material.SetTexture(OtherId, _firstScene);
        material.SetTexture(OtherDepthId, _firstDepth);
        material.SetFloat(FarFillId, FarFill);
        Graphics.Blit(scene, filled, material, FillPass);
        Graphics.Blit(filled, scene);
        RenderTexture.ReleaseTemporary(filled);
    }

    // In the eyepieces' image effect, the frame done: the right eye's kept; the left eye's mixed half and half with it.
    internal static void TakeEye(RenderTexture into, Shader? tubeShader)
    {
        Material? material = MaterialFor(tubeShader);
        if (material == null)
            return;
        if (_pass)
        {
            Graphics.Blit(into, Kept(ref _firstFinal, into, into.format, "Justachae right eye frame"));
            _firstDone = Time.frameCount;
            return;
        }
        if (!Now || _firstDone != Time.frameCount || _firstFinal == null)
            return;
        RenderTexture seen = RenderTexture.GetTemporary(into.width, into.height, 0, into.format);
        Graphics.Blit(into, seen);
        material.SetTexture(OtherId, _firstFinal);
        material.SetFloat(OtherShareId, 0.5f);
        Graphics.Blit(seen, into, material, HalvesPass);
        RenderTexture.ReleaseTemporary(seen);
    }

    private static void Measure()
    {
        float now = Time.realtimeSinceStartup;
        if (_phaseStart < 0f)
        {
            _phaseStart = now;
            _phaseFrame = Time.frameCount;
            _frames = 0;
            _seconds = 0;
            return;
        }
        if (Time.frameCount - _phaseFrame > SettleFrames)
        {
            _frames++;
            _seconds += Time.unscaledDeltaTime;
        }
        if (now - _phaseStart < PhaseSeconds)
            return;
        if (_frames > 0)
        {
            double ms = 1000.0 * _seconds / _frames;
            NewCameraPlugin.Log?.LogInfo($"Two render test (goggle down, the left eye out to {FarFill:F0} m): mean frame {ms:F2} ms ({1000.0 / ms:F1} FPS) over {_frames} frames");
        }
        _phaseStart = now;
        _phaseFrame = Time.frameCount;
        _frames = 0;
        _seconds = 0;
    }
}
