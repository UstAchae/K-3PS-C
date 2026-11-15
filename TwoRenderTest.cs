using UnityEngine;

namespace EFTNewCamera;

// TEMP TEST (the user's, 2026-11-15): the whole frame rendered twice, once from each eye (HalfApart either side of
// where the camera is placed), the left eye's into a texture laid over the screen at half once the frame is done (the
// interface included), the right eye's the camera's own. The goggle's two-eyes-in-one-frame split is off meanwhile
// (NightVisionEyes.cs): each render is one eye. The mean frame time is logged every PhaseSeconds.
internal static class TwoRenderTest
{
    internal static bool On = true;
    private const float HalfApart = 0.032f;      // m, each eye this far from where the camera is placed
    private const float PhaseSeconds = 20f;
    private const int SettleFrames = 60;

    private static RenderTexture? _eye;
    private static bool _rendering, _renderedThisFrame, _phaseOn;
    private static float _phaseStart = -1f;
    private static int _phaseFrame, _frames;
    private static double _seconds;

    internal static bool Rendering => _rendering;

    // From NewCameraPlugin.LateUpdate: the second render, before the camera's own.
    internal static void LateUpdate()
    {
        _renderedThisFrame = false;
        Camera? camera = Camera.main;
        if (!On || camera == null || !NewCameraSwitch.Enabled || Comfort.Common.Singleton<EFT.GameWorld>.Instance?.MainPlayer == null)
        {
            _phaseStart = -1f;
            return;
        }
        Measure();
        if (!_phaseOn)
            return;
        int width = Screen.width, height = Screen.height;
        if (_eye == null || _eye.width != width || _eye.height != height)
        {
            if (_eye != null)
                Object.Destroy(_eye);
            _eye = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { name = "Justachae second eye", hideFlags = HideFlags.DontSave };
        }
        RenderTexture? before = camera.targetTexture;
        Vector3 place = camera.transform.position;
        Quaternion turn = camera.transform.rotation;
        _rendering = true;
        try
        {
            camera.targetTexture = _eye;
            camera.Render();
            _renderedThisFrame = true;
        }
        catch (System.Exception e)
        {
            NewCameraPlugin.Log?.LogError($"Two render test: the second render failed, test off: {e}");
            On = false;
        }
        finally
        {
            _rendering = false;
            camera.targetTexture = before;
            camera.transform.SetPositionAndRotation(place, turn);
        }
    }

    // From the FPS camera's placing (NewCameraSwitch.OnCameraPreCull): during the second render, the other eye.
    internal static void Offset(Camera camera)
    {
        if (_rendering)
            camera.transform.position += camera.transform.rotation * new Vector3(-HalfApart, 0f, 0f);
        else if (On)
            camera.transform.position += camera.transform.rotation * new Vector3(HalfApart, 0f, 0f);
    }

    // From NewCameraPlugin.EndOfFrame: the second render laid over the screen at half.
    internal static void Overlay()
    {
        if (!_renderedThisFrame || _eye == null)
            return;
        RenderTexture? active = RenderTexture.active;
        RenderTexture.active = null;
        GL.PushMatrix();
        // Bottom-left origin (the top-left one showed it upside down).
        GL.LoadPixelMatrix(0, Screen.width, 0, Screen.height);
        // Graphics.DrawTexture's colour: 0.5 is neutral, its alpha doubled; 0.25 is half.
        Graphics.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _eye, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0, new Color(0.5f, 0.5f, 0.5f, 0.25f));
        GL.PopMatrix();
        RenderTexture.active = active;
    }

    private static void Measure()
    {
        float now = Time.realtimeSinceStartup;
        if (_phaseStart < 0f)
        {
            _phaseStart = now;
            _phaseOn = true;
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
            NewCameraPlugin.Log?.LogInfo($"Two render test: {(_phaseOn ? "TWO renders" : "one render")}: mean frame {ms:F2} ms ({1000.0 / ms:F1} FPS) over {_frames} frames");
        }
        _phaseOn = true;
        _phaseStart = now;
        _phaseFrame = Time.frameCount;
        _frames = 0;
        _seconds = 0;
    }
}
