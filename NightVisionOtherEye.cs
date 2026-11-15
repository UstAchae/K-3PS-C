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
        CameraPosition.EyeOverride = ExperimentOn ? CameraPosition.Across : (float?)null;
        if (ExperimentOn && !_experimentNoted)
        {
            _experimentNoted = true;
            NewCameraPlugin.Log?.LogInfo($"NVG experiment: the eyes {CameraPosition.AimEye * 1000f:F0} mm either side of the head's middle, the camera at {CameraPosition.Across * 1000f:F0} mm; the device drawn from each, half each over the frame.");
        }
    }
    private static bool _experimentNoted;

    // The player's own renderers the camera sees (the gun, the arms, the body's), drawn from each eye like the device and
    // kept from the main camera: near, each eye sees them where they are from it. Looked for twice a second; skinned ones
    // baked each frame into meshes of their own, kept.
    private const int NearSearchFrames = 30;
    private static readonly System.Collections.Generic.List<Renderer> _nearRenderers = new System.Collections.Generic.List<Renderer>();
    private static readonly System.Collections.Generic.List<Renderer> _nearFound = new System.Collections.Generic.List<Renderer>();
    private static readonly System.Collections.Generic.Dictionary<SkinnedMeshRenderer, Mesh> _baked = new System.Collections.Generic.Dictionary<SkinnedMeshRenderer, Mesh>();
    private static readonly System.Collections.Generic.Dictionary<Renderer, (Material[] materials, int[] passes)> _nearSources = new System.Collections.Generic.Dictionary<Renderer, (Material[], int[])>();
    private static readonly System.Collections.Generic.List<Material> _nearMaterials = new System.Collections.Generic.List<Material>();
    private static int _nearFrame = -1000, _nearCount;
    private static bool _nearNoted;

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
        }
        int meshes = 0, skinned = 0;
        foreach (Renderer r in _nearRenderers)
        {
            // Only what the main camera would draw: on, in its layers, not left to shadows (the head the body hides).
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r.forceRenderingOff
                || r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly
                || (camera.cullingMask & (1 << r.gameObject.layer)) == 0 || !Opaque(r) || !FinestLevel(r))
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
            if (!_nearSources.TryGetValue(r, out (Material[] materials, int[] passes) source) || !SameMaterials(source.materials, _nearMaterials))
            {
                Material[] materials = _nearMaterials.ToArray();
                int[] passes = new int[materials.Length];
                for (int i = 0; i < materials.Length; i++)
                    passes[i] = GBufferPass(materials[i]);
                source = (materials, passes);
                _nearSources[r] = source;
            }
            _hidden.Add((r, r.shadowCastingMode));
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            _pieces.Add(new Piece
            {
                Renderer = r, Mesh = mesh, Materials = source.materials, Passes = source.passes, Near = true,
                FromCamera = FromCamera(r.transform, camera.transform.position),
            });
            _nearCount++;
        }
        if (!_nearNoted && _nearCount > 0)
        {
            _nearNoted = true;
            NewCameraPlugin.Log?.LogInfo($"NVG experiment: the player's own drawn from each eye too: {meshes} meshes, {skinned} skinned (baked), of {_nearRenderers.Count} found.");
        }
    }

    private static void ReleaseOtherEye()
    {
        CameraPosition.EyeOverride = null;
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
