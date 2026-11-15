using EFT.CameraControl;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace EFTNewCamera;

// EXPERIMENT (the user's, 2026-11-15): a monocular (the PVS-14) seen with both eyes at once. The bare eye's whole view
// is rendered from where that eye stands, by a camera of its own (the game's scope camera prefab, as the thermal
// goggles' is, so it has the game's lighting and post-processing), and laid over the frame the other eye sees, the one
// through the eyepiece, each at half. To be kept or taken out once seen.
internal static partial class NightVisionEyepieces
{
    internal static bool MonocularBothEyes = true;
    private const float OtherEyeShare = 0.5f;
    private const int TubeHalvesPass = 1;   // NvgTube.shader: the frame and another, mixed

    private static Camera? _otherEye;
    private static RenderTexture? _otherEyePicture;
    private static int _otherEyeFrame = -100;
    private static float _otherEyeAcross;   // the bare eye, across from the camera along its right (m), this frame
    private static bool _otherEyeNoted;
    private static readonly int OtherId = Shader.PropertyToID("_Other");
    private static readonly int OtherShareId = Shader.PropertyToID("_OtherShare");

    private static bool EnsureOtherEye()
    {
        if (_otherEye != null)
            return true;
        if (UnityResourcesProxy.Load("BaseOpticCamera") is not GameObject prefab)
            return false;
        var holder = new GameObject("Justachae other eye holder");
        holder.SetActive(false);
        GameObject made = Object.Instantiate(prefab, holder.transform);
        made.name = "Justachae other eye camera";
        made.tag = "Untagged";
        foreach (MonoBehaviour behaviour in made.GetComponents<MonoBehaviour>())
        {
            if (behaviour is OpticComponentUpdater || behaviour is OpticRetrice)
                Object.DestroyImmediate(behaviour);
        }
        made.transform.SetParent(null, false);
        Object.Destroy(holder);
        Object.DontDestroyOnLoad(made);
        _otherEye = made.GetComponent<Camera>();
        if (_otherEye == null)
        {
            Object.Destroy(made);
            return false;
        }
        _otherEye.enabled = false;
        // A bare eye: no night vision, no thermal, none of a scope's lens.
        foreach (MonoBehaviour behaviour in made.GetComponents<MonoBehaviour>())
        {
            string kind = behaviour != null ? behaviour.GetType().Name : "";
            if (kind == "Fisheye" || kind == "CC_FastVignette" || kind == "OpticCullingMask" || kind == "NightVision" || kind == "ThermalVision")
                behaviour!.enabled = false;
        }
        if (made.TryGetComponent(out PostProcessLayer layer))
            layer.computeAutoExposure = false;
        return true;
    }

    // The bare eye's view this frame, from beside the camera as far as that eye is, with the camera's turn and view.
    private static void RenderOtherEye(Camera main)
    {
        if (!MonocularBothEyes || !_device.Monocular || !EnsureOtherEye())
            return;
        SSAA? ssaa = main.GetComponent<SSAA>();
        int width = Mathf.Max(ssaa != null ? ssaa.GetInputWidth() : Screen.width, 1), height = Mathf.Max(ssaa != null ? ssaa.GetInputHeight() : Screen.height, 1);
        if (_otherEyePicture == null || _otherEyePicture.width != width || _otherEyePicture.height != height)
        {
            if (_otherEyePicture != null)
            {
                _otherEyePicture.Release();
                Object.Destroy(_otherEyePicture);
            }
            _otherEyePicture = new RenderTexture(width, height, 24, main.allowHDR ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32)
            {
                name = "Justachae other eye picture",
                filterMode = FilterMode.Bilinear,
            };
            _otherEyePicture.Create();
        }
        Camera camera = _otherEye!;
        camera.CopyFrom(main);
        Transform view = main.transform;
        camera.transform.SetPositionAndRotation(view.position + view.right * _otherEyeAcross, view.rotation);
        camera.rect = new Rect(0f, 0f, 1f, 1f);
        camera.projectionMatrix = main.nonJitteredProjectionMatrix;
        camera.targetTexture = _otherEyePicture;
        try
        {
            camera.Render();
            _otherEyeFrame = Time.frameCount;
            if (!_otherEyeNoted)
            {
                _otherEyeNoted = true;
                NewCameraPlugin.Log?.LogInfo($"NVG experiment: the bare eye's view rendered {_otherEyeAcross * 1000f:F0} mm across from the camera ({width}x{height}), laid over the frame at {OtherEyeShare:P0}.");
            }
        }
        catch (System.Exception e)
        {
            NewCameraPlugin.Log?.LogWarning("NVG experiment: the bare eye's view failed: " + e);
        }
        finally
        {
            camera.ResetProjectionMatrix();
            camera.targetTexture = null;
        }
    }

    // The frame (into) and the bare eye's view, each at half, when that view was made this frame.
    private static void LayOtherEye(RenderTexture into)
    {
        if (_otherEyeFrame != Time.frameCount || _otherEyePicture == null || _tubeShader == null)
            return;
        Material halves = _tubeMaterial ??= new Material(_tubeShader) { name = "Justachae NVG tube", hideFlags = HideFlags.DontSave };
        RenderTexture seen = RenderTexture.GetTemporary(into.width, into.height, 0, into.format);
        Graphics.Blit(into, seen);
        halves.SetTexture(OtherId, _otherEyePicture);
        halves.SetFloat(OtherShareId, OtherEyeShare);
        Graphics.Blit(seen, into, halves, TubeHalvesPass);
        halves.SetTexture(OtherId, Texture2D.blackTexture);
        RenderTexture.ReleaseTemporary(seen);
    }

    private static void ReleaseOtherEye()
    {
        if (_otherEye != null)
            Object.Destroy(_otherEye.gameObject);
        _otherEye = null;
        if (_otherEyePicture != null)
        {
            _otherEyePicture.Release();
            Object.Destroy(_otherEyePicture);
        }
        _otherEyePicture = null;
        _otherEyeNoted = false;
    }
}
