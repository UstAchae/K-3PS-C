using EFT.CameraControl;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace EFTNewCamera;

// EXPERIMENT (the user's, 2026-11-15): a monocular (the PVS-14) seen with both eyes. The eyepieces stand down (the
// game draws the device as it is, nothing seen through its lens), and each eye's whole view is rendered from where that
// eye stands, half the eyes' distance either side of the head's middle, with the camera's turn and view, both bare (no
// night vision): the two laid over each other at half each, in place of the frame. Each by a camera of its own, the
// game's scope camera prefab (as the thermal goggles' is), so it has the game's lighting and post-processing.
// To be kept or taken out once seen.
internal static partial class NightVisionEyepieces
{
    internal static bool MonocularBothEyes = true;
    private const float EyesApart = 0.064f;   // between the pupils (m)
    private const int TubeHalvesPass = 1;     // NvgTube.shader: two pictures, mixed

    private static readonly Camera?[] _bareEyes = new Camera?[2];
    private static readonly RenderTexture?[] _bareEyePictures = new RenderTexture?[2];
    private static int _bareEyesFrame = -100;
    private static bool _bareEyesNoted;
    private static readonly int OtherId = Shader.PropertyToID("_Other");
    private static readonly int OtherShareId = Shader.PropertyToID("_OtherShare");

    // Whether the experiment takes this device: a monocular, worn down and on.
    private static bool BothEyesBare => MonocularBothEyes && _body != null && _lensNodes.Length > 0 && _device.Monocular;

    // The main camera draws nothing while the eyes do (its frame is replaced): its culling mask emptied as it culls, given
    // back once it has rendered; the eyes draw what it would have.
    private static int _mainMask;
    private static bool _mainMaskHeld;

    internal static void HoldMainView(Camera main)
    {
        if (_mainMaskHeld || !BothEyesBare || !Active)
            return;
        _mainMask = main.cullingMask;
        _mainMaskHeld = true;
        main.cullingMask = 0;
    }

    internal static void GiveMainViewBack(Camera main)
    {
        if (!_mainMaskHeld)
            return;
        _mainMaskHeld = false;
        main.cullingMask = _mainMask;
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

    // Both eyes' views this frame, as the main camera is about to render.
    private static void RenderBareEyes(Camera main)
    {
        if (!BothEyesBare || !Active)
            return;
        SSAA? ssaa = main.GetComponent<SSAA>();
        int width = Mathf.Max(ssaa != null ? ssaa.GetInputWidth() : Screen.width, 1), height = Mathf.Max(ssaa != null ? ssaa.GetInputHeight() : Screen.height, 1);
        Transform view = main.transform;
        // The head's middle: the camera is CameraPosition.Horizontal to its right.
        Vector3 middle = view.position - view.right * CameraPosition.Horizontal;
        for (int side = 0; side < 2; side++)
        {
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
                    name = "Justachae bare eye picture " + side,
                    filterMode = FilterMode.Bilinear,
                };
                picture.Create();
                _bareEyePictures[side] = picture;
            }
            camera.CopyFrom(main);
            if (_mainMaskHeld)
                camera.cullingMask = _mainMask;
            camera.transform.SetPositionAndRotation(middle + view.right * ((side == 0 ? -0.5f : 0.5f) * EyesApart), view.rotation);
            camera.rect = new Rect(0f, 0f, 1f, 1f);
            camera.projectionMatrix = main.nonJitteredProjectionMatrix;
            camera.targetTexture = picture;
            try
            {
                camera.Render();
            }
            catch (System.Exception e)
            {
                NewCameraPlugin.Log?.LogWarning("NVG experiment: an eye's view failed: " + e);
                return;
            }
            finally
            {
                camera.ResetProjectionMatrix();
                camera.targetTexture = null;
            }
        }
        _bareEyesFrame = Time.frameCount;
        if (!_bareEyesNoted)
        {
            _bareEyesNoted = true;
            NewCameraPlugin.Log?.LogInfo($"NVG experiment: the eyepieces stood down; both eyes' bare views rendered {EyesApart * 1000f:F0} mm apart about the head's middle ({CameraPosition.Horizontal * 1000f:F0} mm left of the camera), {width}x{height}, half each.");
        }
    }

    // In place of the frame: the two eyes' views, half each. False when they were not made this frame.
    private static bool LayBareEyes(RenderTexture into)
    {
        if (_bareEyesFrame != Time.frameCount || _bareEyePictures[0] == null || _bareEyePictures[1] == null || _tubeShader == null)
            return false;
        Material halves = _tubeMaterial ??= new Material(_tubeShader) { name = "Justachae NVG tube", hideFlags = HideFlags.DontSave };
        halves.SetTexture(OtherId, _bareEyePictures[1]);
        halves.SetFloat(OtherShareId, 0.5f);
        Graphics.Blit(_bareEyePictures[0], into, halves, TubeHalvesPass);
        halves.SetTexture(OtherId, Texture2D.blackTexture);
        return true;
    }

    private static void ReleaseOtherEye()
    {
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
