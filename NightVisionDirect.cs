using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// The two eyepieces brought together with no camera of their own: perspective being only where a thing stands from the eye, a
// half of the device moved by the opposite of its eye's shift looks as it did from that eye. The game's own renderers are left to
// their shadow on such frames, and each half is drawn apart into G-buffers of its own, seen from its eye, then put into the main
// camera's, so the lights, the shadows and the game's effects fall on it as on anything the camera draws.
internal static partial class NightVisionEyepieces
{
    private const float DirectFar = 100f;
    private const float DirectDepthRatio = 2e5f;  // the device's own depth kept to this ratio: below it the lights flickered on it
    private const float FrontBy = 0.012f;         // m: how much nearer an eyepiece has to stand to be all before the other
    private const float DirectInsetPixels = 6f;   // of the screen: past a pixel of the upscaler's render and its jitter
    private static float DirectNear => Mathf.Max(EyeNear, DirectFar / DirectDepthRatio);

    private struct Piece
    {
        internal Renderer Renderer;
        internal Mesh Mesh;
        internal Matrix4x4 FromCamera;        // its mesh's frame from the camera (turned with the world, not moved)
        internal Material[] Materials;
        internal int[] Passes;                // each material's G-buffer pass (-1: none)
    }

    private struct PieceSource
    {
        internal MeshFilter? Filter;
        internal bool Finest;
        internal Material[] Materials;
        internal int[] Passes;
        internal bool Any;
    }

    private static readonly int[] DirectLeft = { Shader.PropertyToID("_DirectL0"), Shader.PropertyToID("_DirectL1"), Shader.PropertyToID("_DirectL2"), Shader.PropertyToID("_DirectL3") };
    private static readonly int[] DirectRight = { Shader.PropertyToID("_DirectR0"), Shader.PropertyToID("_DirectR1"), Shader.PropertyToID("_DirectR2"), Shader.PropertyToID("_DirectR3") };
    private static readonly int[] DirectMasks = { Shader.PropertyToID("_DirectMaskL"), Shader.PropertyToID("_DirectMaskR") };
    private static readonly int[] DirectLens = { Shader.PropertyToID("_DirectLens0"), Shader.PropertyToID("_DirectLens1") };
    private static readonly int DirectLeftDepth = Shader.PropertyToID("_DirectLD"), DirectRightDepth = Shader.PropertyToID("_DirectRD");
    private static readonly int DirectPlanes = Shader.PropertyToID("_DirectPlanes"), DirectInset = Shader.PropertyToID("_DirectInset");
    private static readonly int DirectSplit = Shader.PropertyToID("_DirectSplit"), DirectStrip = Shader.PropertyToID("_DirectStrip");
    private static readonly int DirectRange = Shader.PropertyToID("_DirectRange"), MergedRangeId = Shader.PropertyToID("_MergedRange");
    private static readonly int DirectPlane = Shader.PropertyToID("_DirectPlane"), DirectKeep = Shader.PropertyToID("_DirectKeep");
    private static readonly int DirectChoiceId = Shader.PropertyToID("_DirectChoice"), DirectFront = Shader.PropertyToID("_DirectFront");
    private static readonly int MergedEyesId = Shader.PropertyToID("_MergedEyes");
    private static readonly int CameraPlaceId = Shader.PropertyToID("_WorldSpaceCameraPos");
    private static readonly RenderTextureFormat[] DirectFormats = { RenderTextureFormat.ARGB32, RenderTextureFormat.ARGB32, RenderTextureFormat.ARGB2101010, RenderTextureFormat.DefaultHDR };
    private static readonly RenderTargetIdentifier[][] _directTargets = { new RenderTargetIdentifier[4], new RenderTargetIdentifier[4] };
    private static readonly RenderTargetIdentifier[] _directPlaces = new RenderTargetIdentifier[2];
    private static readonly RenderTargetIdentifier[] _mainGBuffer =
    {
        BuiltinRenderTextureType.GBuffer0, BuiltinRenderTextureType.GBuffer1, BuiltinRenderTextureType.GBuffer2, BuiltinRenderTextureType.CameraTarget,
    };
    private static readonly RenderTargetIdentifier[] _mainSurface = { BuiltinRenderTextureType.GBuffer0, BuiltinRenderTextureType.GBuffer1, BuiltinRenderTextureType.GBuffer2 };

    private static Shader? _directShader;
    private static Material? _directMaterial;
    private static Mesh? _directQuad;
    private static CommandBuffer? _directBuffer, _directAgain, _directEnd;
    private static readonly int DirectLitNow = Shader.PropertyToID("_DirectLitNow"), DirectTexel = Shader.PropertyToID("_DirectTexel");
    private static Camera? _directCamera;
    private static RenderTexture? _directChoice;
    private static int _arrangedCount = -1;
    private static bool _direct;

    private static readonly List<Renderer> _slotRenderers = new List<Renderer>();
    private static readonly List<(Renderer renderer, ShadowCastingMode casting)> _hidden = new List<(Renderer, ShadowCastingMode)>();
    private static readonly List<Piece> _pieces = new List<Piece>();
    private static readonly List<Material> _materialsNow = new List<Material>();
    private static readonly Dictionary<MeshRenderer, PieceSource> _pieceSources = new Dictionary<MeshRenderer, PieceSource>();
    private static readonly MaterialPropertyBlock _pieceBlock = new MaterialPropertyBlock();

    // The renderers as they are this frame, not as they were found once: a device whose renderers had been made anew was
    // not hidden, and four eyepieces were seen.
    private static void HideDevice()
    {
        _pieces.Clear();
        DeviceSlot().GetComponentsInChildren(false, _slotRenderers);
        foreach (Renderer r in _slotRenderers)
        {
            _hidden.Add((r, r.shadowCastingMode));
            r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            if (r is not MeshRenderer mesh || !r.enabled || !Opaque(r))
                continue;
            PieceSource source = SourceOf(mesh);
            if (!source.Finest || !source.Any || source.Filter == null || source.Filter.sharedMesh == null)
                continue;
            _pieces.Add(new Piece
            {
                Renderer = r, Mesh = source.Filter.sharedMesh, Materials = source.Materials, Passes = source.Passes,
                FromCamera = FromCamera(r.transform, Vector3.zero),
            });
        }
    }

    private static void ShowDeviceAgain()
    {
        foreach ((Renderer renderer, ShadowCastingMode casting) in _hidden)
        {
            if (renderer != null)
                renderer.shadowCastingMode = casting;
        }
        _hidden.Clear();
    }

    private static PieceSource SourceOf(MeshRenderer r)
    {
        r.GetSharedMaterials(_materialsNow);
        if (_pieceSources.TryGetValue(r, out PieceSource known) && SameMaterials(known.Materials, _materialsNow))
            return known;
        if (_pieceSources.Count > 512)
            _pieceSources.Clear();
        var source = new PieceSource
        {
            Filter = r.TryGetComponent(out MeshFilter filter) ? filter : null,
            Finest = FinestLevel(r),
            Materials = _materialsNow.ToArray(),
        };
        source.Passes = new int[source.Materials.Length];
        for (int i = 0; i < source.Materials.Length; i++)
        {
            source.Passes[i] = GBufferPass(source.Materials[i]);
            source.Any |= source.Passes[i] >= 0;
        }
        _pieceSources[r] = source;
        return source;
    }

    private static bool SameMaterials(Material[] known, List<Material> now)
    {
        if (known.Length != now.Count)
            return false;
        for (int i = 0; i < known.Length; i++)
        {
            if (!ReferenceEquals(known[i], now[i]))
                return false;
        }
        return true;
    }

    private static int GBufferPass(Material? material)
    {
        if (material == null)
            return -1;
        for (int i = 0; i < material.passCount; i++)
        {
            // The game's shaders spell it in capitals.
            if (string.Equals(material.shader.FindPassTagValue(i, new ShaderTagId("LightMode")).name, "Deferred", System.StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static bool FinestLevel(Renderer renderer)
    {
        LODGroup? group = renderer.GetComponentInParent<LODGroup>();
        if (group == null)
            return true;
        LOD[] lods = group.GetLODs();
        if (lods.Length == 0 || System.Array.IndexOf(lods[0].renderers, renderer) >= 0)
            return true;
        for (int i = 1; i < lods.Length; i++)
        {
            if (System.Array.IndexOf(lods[i].renderers, renderer) >= 0)
                return false;
        }
        return true;
    }

    private static void ClearDirect()
    {
        _directBuffer?.Clear();
        _directAgain?.Clear();
        _directEnd?.Clear();
    }

    private static void DrawDirect(Camera main, Vector3 leftShift, Vector3 rightShift, float split, Vector3 middle, Vector3 across, float nearerRight, bool monocular)
    {
        _directBuffer ??= new CommandBuffer { name = "Justachae NVG device in the main camera" };
        _directAgain ??= new CommandBuffer { name = "Justachae NVG device after the decals" };
        _directEnd ??= new CommandBuffer { name = "Justachae NVG device's fade evened" };
        CommandBuffer buffer = _directBuffer;
        buffer.Clear();
        _directAgain.Clear();
        _directEnd.Clear();
        if (_directShader == null)
            return;
        if (_directCamera != main)
        {
            if (_directCamera != null)
            {
                _directCamera.RemoveCommandBuffer(CameraEvent.AfterGBuffer, buffer);
                _directCamera.RemoveCommandBuffer(CameraEvent.BeforeLighting, _directAgain);
                _directCamera.RemoveCommandBuffer(CameraEvent.AfterImageEffectsOpaque, _directEnd);
            }
            main.AddCommandBuffer(CameraEvent.AfterGBuffer, buffer);
            _directCamera = main;
            _arrangedCount = -1;
        }
        // The last before the lights, whatever the game has added since.
        if (main.commandBufferCount != _arrangedCount)
        {
            main.RemoveCommandBuffer(CameraEvent.BeforeLighting, _directAgain);
            main.AddCommandBuffer(CameraEvent.BeforeLighting, _directAgain);
            main.RemoveCommandBuffer(CameraEvent.AfterImageEffectsOpaque, _directEnd);
            main.AddCommandBuffer(CameraEvent.AfterImageEffectsOpaque, _directEnd);
            _arrangedCount = main.commandBufferCount;
        }
        _directMaterial ??= new Material(_directShader) { name = "Justachae NVG device in the main camera", hideFlags = HideFlags.DontSave };
        if (_directQuad == null)
        {
            _directQuad = new Mesh
            {
                name = "Justachae NVG whole frame",
                vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(-1f, 1f, 0f) },
                triangles = new[] { 0, 1, 2, 0, 2, 3 },
                bounds = new Bounds(Vector3.zero, Vector3.one * 1000f),
                hideFlags = HideFlags.DontSave,
            };
        }
        int width = main.pixelWidth, height = main.pixelHeight;
        // Each lens as far past the split as it can reach; all the frame with one eyepiece before the other, nearer and larger.
        float front = monocular ? 0f : Mathf.Clamp(nearerRight / FrontBy, -1f, 1f);
        float reach = 0.15f + 0.85f * Mathf.Abs(front);
        Vector4 range = monocular ? new Vector4(0f, 1f, 0f, 0f) : new Vector4(Mathf.Clamp01(split - reach), Mathf.Clamp01(split + reach), 0f, 0f);
        buffer.SetGlobalVector(DirectRange, range);
        _binocularMaterial!.SetVector(MergedRangeId, range);

        Vector3 place = main.transform.position;
        Matrix4x4 turned = main.worldToCameraMatrix;
        turned.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
        // The main camera's view with a near plane of the device's own, to a far plane past all of it; the jitter and the
        // fisheye's field as they are.
        float near = DirectNear;
        Matrix4x4 projection = main.projectionMatrix;
        projection.m22 = -(DirectFar + near) / (DirectFar - near);
        projection.m23 = -2f * DirectFar * near / (DirectFar - near);
        buffer.SetGlobalVector(DirectPlanes, new Vector4(1f / near, 1f / DirectFar, 1f / main.nearClipPlane, 1f / main.farClipPlane));

        bool byRenderer = NvgDump.UseRenderer;
        for (int half = 0; half < 2; half++)
        {
            int[] ids = half == 0 ? DirectLeft : DirectRight;
            int depth = half == 0 ? DirectLeftDepth : DirectRightDepth;
            Vector3 shift = half == 0 ? leftShift : rightShift;
            for (int t = 0; t < 4; t++)
            {
                buffer.GetTemporaryRT(ids[t], width, height, 0, FilterMode.Point, DirectFormats[t], t < 2 ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
                _directTargets[half][t] = ids[t];
            }
            buffer.GetTemporaryRT(depth, width, height, 24, FilterMode.Point, RenderTextureFormat.Depth);
            buffer.SetRenderTarget(_directTargets[half], depth);
            buffer.ClearRenderTarget(true, true, Color.clear);
            // From where this eye truly stands: the view direction a material works out comes from the camera's place, which the
            // view matrix alone leaves where the main camera is. (Far out in a map a place in the world is kept only to some tens
            // of micrometres: the device is drawn with the eye at the origin, by its true offsets, or it shook.)
            Matrix4x4 fromEye = Matrix4x4.Translate(-shift);
            if (byRenderer)
            {
                buffer.SetViewProjectionMatrices(main.worldToCameraMatrix * fromEye, projection);
                buffer.SetGlobalVector(CameraPlaceId, new Vector4(place.x + shift.x, place.y + shift.y, place.z + shift.z, 1f));
            }
            else
            {
                buffer.SetViewProjectionMatrices(turned, projection);
                buffer.SetGlobalVector(CameraPlaceId, new Vector4(0f, 0f, 0f, 1f));
            }
            foreach (Piece piece in _pieces)
            {
                piece.Renderer.GetPropertyBlock(_pieceBlock);
                int count = Mathf.Min(piece.Materials.Length, piece.Mesh.subMeshCount);
                for (int s = 0; s < count; s++)
                {
                    if (piece.Passes[s] < 0)
                        continue;
                    if (byRenderer)
                        buffer.DrawRenderer(piece.Renderer, piece.Materials[s], s, piece.Passes[s]);
                    else
                        buffer.DrawMesh(piece.Mesh, fromEye * piece.FromCamera, piece.Materials[s], s, piece.Passes[s], _pieceBlock);
                }
            }
            // Which of what it drew is its own half, by the model's own middle: this close the middle is not one line down the frame.
            int mask = DirectMasks[half];
            buffer.GetTemporaryRT(mask, width, height, 0, FilterMode.Point, RenderTextureFormat.R8, RenderTextureReadWrite.Linear);
            buffer.SetRenderTarget(mask, depth);
            buffer.ClearRenderTarget(false, true, Color.clear);
            Vector3 point = byRenderer ? place + middle : middle - shift;
            buffer.SetGlobalVector(DirectPlane, new Vector4(across.x, across.y, across.z, Vector3.Dot(point, across)));
            buffer.SetGlobalFloat(DirectKeep, monocular ? 0f : half == 0 ? -1f : 1f);
            foreach (Piece piece in _pieces)
            {
                for (int s = 0; s < piece.Mesh.subMeshCount; s++)
                {
                    if (byRenderer)
                        buffer.DrawRenderer(piece.Renderer, _directMaterial, s, 1);
                    else
                        buffer.DrawMesh(piece.Mesh, fromEye * piece.FromCamera, _directMaterial, s, 1);
                }
            }
            buffer.SetGlobalTexture(mask, mask);
            for (int t = 0; t < 4; t++)
                buffer.SetGlobalTexture(ids[t], ids[t]);
            buffer.SetGlobalTexture(depth, depth);
        }

        // The camera's place given back before anything else is drawn.
        buffer.SetGlobalVector(CameraPlaceId, new Vector4(place.x, place.y, place.z, 1f));

        // Which lens shows where, for this frame's device and for the composite after it.
        if (_directChoice == null || _directChoice.width != width || _directChoice.height != height)
        {
            if (_directChoice != null)
            {
                _directChoice.Release();
                Object.Destroy(_directChoice);
            }
            _directChoice = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { name = "Justachae NVG lens choice", filterMode = FilterMode.Point };
            _directChoice.Create();
            Shader.SetGlobalTexture(DirectChoiceId, _directChoice);
        }
        buffer.SetGlobalFloat(DirectFront, front);
        buffer.SetGlobalFloat(DirectSplit, split);
        buffer.SetGlobalFloat(DirectStrip, Overscan);
        buffer.SetGlobalVector(DirectInset, new Vector4(DirectInsetPixels / Mathf.Max(Screen.width, 1), DirectInsetPixels / Mathf.Max(Screen.height, 1), 0f, 0f));
        for (int side = 0; side < 2; side++)
        {
            if (_lensMaps[side] != null)
                buffer.SetGlobalTexture(DirectLens[side], _lensMaps[side]);
        }
        // What the composite works out what is open by.
        Material composite = _binocularMaterial!;
        for (int side = 0; side < 2; side++)
            _directMaterial.SetMatrix(EyeInvVpIds[side], composite.GetMatrix(EyeInvVpIds[side]));
        for (int i = 0; i < TubeAxisIds.Length; i++)
            _directMaterial.SetVector(TubeAxisIds[i], composite.GetVector(TubeAxisIds[i]));
        _directMaterial.SetVector(TubeFieldId, composite.GetVector(TubeFieldId));
        buffer.SetRenderTarget(_directChoice);
        buffer.DrawMesh(_directQuad, Matrix4x4.identity, _directMaterial, 0, 3);

        // The sealed eyecups' insides go dark as the device comes down: from where each half of the device stands, not from the
        // main camera's depth, which holds what is nearest at its near plane.
        Vector3 leftOf = leftShift, rightOf = rightShift;
        Transform lensLeft = _lensNodes[CentreLensA];
        ShadeCamera(2, main, main.nonJitteredProjectionMatrix, t => (Vector3)FromCamera(t, place).GetColumn(3) - (t == lensLeft ? leftOf : rightOf));
        _shadeMainCamera = main;
        if (_device.Sealed && _shadeMaterial != null && ShadeKeep < 0.999f)
        {
            RenderTexture places = ShadeTexture(_shadePlaces, 0, width, height, 24, RenderTextureFormat.ARGBFloat);
            RenderTexture facing = ShadeTexture(_shadeFacings, 0, width, height, 0, RenderTextureFormat.ARGBHalf);
            _directPlaces[0] = places;
            _directPlaces[1] = facing;
            buffer.SetRenderTarget(_directPlaces, places.depthBuffer);
            buffer.ClearRenderTarget(true, true, Color.clear);
            buffer.SetGlobalTexture(ShadePlaceId, places);
            buffer.SetGlobalTexture(ShadeFacingId, facing);
            buffer.SetViewProjectionMatrices(turned, projection);
            for (int half = 0; half < 2; half++)
            {
                Matrix4x4 fromEye = Matrix4x4.Translate(-(half == 0 ? leftShift : rightShift));
                foreach (Piece piece in _pieces)
                {
                    for (int s = 0; s < piece.Mesh.subMeshCount; s++)
                        buffer.DrawMesh(piece.Mesh, fromEye * piece.FromCamera, _shadeMaterial, s, 1);
                }
            }
        }

        buffer.SetRenderTarget(_mainGBuffer, BuiltinRenderTextureType.CameraTarget);
        buffer.SetViewProjectionMatrices(main.worldToCameraMatrix, main.projectionMatrix);
        buffer.DrawMesh(_directQuad, Matrix4x4.identity, _directMaterial, 0, 0);
        NvgDump.AfterInsert(buffer, main, DirectLeft, DirectRight);

        // After the decals, which are thrown on whatever the depth holds. The textures are let go only after this.
        buffer = _directAgain;
        buffer.SetRenderTarget(_mainSurface, BuiltinRenderTextureType.CameraTarget);
        buffer.DrawMesh(_directQuad, Matrix4x4.identity, _directMaterial, 0, 2);
        buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        // Once all is lit: the fade. The textures are let go only after this.
        buffer = _directEnd;
        buffer.SetGlobalVector(DirectTexel, new Vector4(1f / Mathf.Max(width, 1), 1f / Mathf.Max(height, 1), 0f, 0f));
        buffer.GetTemporaryRT(DirectLitNow, width, height, 0, FilterMode.Point, RenderTextureFormat.DefaultHDR);
        buffer.Blit(BuiltinRenderTextureType.CameraTarget, DirectLitNow);
        buffer.SetGlobalTexture(DirectLitNow, DirectLitNow);
        buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        buffer.DrawMesh(_directQuad, Matrix4x4.identity, _directMaterial, 0, 4);
        buffer.ReleaseTemporaryRT(DirectLitNow);
        for (int t = 0; t < 4; t++)
        {
            buffer.ReleaseTemporaryRT(DirectLeft[t]);
            buffer.ReleaseTemporaryRT(DirectRight[t]);
        }
        buffer.ReleaseTemporaryRT(DirectMasks[0]);
        buffer.ReleaseTemporaryRT(DirectMasks[1]);
        buffer.ReleaseTemporaryRT(DirectLeftDepth);
        buffer.ReleaseTemporaryRT(DirectRightDepth);
    }

    // Temporary: why a device down does or does not go this way, each time any of it changes.
    private static int _stateNoted = -1;
    private static void NoteState(bool present, bool worn, bool switchedOn, bool renderer, bool brnvg, bool settled, bool on, bool filter)
    {
        bool down = _flip >= 0.999f, moving = _flip > 0.001f && !down, image = _eyepieceImage != null && _eyepieceImage.enabled;
        int state = (present ? 1 : 0) | (worn ? 2 : 0) | (switchedOn ? 4 : 0) | (renderer ? 8 : 0) | (brnvg ? 16 : 0) | (settled ? 32 : 0) | (on ? 64 : 0)
            | (filter ? 128 : 0) | (down ? 256 : 0) | (moving ? 512 : 0) | (image ? 1024 : 0) | (_direct ? 2048 : 0) | (_precise ? 4096 : 0) | (Active ? 8192 : 0);
        if (state == _stateNoted)
            return;
        _stateNoted = state;
        Log($"NVG state: present {present}, worn {worn}, switched on {switchedOn}, BRNVG renderer {renderer}, wants BRNVG {brnvg}, settled {settled}, tubes on {on}, game's filter {filter}, flip {_flip:F2}, image {image}, direct {_direct}, precise {_precise}, body {(_body != null ? _body.name : "none")}");
    }

    // Temporary: what the capture needs to know.
    internal static float FlipNow => _flip;

    internal static void DescribeDevice(Camera main)
    {
        Log($"NVG dump: flip {_flip:F3}, direct {_direct}, pieces {_pieces.Count}, renderers {_slotRenderers.Count}, body {(_body != null ? _body.name : "none")} under {(_body != null ? _body.root.name : "none")}, near {main.nearClipPlane}, far {main.farClipPlane}, device {_device.Node}, shift share {EyeShift}, depth mode {main.depthTextureMode}");
        foreach (Renderer r in _slotRenderers)
        {
            if (r == null)
                continue;
            var text = new System.Text.StringBuilder();
            foreach (Material m in r.sharedMaterials)
            {
                if (m != null)
                    text.Append(m.shader.name).Append(" q").Append(m.renderQueue).Append(" deferred ").Append(GBufferPass(m)).Append(' ').Append(string.Join(",", m.shaderKeywords)).Append("; ");
            }
            Log($"NVG dump, {r.name}: drawn {_pieces.Exists(p => p.Renderer == r)}, probes {r.lightProbeUsage}/{r.reflectionProbeUsage}, {text}");
        }
    }

    private static void Log(string text) => NewCameraPlugin.Log?.LogInfo(text);
}
