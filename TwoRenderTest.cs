using UnityEngine;

namespace EFTNewCamera;

// TEMP TEST (the user's, 2026-11-15): what rendering the whole frame twice costs. While in a raid, the FPS camera is
// rendered a second time each frame, whole (all its own effects and command buffers), from the other eye (EyeApart to
// the left of where it stands), into a texture laid over the screen at half once the frame is done (the interface
// included). Switched on and off by itself every PhaseSeconds; the mean frame time of each stretch is logged, the
// first frames after each switch left out.
internal static class TwoRenderTest
{
    internal static bool On = true;
    private const float EyeApart = 0.064f;       // m, the second render this far to the left
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
            camera.transform.position += camera.transform.rotation * new Vector3(-EyeApart, 0f, 0f);
    }

    // From NewCameraPlugin.EndOfFrame: the second render laid over the screen at half.
    internal static void Overlay()
    {
        if (!_renderedThisFrame || _eye == null)
            return;
        RenderTexture? active = RenderTexture.active;
        RenderTexture.active = null;
        GL.PushMatrix();
        GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);
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
            _phaseOn = false;
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
        _phaseOn = !_phaseOn;
        _phaseStart = now;
        _phaseFrame = Time.frameCount;
        _frames = 0;
        _seconds = 0;
    }
}
