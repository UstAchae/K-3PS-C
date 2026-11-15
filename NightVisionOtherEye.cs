using UnityEngine;

namespace EFTNewCamera;

// EXPERIMENT (the user's, 2026-11-15): a device seen with both eyes, half each over the whole frame, each eye's view
// the whole picture as the game makes it. The eyes stand at the two aiming eyes, AimEye either side of the head's middle
// (F12 「镜头位置」 瞄准眼离中线). The main camera renders twice a frame: first, before anything else renders
// (Application.onBeforeRender), from the left eye, its finished frame kept at the eyepieces' image effect; then as ever,
// from the right eye, its frame mixed half and half with the kept one there. Each render is the whole of it (the
// game's lighting and post-processing, the eyepieces' picture from that eye, the gun where that eye sees it); what
// carries over from frame to frame (temporal anti-aliasing, exposure, the objective's gathered depth) now sees two
// views a frame. To be kept or taken out once seen.
internal static partial class NightVisionEyepieces
{
    internal static bool MonocularBothEyes = true;
    internal static EFT.Player? NearPlayer;
    private static int _nearCount;   // (the direct drawing's near pieces, unused now)

    private static bool _otherEyePass, _otherEyeHooked, _otherEyeFailed, _experimentNoted;
    private static int _otherEyeDone = -100;
    private static RenderTexture? _otherEyePicture;
    private const int TubeHalvesPass = 1;     // NvgTube.shader: two pictures, mixed
    private static readonly int OtherId = Shader.PropertyToID("_Other");
    private static readonly int OtherShareId = Shader.PropertyToID("_OtherShare");

    // The experiment is on for this device: one with eyepieces, down, flipping or switched on.
    private static bool ExperimentOn => MonocularBothEyes && _body != null && _lensNodes.Length > 0 && (Active || _flip > 0.001f);

    // Each frame, before the camera is placed: at the right eye while the experiment is on (not moved over to the aiming
    // eye); the left eye's render a camera placed from there (OtherEyeOffset).
    private static void PlaceExperimentEye()
    {
        CameraPosition.EyeOverride = ExperimentOn ? CameraPosition.AimEye : (float?)null;
        if (!_otherEyeHooked)
        {
            _otherEyeHooked = true;
            Application.onBeforeRender += RenderOtherEye;
        }
        if (ExperimentOn && !_experimentNoted)
        {
            _experimentNoted = true;
            NewCameraPlugin.Log?.LogInfo($"NVG experiment: the whole frame rendered twice, from the left eye then the right, {CameraPosition.AimEye * 1000f:F0} mm either side of the head's middle, half each.");
        }
    }

    // Where the camera is placed from its place this frame (along the world's axes): over at the left eye during the
    // left eye's render.
    internal static Vector3 OtherEyeOffset(Quaternion turn) => _otherEyePass ? turn * new Vector3(-2f * CameraPosition.AimEye, 0f, 0f) : Vector3.zero;

    // The left eye's render, before the frame's own.
    private static void RenderOtherEye()
    {
        if (_otherEyeFailed || !ExperimentOn || CameraPosition.EyeOverride == null)
            return;
        Camera? main = Camera.main;
        if (main == null || !main.isActiveAndEnabled)
            return;
        _otherEyePass = true;
        try
        {
            main.Render();
        }
        catch (System.Exception e)
        {
            _otherEyeFailed = true;
            NewCameraPlugin.Log?.LogError("NVG experiment: the left eye's render failed, not tried again: " + e);
        }
        finally
        {
            _otherEyePass = false;
        }
    }

    // In the eyepieces' image effect, the frame done: the left eye's kept; the right eye's mixed half and half with it.
    private static void TakeOtherEye(RenderTexture into)
    {
        if (_otherEyePass)
        {
            if (_otherEyePicture == null || _otherEyePicture.width != into.width || _otherEyePicture.height != into.height || _otherEyePicture.format != into.format)
            {
                if (_otherEyePicture != null)
                {
                    _otherEyePicture.Release();
                    Object.Destroy(_otherEyePicture);
                }
                _otherEyePicture = new RenderTexture(into.width, into.height, 0, into.format) { name = "Justachae left eye frame", hideFlags = HideFlags.DontSave };
                _otherEyePicture.Create();
            }
            Graphics.Blit(into, _otherEyePicture);
            _otherEyeDone = Time.frameCount;
            return;
        }
        if (!ExperimentOn || _otherEyeDone != Time.frameCount || _otherEyePicture == null || _tubeShader == null)
            return;
        Material halves = _tubeMaterial ??= new Material(_tubeShader) { name = "Justachae NVG tube", hideFlags = HideFlags.DontSave };
        RenderTexture seen = RenderTexture.GetTemporary(into.width, into.height, 0, into.format);
        Graphics.Blit(into, seen);
        halves.SetTexture(OtherId, _otherEyePicture);
        halves.SetFloat(OtherShareId, 0.5f);
        Graphics.Blit(seen, into, halves, TubeHalvesPass);
        halves.SetTexture(OtherId, Texture2D.blackTexture);
        RenderTexture.ReleaseTemporary(seen);
    }

    private static void ReleaseOtherEye()
    {
        CameraPosition.EyeOverride = null;
        _experimentNoted = false;
        if (_otherEyePicture != null)
        {
            _otherEyePicture.Release();
            Object.Destroy(_otherEyePicture);
        }
        _otherEyePicture = null;
    }
}
