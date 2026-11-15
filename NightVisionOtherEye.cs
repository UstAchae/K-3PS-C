using EFT.CameraControl;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace EFTNewCamera;

// EXPERIMENT (the user's, 2026-11-15; every device since 1.9.26): a device seen with both eyes, half each. The two
// eyes stand at the two aiming eyes' places, AimEye either side of the head's middle (F12 「镜头位置」 瞄准眼离中线). The main camera is the eye behind the eyepiece, put there
// (CameraPosition.EyeOverride), the eyepiece's picture as ever, but nothing of the two-eye drawing: the device drawn into
// the main camera from that eye alone, the eyes not moved from the camera, no monocular fade. The other eye sees the
// device as the game draws it (shown to its camera while it renders). The other, bare eye's whole view is rendered by a camera
// of its own (the game's scope camera prefab, as the thermal goggles' is, with only the main camera's own effects), and
// laid over the frame at half. To be kept or taken out once seen.
internal static partial class NightVisionEyepieces
{
    internal static bool MonocularBothEyes = true;
    private const int TubeHalvesPass = 1;     // NvgTube.shader: two pictures, mixed

    private static readonly Camera?[] _bareEyes = new Camera?[2];
    private static readonly RenderTexture?[] _bareEyePictures = new RenderTexture?[2];
    private static int _bareEyesFrame = -100;
    private static bool _bareEyesNoted;
    private static float _eyepieceSide = 1f;   // the eyepiece's side of the head: 1 right, -1 left (RenderLenses)
    private static readonly int OtherId = Shader.PropertyToID("_Other");
    private static readonly int OtherShareId = Shader.PropertyToID("_OtherShare");

    // The experiment is on for this device: with it down, flipping or switched on.
    private static bool ExperimentOn => BothEyesBare && (Active || _flip > 0.001f);

    // Whether the experiment takes this device: any with eyepieces.
    private static bool BothEyesBare => MonocularBothEyes && _body != null && _lensNodes.Length > 0;

    // Each frame, before the camera is placed: the camera at the eye behind the eyepiece while the experiment is on.
    private static void PlaceExperimentEye()
    {
        // A monocular's camera at the eye behind it, a binocular's at the right eye; aimed or not (the camera not moved
        // over to the aiming eye). From the moment the device starts down until it is all the way up: the eyes stay where
        // they are as it flips, not brought together toward the eyepieces.
        float side = _device.Monocular ? _eyepieceSide : 1f;
        CameraPosition.EyeOverride = ExperimentOn ? side * CameraPosition.AimEye : (float?)null;
    }

    private static Camera? BareEye(int side, Camera main)
    {
        if (_bareEyes[side] != null)
            return _bareEyes[side];
        if (UnityResourcesProxy.Load("BaseOpticCamera") is not GameObject prefab)
            return null;
        var holder = new GameObject("Justachae bare eye holder");
        holder.SetActive(false);
        GameObject made = Object.Instantiate(prefab, holder.transform);
        made.name = "Justachae bare eye " + (side == 0 ? "left" : "right");
        made.tag = "Untagged";
        foreach (MonoBehaviour behaviour in made.GetComponents<MonoBehaviour>())
        {
            if (behaviour is OpticComponentUpdater || behaviour is OpticRetrice)
                Object.DestroyImmediate(behaviour);
        }
        made.transform.SetParent(null, false);
        Object.Destroy(holder);
        Object.DontDestroyOnLoad(made);
        Camera? camera = made.GetComponent<Camera>();
        if (camera == null)
        {
            Object.Destroy(made);
            return null;
        }
        camera.enabled = false;
        // A bare eye: only what the main camera itself has on (a scope's lens, its blur and vignette, night vision and
        // thermal all off), with the main camera's post-processing volumes, not a scope's, and no temporal smearing.
        var kept = new System.Collections.Generic.List<string>();
        var off = new System.Collections.Generic.List<string>();
        foreach (MonoBehaviour behaviour in made.GetComponents<MonoBehaviour>())
        {
            if (behaviour == null)
                continue;
            string kind = behaviour.GetType().Name;
            bool mainHas = false;
            foreach (MonoBehaviour own in main.GetComponents<MonoBehaviour>())
            {
                if (own != null && own.enabled && own.GetType() == behaviour.GetType())
                    mainHas = true;
            }
            bool keep = mainHas && kind != "NightVision" && kind != "ThermalVision";
            if (behaviour.enabled != keep)
                behaviour.enabled = keep;
            (keep ? kept : off).Add(kind);
        }
        if (made.TryGetComponent(out PostProcessLayer layer))
        {
            layer.computeAutoExposure = false;
            layer.antialiasingMode = PostProcessLayer.Antialiasing.None;
            if (main.TryGetComponent(out PostProcessLayer mainLayer))
                layer.volumeLayer = mainLayer.volumeLayer;
            layer.volumeTrigger = made.transform;
        }
        NewCameraPlugin.Log?.LogInfo($"NVG experiment: bare eye camera kept {string.Join(", ", kept)}; off {string.Join(", ", off)}.");
        _bareEyes[side] = camera;
        return camera;
    }

    // The other camera's view this frame, as the main camera is about to render: at the other aiming eye.
    private static void RenderBareEyes(Camera main)
    {
        if (!ExperimentOn || CameraPosition.EyeOverride == null)
            return;
        SSAA? ssaa = main.GetComponent<SSAA>();
        int width = Mathf.Max(ssaa != null ? ssaa.GetInputWidth() : Screen.width, 1), height = Mathf.Max(ssaa != null ? ssaa.GetInputHeight() : Screen.height, 1);
        Transform view = main.transform;
        const int side = 0;
        Camera? camera = BareEye(side, main);
        if (camera == null)
            return;
        RenderTexture? picture = _bareEyePictures[side];
        if (picture == null || picture.width != width || picture.height != height)
        {
            if (picture != null)
            {
                picture.Release();
                Object.Destroy(picture);
            }
            picture = new RenderTexture(width, height, 24, main.allowHDR ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32)
            {
                name = "Justachae bare eye picture",
                filterMode = FilterMode.Bilinear,
            };
            picture.Create();
            _bareEyePictures[side] = picture;
        }
        camera.CopyFrom(main);
        // The other aiming eye: as far the other side of the head's middle.
        camera.transform.SetPositionAndRotation(view.position - view.right * (2f * CameraPosition.EyeOverride.Value), view.rotation);
        camera.rect = new Rect(0f, 0f, 1f, 1f);
        camera.projectionMatrix = main.nonJitteredProjectionMatrix;
        camera.targetTexture = picture;
        // The device, kept from the main camera (drawn into it from its eye), seen by this one as the game draws it.
        foreach ((Renderer renderer, UnityEngine.Rendering.ShadowCastingMode casting) in _hidden)
        {
            if (renderer != null)
                renderer.shadowCastingMode = casting;
        }
        try
        {
            camera.Render();
        }
        catch (System.Exception e)
        {
            NewCameraPlugin.Log?.LogWarning("NVG experiment: the bare eye's view failed: " + e);
            return;
        }
        finally
        {
            camera.ResetProjectionMatrix();
            camera.targetTexture = null;
            foreach ((Renderer renderer, UnityEngine.Rendering.ShadowCastingMode _) in _hidden)
            {
                if (renderer != null)
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            }
        }
        _bareEyesFrame = Time.frameCount;
        // Noted once the camera has come to the eye (the frame the experiment starts, it is still where it was).
        if (!_bareEyesNoted && Mathf.Abs(CameraPosition.Horizontal - CameraPosition.EyeOverride.Value) < 1e-4f)
        {
            _bareEyesNoted = true;
            NewCameraPlugin.Log?.LogInfo($"NVG experiment: the two cameras at the aiming eyes, {CameraPosition.AimEye * 1000f:F0} mm either side of the head's middle; the main camera at {CameraPosition.EyeOverride.Value * 1000f:F0} mm ({width}x{height}), half each.");
        }
    }

    // Whether the other eye's view was made this frame; if so, each eye's split (the composite all one side's: the
    // main camera's eye, then the other's) and the other eye's frame.
    private static bool ExperimentSplits(out float mainSplit, out float otherSplit, out RenderTexture? otherFrom)
    {
        bool mainRight = (CameraPosition.EyeOverride ?? 1f) >= 0f;
        mainSplit = mainRight ? -1f : 2f;
        otherSplit = mainRight ? 2f : -1f;
        otherFrom = _bareEyePictures[0];
        return _bareEyesFrame == Time.frameCount && otherFrom != null && _tubeShader != null;
    }

    // The main camera's eye's finished frame (into) and the other eye's (other), half each.
    private static void LayBareEyes(RenderTexture into, RenderTexture other)
    {
        Material halves = _tubeMaterial ??= new Material(_tubeShader) { name = "Justachae NVG tube", hideFlags = HideFlags.DontSave };
        RenderTexture seen = RenderTexture.GetTemporary(into.width, into.height, 0, into.format);
        Graphics.Blit(into, seen);
        halves.SetTexture(OtherId, other);
        halves.SetFloat(OtherShareId, 0.5f);
        Graphics.Blit(seen, into, halves, TubeHalvesPass);
        halves.SetTexture(OtherId, Texture2D.blackTexture);
        RenderTexture.ReleaseTemporary(seen);
    }

    private static void ReleaseOtherEye()
    {
        CameraPosition.EyeOverride = null;
        for (int side = 0; side < 2; side++)
        {
            if (_bareEyes[side] != null)
                Object.Destroy(_bareEyes[side]!.gameObject);
            _bareEyes[side] = null;
            if (_bareEyePictures[side] != null)
            {
                _bareEyePictures[side]!.Release();
                Object.Destroy(_bareEyePictures[side]);
            }
            _bareEyePictures[side] = null;
        }
        _bareEyesNoted = false;
    }
}
