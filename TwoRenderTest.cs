using UnityEngine;

namespace EFTNewCamera;

// TEMP TEST (the user's, 2026-11-15), as the 1.9.36-1.9.39 experiment did it: with the goggle down, the whole frame
// rendered twice, once from each eye (CameraPosition.AimEye either side of the head's middle), half each. The main
// camera renders first, before anything else renders (Application.onBeforeRender), from the left eye, its finished
// frame kept at the eyepieces' image effect; then as ever, from the right eye, its frame mixed half and half with the
// kept one there (NightVisionComposite.cs, before the interface). Each render is one eye, the camera itself: the device
// drawn into it and its lenses mapped from there, no two eyes' views split in one frame (NightVisionEyes.cs). The mean
// frame time is logged every PhaseSeconds.
internal static class TwoRenderTest
{
    internal static bool On = true;
    private const float PhaseSeconds = 20f;
    private const int SettleFrames = 60;
    private const int HalvesPass = 1;   // NvgTube.shader: two pictures, mixed

    private static bool _pass, _hooked, _failed;
    private static int _leftDone = -100;
    private static RenderTexture? _left;
    private static Material? _halves;
    private static readonly int OtherId = Shader.PropertyToID("_Other"), OtherShareId = Shader.PropertyToID("_OtherShare");

    private static float _phaseStart = -1f;
    private static int _phaseFrame, _frames;
    private static double _seconds;

    // Only with the goggle down (and on): otherwise the game's one render, as ever.
    internal static bool Now => On && !_failed && NightVisionEyepieces.Active;

    // The camera across from the head's middle while the test is on: at the right eye (the left eye's render is placed
    // from there, OtherEyeOffset).
    internal static float? EyeOverride => Now ? CameraPosition.AimEye : (float?)null;

    // Where the camera is placed from its place this frame: over at the left eye during the left eye's render.
    internal static Vector3 OtherEyeOffset(Quaternion turn) => _pass ? turn * new Vector3(-2f * CameraPosition.AimEye, 0f, 0f) : Vector3.zero;

    internal static void Hook()
    {
        if (_hooked)
            return;
        _hooked = true;
        Application.onBeforeRender += RenderLeftEye;
    }

    // The left eye's render, before the frame's own.
    private static void RenderLeftEye()
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
            NewCameraPlugin.Log?.LogError("Two render test: the left eye's render failed, not tried again: " + e);
        }
        finally
        {
            _pass = false;
        }
    }

    // In the eyepieces' image effect, the frame done: the left eye's kept; the right eye's mixed half and half with it.
    internal static void TakeEye(RenderTexture into, Shader? tubeShader)
    {
        if (_pass)
        {
            if (_left == null || _left.width != into.width || _left.height != into.height || _left.format != into.format)
            {
                if (_left != null)
                {
                    _left.Release();
                    Object.Destroy(_left);
                }
                _left = new RenderTexture(into.width, into.height, 0, into.format) { name = "Justachae left eye frame", hideFlags = HideFlags.DontSave };
                _left.Create();
            }
            Graphics.Blit(into, _left);
            _leftDone = Time.frameCount;
            return;
        }
        if (!Now || _leftDone != Time.frameCount || _left == null || tubeShader == null)
            return;
        _halves ??= new Material(tubeShader) { name = "Justachae two eyes", hideFlags = HideFlags.DontSave };
        RenderTexture seen = RenderTexture.GetTemporary(into.width, into.height, 0, into.format);
        Graphics.Blit(into, seen);
        _halves.SetTexture(OtherId, _left);
        _halves.SetFloat(OtherShareId, 0.5f);
        Graphics.Blit(seen, into, _halves, HalvesPass);
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
            NewCameraPlugin.Log?.LogInfo($"Two render test (goggle down, two renders): mean frame {ms:F2} ms ({1000.0 / ms:F1} FPS) over {_frames} frames");
        }
        _phaseStart = now;
        _phaseFrame = Time.frameCount;
        _frames = 0;
        _seconds = 0;
    }
}
