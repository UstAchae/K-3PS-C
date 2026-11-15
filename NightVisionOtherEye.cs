using UnityEngine;

namespace EFTNewCamera;

// EXPERIMENT (the user's, 2026-11-15): a device seen with both eyes, half each, over the whole frame. The eyes stand at
// the two aiming eyes, AimEye either side of the head's middle (F12 「镜头位置」 瞄准眼离中线). The main camera renders
// the scene once, from where it is set (not moved over to the aiming eye while aimed); the device is drawn into it from
// each eye, and each eye's lenses are mapped from that eye (NightVisionEyes.RenderLenses), and the two eyes are laid
// over the whole frame half each (_EyeMix, _DirectMix) instead of the left eye's on the left and the right's on the
// right. The scene itself is one view for both (the gun among it). To be kept or taken out once seen.
internal static partial class NightVisionEyepieces
{
    internal static bool MonocularBothEyes = true;

    // The experiment is on for this device: one with eyepieces, down, flipping or switched on.
    private static bool ExperimentOn => MonocularBothEyes && _body != null && _lensNodes.Length > 0 && (Active || _flip > 0.001f);

    // Each frame, before the camera is placed: kept where it is set while the experiment is on (not over to the aiming eye).
    private static void PlaceExperimentEye()
    {
        // The camera at the aiming eye (the shouldered side's, behind its eyepiece), aimed or not: what the game draws
        // from the camera alone (a sight's picture, a red dot's reticle, laid in at its upscaler) is that eye's. Until
        // the eyes are known, where it is set.
        float? aimingEye = CameraPosition.EyesOverride is Vector2 eyes ? (CameraPosition.AimSide >= 0f ? eyes.y : eyes.x) : (float?)null;
        // Over to it as the device comes down, back as it goes up, with the eyes (NightVisionEyes.RenderLenses).
        float apart = Mathf.SmoothStep(0f, 1f, _split);
        CameraPosition.EyeOverride = ExperimentOn ? Mathf.Lerp(CameraPosition.Across, aimingEye ?? CameraPosition.Across, apart) : (float?)null;
        if (!ExperimentOn)
            CameraPosition.EyesOverride = null;
        if (ExperimentOn && !_experimentNoted)
        {
            _experimentNoted = true;
            NewCameraPlugin.Log?.LogInfo($"NVG experiment: the eyes {CameraPosition.AimEye * 1000f:F0} mm either side of the head's middle, the camera at {CameraPosition.Across * 1000f:F0} mm; the device drawn from each, half each over the frame.");
        }
    }
    private static bool _experimentNoted;

    // The frame is rendered once (no render of its own for either eye): the camera where it is set.
    internal static Vector3 OtherEyeOffset(Quaternion turn) => Vector3.zero;

    // The player's own renderers the camera sees (the gun, the arms, the body's), drawn from each eye like the device and
    // kept from the main camera: near, each eye sees them where they are from it. Looked for twice a second; skinned ones
    // baked each frame into meshes of their own, kept.
    private const int NearSearchFrames = 30;
    private static readonly System.Collections.Generic.List<Renderer> _nearRenderers = new System.Collections.Generic.List<Renderer>();
    private static readonly System.Collections.Generic.List<Renderer> _nearFound = new System.Collections.Generic.List<Renderer>();
    private static readonly System.Collections.Generic.Dictionary<SkinnedMeshRenderer, Mesh> _baked = new System.Collections.Generic.Dictionary<SkinnedMeshRenderer, Mesh>();
    private static readonly System.Collections.Generic.Dictionary<Renderer, (Material[] materials, int[] passes, int[]? forward)> _nearSources = new System.Collections.Generic.Dictionary<Renderer, (Material[], int[], int[]?)>();
    private static readonly System.Collections.Generic.List<Material> _nearMaterials = new System.Collections.Generic.List<Material>();
    private static int _nearFrame = -1000, _nearCount;
    internal static EFT.Player? NearPlayer;   // whose gun is in the hands (its model is not under the body's)
    private static bool _nearNoted, _nearWhyNoted;

    private static void AddNearPieces(Camera camera)
    {
        _nearCount = 0;
        Transform slot = DeviceSlot();
        if (Time.frameCount - _nearFrame >= NearSearchFrames)
        {
            _nearFrame = Time.frameCount;
            _nearRenderers.Clear();
            slot.root.GetComponentsInChildren(false, _nearFound);
            foreach (Renderer r in _nearFound)
            {
                if ((r is MeshRenderer || r is SkinnedMeshRenderer) && !r.transform.IsChildOf(slot))
                    _nearRenderers.Add(r);
            }
            // The gun in the hands, a model of its own.
            GameObject? gun = NearPlayer?.HandsController?.ControllerGameObject;
            if (gun != null && !gun.transform.IsChildOf(slot.root))
            {
                gun.GetComponentsInChildren(false, _nearFound);
                foreach (Renderer r in _nearFound)
                {
                    if (r is MeshRenderer || r is SkinnedMeshRenderer)
                        _nearRenderers.Add(r);
                }
            }
        }
        int meshes = 0, skinned = 0;
        // TEMP DIAG: once, why each of the gun's renderers is or is not drawn from each eye.
        if (!_nearWhyNoted && NearPlayer?.HandsController?.ControllerGameObject is GameObject gunObject)
        {
            _nearWhyNoted = true;
            var why = new System.Collections.Generic.List<string>();
            foreach (Renderer r in gunObject.GetComponentsInChildren<Renderer>(true))
            {
                if (why.Count >= 30)
                    break;
                why.Add($"{r.name} ({r.GetType().Name}) on {r.enabled}/{r.gameObject.activeInHierarchy} off {r.forceRenderingOff} shadows {r.shadowCastingMode} layer {LayerMask.LayerToName(r.gameObject.layer)} in mask {(camera.cullingMask & (1 << r.gameObject.layer)) != 0} finest {FinestLevel(r)} listed {_nearRenderers.Contains(r)} under body {r.transform.IsChildOf(slot.root)}");
            }
            NewCameraPlugin.Log?.LogInfo("NVG experiment DIAG gun renderers: " + string.Join("; ", why));
        }
        foreach (Renderer r in _nearRenderers)
        {
            // Only what the main camera would draw: on, in its layers, not left to shadows (the head the body hides).
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r.forceRenderingOff
                || r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly
                || (camera.cullingMask & (1 << r.gameObject.layer)) == 0 || !FinestLevel(r))
                continue;
            Mesh? mesh = null;
            if (r is SkinnedMeshRenderer skin)
            {
                if (skin.sharedMesh == null)
                    continue;
                if (!_baked.TryGetValue(skin, out mesh) || mesh == null)
                {
                    mesh = new Mesh { name = "Justachae near " + skin.name, hideFlags = HideFlags.DontSave };
                    mesh.MarkDynamic();
                    _baked[skin] = mesh;
                }
                skin.BakeMesh(mesh);
                skinned++;
            }
            else if (r.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null)
            {
                mesh = filter.sharedMesh;
                meshes++;
            }
            if (mesh == null)
                continue;
            r.GetSharedMaterials(_nearMaterials);
            if (!_nearSources.TryGetValue(r, out (Material[] materials, int[] passes, int[]? forward) source) || !SameMaterials(source.materials, _nearMaterials))
            {
                // Its solid parts into the G-buffers like the device; its see-through ones (a sight's lens, the reticle)
                // drawn after the scene's (NightVisionDirect.DrawDirect).
                Material[] materials = _nearMaterials.ToArray();
                int[] passes = new int[materials.Length], forward = new int[materials.Length];
                bool anyForward = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    passes[i] = GBufferPass(materials[i]);
                    forward[i] = ForwardPass(materials[i]);
                    anyForward |= forward[i] >= 0;
                }
                source = (materials, passes, anyForward ? forward : null);
                _nearSources[r] = source;
            }
            if (System.Array.TrueForAll(source.passes, pass => pass < 0) && source.forward == null)
                continue;
            _hidden.Add((r, r.shadowCastingMode));
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            _pieces.Add(new Piece
            {
                Renderer = r, Mesh = mesh, Materials = source.materials, Passes = source.passes, Near = true, Forward = source.forward,
                FromCamera = FromCamera(r.transform, camera.transform.position),
            });
            _nearCount++;
        }
        if (!_nearNoted && _nearCount > 0)
        {
            _nearNoted = true;
            NewCameraPlugin.Log?.LogInfo($"NVG experiment: the player's own drawn from each eye too: {meshes} meshes, {skinned} skinned (baked), of {_nearRenderers.Count} found.");
            // TEMP DIAG: which they are, and how near.
            var names = new System.Collections.Generic.List<string>();
            foreach (Piece piece in _pieces)
            {
                if (piece.Near && names.Count < 40)
                    names.Add($"{piece.Renderer.name}{(piece.Renderer is SkinnedMeshRenderer ? "*" : "")} [{LayerMask.LayerToName(piece.Renderer.gameObject.layer)}] {(piece.Renderer.bounds.Contains(camera.transform.position) ? "AROUND-EYE " : "")}{Vector3.Distance(piece.Renderer.bounds.center, camera.transform.position):F2}m");
            }
            NewCameraPlugin.Log?.LogInfo("NVG experiment DIAG near pieces: " + string.Join("; ", names));
            _diagReadAt = Time.frameCount + 90;
        }
        DiagReadMaps();
    }

    // TEMP DIAG: once, a while after the experiment starts: how much of each eye's lens map, glass map and near mask is
    // set (the share of pixels not zero), to find where the lens's picture went.
    private static int _diagReadAt = -1;
    private static void DiagReadMaps()
    {
        if (_diagReadAt < 0 || Time.frameCount < _diagReadAt)
            return;
        _diagReadAt = -1;
        void Read(string what, RenderTexture? texture)
        {
            if (texture == null)
            {
                NewCameraPlugin.Log?.LogInfo($"NVG experiment DIAG {what}: none");
                return;
            }
            UnityEngine.Rendering.AsyncGPUReadback.Request(texture, 0, request =>
            {
                if (request.hasError)
                {
                    NewCameraPlugin.Log?.LogInfo($"NVG experiment DIAG {what}: readback failed");
                    return;
                }
                Unity.Collections.NativeArray<byte> data = request.GetData<byte>();
                int pixels = request.width * request.height, step = Mathf.Max(data.Length / Mathf.Max(pixels, 1), 1), set = 0;
                for (int p = 0; p < pixels; p++)
                {
                    for (int b = 0; b < step; b++)
                    {
                        if (data[p * step + b] != 0)
                        {
                            set++;
                            break;
                        }
                    }
                }
                // A lens map's four channels are the four tubes': how much of the frame each one lights.
                string tubes = "";
                if (step == 4)
                {
                    var lit = new int[4];
                    for (int p = 0; p < pixels; p++)
                        for (int c = 0; c < 4; c++)
                            if (data[p * 4 + c] > 8)
                                lit[c]++;
                    tubes = $"; per channel {100f * lit[0] / pixels:F1}% {100f * lit[1] / pixels:F1}% {100f * lit[2] / pixels:F1}% {100f * lit[3] / pixels:F1}%";
                }
                NewCameraPlugin.Log?.LogInfo($"NVG experiment DIAG {what} ({request.width}x{request.height}, {texture.format}): {100f * set / Mathf.Max(pixels, 1):F1}% set{tubes}");
            });
        }
        for (int side = 0; side < 2; side++)
        {
            Read("lens map " + side, _lensMaps[side]);
            Read("glass map " + side, _glassMaps[side]);
            Read("near mask " + side, _directNear[side]);
        }
        NewCameraPlugin.Log?.LogInfo($"NVG experiment DIAG: active {Active}, direct {_direct}, tube power {_power[0]:F2} {_power[2]:F2}, eyes frame {_eyesFrame == Time.frameCount}.");
    }

    private static void ReleaseOtherEye()
    {
        CameraPosition.EyeOverride = null;
        CameraPosition.EyesOverride = null;
        _experimentNoted = false;
        _nearNoted = false;
        _nearRenderers.Clear();
        _nearSources.Clear();
        _nearFrame = -1000;
        _nearCount = 0;
        foreach (Mesh mesh in _baked.Values)
        {
            if (mesh != null)
                Object.Destroy(mesh);
        }
        _baked.Clear();
    }
}
