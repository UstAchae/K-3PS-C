using UnityEngine;

namespace EFTNewCamera;

// EXPERIMENT (the user's, 2026-11-15): a device seen with both eyes. The scene is rendered once, by the main camera at
// the aiming eye; the device, the arms and the gun are drawn into it from each eye (behind its eyepiece's inner lens),
// and each eye's lenses mapped from it. Agreed rules (1.9.58): the aiming eye (the shouldered side's) first, nothing
// covers what it sees; the other eye's gun and arms only outside the eyepieces, faint, never in an eyepiece, the
// night-vision picture or the thermal; in an eyepiece its picture whole; outside, the device and scene by where across
// the frame; the eyes slide out from the middle as the device flips down (NvgDirect.shader, NvgBinocular.shader).
internal static partial class NightVisionEyepieces
{
    internal static bool MonocularBothEyes = true;
    // For SightLensHalves: the experiment on this frame, and the shader its mixing pass is in.
    internal static bool TwoEyesNow => ExperimentOn;
    internal static Shader? TubeShader => _tubeShader;
    internal static float ScopeLightNow => _scope.Through ? _scope.Light : 1f;
    internal static Color PhosphorNow => PhosphorColor();

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
            // The gun in the hands, a model of its own apart from the body: its whole hierarchy, found from the firearm
            // controller's weapon root, or from the sight aimed through.
            Transform? gun = GunRoot();
            int gunRenderers = 0;
            if (gun != null && !gun.IsChildOf(slot.root))
            {
                gun.GetComponentsInChildren(false, _nearFound);
                foreach (Renderer r in _nearFound)
                {
                    if (r is MeshRenderer || r is SkinnedMeshRenderer)
                    {
                        _nearRenderers.Add(r);
                        gunRenderers++;
                    }
                }
            }
            // TEMP DIAG: once the gun has renderers, why each is or is not drawn from each eye.
            if (!_nearWhyNoted && gun != null && gunRenderers > 0)
            {
                _nearWhyNoted = true;
                var why = new System.Collections.Generic.List<string>();
                foreach (Renderer r in gun.GetComponentsInChildren<Renderer>(false))
                {
                    if (why.Count >= 30)
                        break;
                    why.Add($"{r.name} ({r.GetType().Name}) on {r.enabled} off {r.forceRenderingOff} shadows {r.shadowCastingMode} layer {LayerMask.LayerToName(r.gameObject.layer)} in mask {(camera.cullingMask & (1 << r.gameObject.layer)) != 0} finest {FinestLevel(r)}");
                }
                NewCameraPlugin.Log?.LogInfo($"NVG experiment DIAG gun {gun.name}: {gunRenderers} renderers; " + string.Join("; ", why));
            }
        }
        int meshes = 0, skinned = 0;
        DiagSightLens(camera);
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
            // Wholly see-through (a sight's lens or glass alone): left to the game.
            if (System.Array.TrueForAll(source.passes, pass => pass < 0))
                continue;
            _hidden.Add((r, r.shadowCastingMode));
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            _pieces.Add(new Piece
            {
                // Its see-through parts (a sight's glass, its lens) not drawn again from each eye: drawn there, a sight's lens
                // had no picture (that is laid in at the upscaler, SightLensHalves) and showed as a black disc.
                Renderer = r, Mesh = mesh, Materials = source.materials, Passes = source.passes, Near = true, Forward = null,
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

    // The gun in the hands' whole model: the root of the firearm controller's weapon root (or, failing that, of the sight
    // aimed through).
    private static System.Reflection.PropertyInfo? _weaponRootProperty;
    private static System.Type? _weaponRootOf;
    private static Transform? GunRoot()
    {
        object? hands = NearPlayer?.HandsController;
        Transform? at = null;
        if (hands != null)
        {
            if (_weaponRootOf != hands.GetType())
            {
                _weaponRootOf = hands.GetType();
                _weaponRootProperty = HarmonyLib.AccessTools.Property(_weaponRootOf, "WeaponRoot");
            }
            at = _weaponRootProperty?.GetValue(hands) as Transform;
        }
        if (at == null)
            at = Scopes.Current(NearPlayer?.ProceduralWeaponAnimation)?.ScopePrefabCache?.transform;
        return at != null ? at.root : null;
    }

    // TEMP DIAG: once, the material the game lays a sight's lens in with at its upscaler (SSAAPropagator), to see how it
    // could be laid in half.
    private static readonly HarmonyLib.AccessTools.FieldRef<SSAAPropagator, Material> DiagLensMaterial =
        HarmonyLib.AccessTools.FieldRefAccess<SSAAPropagator, Material>("_opticLensMaterial");
    private static bool _sightLensNoted;
    private static void DiagSightLens(Camera camera)
    {
        if (_sightLensNoted || !camera.TryGetComponent(out SSAAPropagator propagator))
            return;
        Material? material = DiagLensMaterial(propagator);
        if (material == null || material.shader == null)
            return;
        _sightLensNoted = true;
        var properties = new System.Collections.Generic.List<string>();
        for (int i = 0; i < material.shader.GetPropertyCount(); i++)
            properties.Add(material.shader.GetPropertyName(i));
        NewCameraPlugin.Log?.LogInfo($"NVG experiment DIAG sight lens material {material.name}, shader {material.shader.name}, queue {material.renderQueue}, passes {material.passCount}; properties {string.Join(", ", properties)}");
    }

    // The experiment at the eyepieces' stage: each eye's own frame (the frame of the two half each, as the game finished
    // it, scaled at each pixel by how that eye's lit frame stands to the two's mean, NvgTube pass 3), its own picture made
    // from it, its own eyepieces (only the right tube with the other mod's), and the two eyes' results half each.
    private const int EyeFromBlendPass = 3, TubeHalves = 1, ProbePass = 4;
    private static readonly int EyePreId = Shader.PropertyToID("_EyePre"), OtherPreId = Shader.PropertyToID("_OtherPre");
    private static readonly int OtherId = Shader.PropertyToID("_Other"), OtherShareId = Shader.PropertyToID("_OtherShare");
    private static Material? _eyesMaterial;

    private static bool ComposeEyesApart(Component camera, Material composite, Material glow, RenderTexture from, RenderTexture into)
    {
        if (_tubeShader == null || _eyesLit[0] == null || _eyesLit[1] == null)
            return false;
        Material eyes = _eyesMaterial ??= new Material(_tubeShader) { name = "Justachae NVG two eyes", hideFlags = HideFlags.DontSave };
        var temporaries = new System.Collections.Generic.List<RenderTexture>();
        RenderTexture Temporary()
        {
            RenderTexture texture = RenderTexture.GetTemporary(from.width, from.height, 0, from.format);
            temporaries.Add(texture);
            return texture;
        }
        ScopeLensStep(camera, from);
        DiagEyeLight();
        var seen = new RenderTexture[2];
        RenderTexture? fusedMade = null;
        for (int side = 0; side < 2; side++)
        {
            RenderTexture own = Temporary();
            eyes.SetTexture(EyePreId, _eyesLit[side]);
            eyes.SetTexture(OtherPreId, _eyesLit[1 - side]);
            Graphics.Blit(from, own, eyes, EyeFromBlendPass);
            RenderTexture? made = null, tubed = null, focused = null, dimmed = null;
            // The non-aiming eye's picture without the objective's near blur: that goes by the depth, which holds the
            // aiming eye's gun as well, and blurred its shape into this eye's picture.
            bool aiming = side == (CameraPosition.AimSide >= 0f ? 1 : 0);
            RenderTexture tube = ForTube(own, ref tubed);
            Texture picture = LensPicture(camera, aiming ? ThroughObjective(camera, tube, ref focused) : tube, ref made);
            picture = EaseGrain(picture, ref made);
            picture = TubesGain(picture, ref dimmed);
            ReleaseMask();
            // The other mod's picture goes into the right tube only: the right eye's.
            Texture? fusedPicture = side == 1 ? Fused(picture, ref fusedMade) : null;
            seen[side] = Temporary();
            composite.SetFloat(ProbeEyeId, side);
            Compose(composite, glow, own, seen[side], picture, fusedPicture);
            foreach (RenderTexture? texture in new[] { made, tubed, focused, dimmed })
            {
                if (texture != null)
                    RenderTexture.ReleaseTemporary(texture);
            }
        }
        composite.SetFloat(ProbeEyeId, -1f);
        eyes.SetTexture(OtherId, seen[1]);
        eyes.SetFloat(OtherShareId, 0.5f);
        Graphics.Blit(seen[0], into, eyes, TubeHalves);
        if (EyeProbes && _eyeSources[0] != null && _eyeSources[1] != null)
            DrawEyeProbes(into, _eyeSources[0]!, _eyeSources[1]!);
        if (fusedMade != null)
            RenderTexture.ReleaseTemporary(fusedMade);
        foreach (RenderTexture texture in temporaries)
            RenderTexture.ReleaseTemporary(texture);
        if (!_apartNoted)
        {
            _apartNoted = true;
            NewCameraPlugin.Log?.LogInfo("NVG experiment: the two eyes composed apart (each its own frame, picture and eyepieces), half each.");
        }
        return true;
    }
    private static bool _apartNoted;

    // TEMP DIAG: every few seconds, how bright each eye's lit frame is (the luminance below which a tenth, half, nine
    // tenths of its pixels lie), and how much of it lay under the old floor (0.002) that made both eyes' frames the same.
    private static int _diagLightAt;
    private static void DiagEyeLight()
    {
        if (Time.frameCount < _diagLightAt)
            return;
        _diagLightAt = Time.frameCount + 300;
        for (int side = 0; side < 2; side++)
        {
            RenderTexture? texture = _eyesLit[side];
            if (texture == null)
                continue;
            string name = side == 0 ? "left" : "right";
            UnityEngine.Rendering.AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBAFloat, request =>
            {
                if (request.hasError)
                    return;
                Unity.Collections.NativeArray<float> data = request.GetData<float>();
                int pixels = request.width * request.height, under = 0, step = 7, n = 0;
                var lum = new System.Collections.Generic.List<float>(pixels / step + 1);
                for (int p = 0; p < pixels; p += step)
                {
                    float y = 0.2126f * data[p * 4] + 0.7152f * data[p * 4 + 1] + 0.0722f * data[p * 4 + 2];
                    lum.Add(y);
                    if (y < 0.002f)
                        under++;
                    n++;
                }
                lum.Sort();
                float At(float f) => lum.Count > 0 ? lum[Mathf.Clamp((int)(f * lum.Count), 0, lum.Count - 1)] : 0f;
                NewCameraPlugin.Log?.LogInfo($"NVG experiment DIAG {name} eye lit: 10% {At(0.1f):G3}, 50% {At(0.5f):G3}, 90% {At(0.9f):G3}; under 0.002: {100f * under / Mathf.Max(n, 1):F0}%");
            });
        }
    }

    // TEMP PROBES (the user's, to see what goes into the mix): each eye's own drawing alone (the device, the gun, the arms,
    // as drawn from that eye, before the two are mixed), the left eye's in the lower left corner, the right's in the lower
    // right, a quarter of the frame across.
    internal static bool EyeProbes = true;
    private static readonly int ProbeEyeId = Shader.PropertyToID("_ProbeEye");
    private static void DrawEyeProbes(RenderTexture into, RenderTexture left, RenderTexture right)
    {
        int width = into.width / 4, height = into.height / 4, margin = into.height / 40;
        for (int side = 0; side < 2; side++)
        {
            RenderTexture small = RenderTexture.GetTemporary(width, height, 0, into.format);
            Graphics.Blit(side == 0 ? left : right, small);
            RenderTexture? active = RenderTexture.active;
            RenderTexture.active = into;
            GL.PushMatrix();
            // Top-left origin (as Graphics.DrawTexture's rects go): the lower corners.
            GL.LoadPixelMatrix(0, into.width, into.height, 0);
            float x = side == 0 ? margin : into.width - width - margin, y = into.height - margin - height;
            // A white frame round it (four strips), the picture inside opaque (its alpha is not coverage: drawn blended,
            // the frame's white showed through it).
            Graphics.DrawTexture(new Rect(x - 2, y - 2, width + 4, 2), Texture2D.whiteTexture);
            Graphics.DrawTexture(new Rect(x - 2, y + height, width + 4, 2), Texture2D.whiteTexture);
            Graphics.DrawTexture(new Rect(x - 2, y, 2, height), Texture2D.whiteTexture);
            Graphics.DrawTexture(new Rect(x + width, y, 2, height), Texture2D.whiteTexture);
            if (_eyesMaterial != null)
                Graphics.DrawTexture(new Rect(x, y, width, height), small, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0, Color.white, _eyesMaterial, ProbePass);
            else
                Graphics.DrawTexture(new Rect(x, y, width, height), small);
            GL.PopMatrix();
            RenderTexture.active = active;
            RenderTexture.ReleaseTemporary(small);
        }
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
