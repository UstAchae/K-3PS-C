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

    // TEMP DIAG: a while after the experiment starts, for each of the frame's two renders: where each lens is from the
    // camera (across, up, ahead; mm) and off its axis, the tubes' power, and how much of each lens map has light.
    private static int _diagFrame = -1, _diagStart = -1;
    private static void DiagExperimentEye(Camera camera, Vector3 position)
    {
        if (!ExperimentOn)
        {
            _diagStart = -1;
            return;
        }
        if (_diagStart < 0)
            _diagStart = Time.frameCount;
        if (_diagFrame >= 0 && Time.frameCount > _diagFrame || Time.frameCount < _diagStart + 120)
            return;
        _diagFrame = Time.frameCount;
        string pass = _otherEyePass ? "left render" : "right render";
        Transform view = camera.transform;
        var lenses = new System.Collections.Generic.List<string>();
        for (int i = 0; i < _lensNodes.Length; i += 2)
        {
            Vector3 to = _lensNodes[i].position - position;
            Vector3 axis = -_lensNodes[i].forward;
            float along = Vector3.Dot(to, axis);
            float off = (to - axis * along).magnitude;
            lenses.Add($"lens {i}: across {Vector3.Dot(to, view.right) * 1000f:F0} up {Vector3.Dot(to, view.up) * 1000f:F0} ahead {Vector3.Dot(to, view.forward) * 1000f:F0}, {off * 1000f:F0} mm off its axis");
        }
        GameObject? gun = NearPlayer?.HandsController?.ControllerGameObject;
        Vector3 gunFrom = gun != null ? view.InverseTransformPoint(gun.transform.position) * 1000f : Vector3.zero;
        Transform? deviceRoot = _body;
        Vector3 deviceFrom = deviceRoot != null ? view.InverseTransformPoint(deviceRoot.position) * 1000f : Vector3.zero;
        NewCameraPlugin.Log?.LogInfo($"NVG experiment DIAG {pass}: camera at {view.position.x:F4},{view.position.y:F4},{view.position.z:F4}; gun from it {gunFrom.x:F0},{gunFrom.y:F0},{gunFrom.z:F0} mm (parent chain has camera: {(gun != null && gun.transform.IsChildOf(view))}); device {deviceFrom.x:F0},{deviceFrom.y:F0},{deviceFrom.z:F0} mm; claimed {Interop.EyepieceFusion.ClaimedFrame == Time.frameCount}");
        NewCameraPlugin.Log?.LogInfo($"NVG experiment DIAG {pass}: camera across {CameraPosition.Horizontal * 1000f:F0} mm (+{OtherEyeOffset(view.rotation).magnitude * 1000f:F0}), active {Active}, power {_power[0]:F2}/{_power[2]:F2}, direct {_direct}; {string.Join("; ", lenses)}");
        for (int side = 0; side < 2; side++)
        {
            RenderTexture? map = _lensMaps[side];
            if (map == null)
                continue;
            string what = $"{pass} lens map {side}";
            UnityEngine.Rendering.AsyncGPUReadback.Request(map, 0, TextureFormat.RGBA32, request =>
            {
                if (request.hasError)
                {
                    NewCameraPlugin.Log?.LogInfo($"NVG experiment DIAG {what}: readback failed");
                    return;
                }
                Unity.Collections.NativeArray<Color32> data = request.GetData<Color32>();
                int lit = 0;
                foreach (Color32 c in data)
                {
                    if (c.r > 0 || c.g > 0 || c.b > 0 || c.a > 0)
                        lit++;
                }
                NewCameraPlugin.Log?.LogInfo($"NVG experiment DIAG {what}: {100f * lit / Mathf.Max(data.Length, 1):F2}% lit");
            });
        }
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
