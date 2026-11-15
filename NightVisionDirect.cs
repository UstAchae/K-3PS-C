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
    private const float DirectFar = 2f;           // m: past all of the device; with the ratio below, what lets the near plane come in
    private const float DirectDepthRatio = 2e5f;  // the device's own depth kept to this ratio: below it the lights flickered on it
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
    private static readonly int[] DirectLens = { Shader.PropertyToID("_DirectLens0"), Shader.PropertyToID("_DirectLens1") };
    private static readonly int DirectLeftDepth = Shader.PropertyToID("_DirectLD"), DirectRightDepth = Shader.PropertyToID("_DirectRD");
    private static readonly int DirectPlanes = Shader.PropertyToID("_DirectPlanes"), DirectInset = Shader.PropertyToID("_DirectInset");
    private static readonly int DirectSplit = Shader.PropertyToID("_DirectSplit"), DirectStrip = Shader.PropertyToID("_DirectStrip");
    private static readonly int CameraPlaceId = Shader.PropertyToID("_WorldSpaceCameraPos");
    private static readonly RenderTextureFormat[] DirectFormats = { RenderTextureFormat.ARGB32, RenderTextureFormat.ARGB32, RenderTextureFormat.ARGB2101010, RenderTextureFormat.DefaultHDR };
    private static readonly RenderTargetIdentifier[][] _directTargets = { new RenderTargetIdentifier[4], new RenderTargetIdentifier[4] };
    // Textures of our own, not from Unity's pool of temporary ones: out of the pool the right eye's specular came to hold the normals,
    // and the device glinted in colours where no colour was. Held while the device is in use (DropDirectTextures).
    private static readonly RenderTexture?[][] _directTextures = { new RenderTexture?[4], new RenderTexture?[4] };
    private static readonly RenderTexture?[] _directDepths = new RenderTexture?[2];

    private static RenderTexture DirectTexture(RenderTexture?[] kept, int which, int width, int height, int depth, RenderTextureFormat format, RenderTextureReadWrite readWrite, string name)
    {
        RenderTexture? texture = kept[which];
        if (texture != null && texture.width == width && texture.height == height)
            return texture;
        if (texture != null)
        {
            texture.Release();
            Object.Destroy(texture);
        }
        texture = new RenderTexture(width, height, depth, format, readWrite) { name = name, filterMode = FilterMode.Point, hideFlags = HideFlags.DontSave };
        texture.Create();
        kept[which] = texture;
        return texture;
    }

    private static void DropDirectTextures()
    {
        for (int half = 0; half < 2; half++)
        {
            Drop(_directDepths, half);
            for (int t = 0; t < 4; t++)
                Drop(_directTextures[half], t);
        }
    }


    private static readonly RenderTargetIdentifier[] _directPlaces = new RenderTargetIdentifier[2];
    private static readonly RenderTargetIdentifier[] _mainGBuffer =
    {
        BuiltinRenderTextureType.GBuffer0, BuiltinRenderTextureType.GBuffer1, BuiltinRenderTextureType.GBuffer2, BuiltinRenderTextureType.CameraTarget,
    };
    private static readonly RenderTargetIdentifier[] _mainSurface = { BuiltinRenderTextureType.GBuffer0, BuiltinRenderTextureType.GBuffer1, BuiltinRenderTextureType.GBuffer2 };

    private static Shader? _directShader;
    private static Material? _directMaterial;
    private static Mesh? _directQuad;
    private static CommandBuffer? _directBuffer, _directAgain, _directEnd, _directMotion;
    private static readonly int DirectLitNow = Shader.PropertyToID("_DirectLitNow"), DirectTexel = Shader.PropertyToID("_DirectTexel");
    private static Camera? _directCamera;
    private static int _arrangedCount = -1;
    private static bool _direct;
    private const int InsertPass = 0, SurfacePass = 1, EvenPass = 2, MotionPass = 3, StencilBitsPass = 4, PlacesPass = 12;
    private static readonly int ShadeLeftCupId = Shader.PropertyToID("_ShadeLeftCup");
    private static readonly List<Renderer> _slotRenderers = new List<Renderer>();
    private static readonly List<(Renderer renderer, ShadowCastingMode casting)> _hidden = new List<(Renderer, ShadowCastingMode)>();
    private static readonly List<Piece> _pieces = new List<Piece>();
    private static readonly List<Material> _materialsNow = new List<Material>();
    private static readonly Dictionary<MeshRenderer, PieceSource> _pieceSources = new Dictionary<MeshRenderer, PieceSource>();
    private static readonly MaterialPropertyBlock _pieceBlock = new MaterialPropertyBlock();

    // The renderers as they are this frame, not as they were found once: a device whose renderers had been made anew was
    // not hidden, and four eyepieces were seen.
    private static void HideDevice(Camera camera)
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
                FromCamera = FromCamera(r.transform, camera.transform.position),
            });
        }
        HideTwins();
    }

    // Another copy of the same model on the player, outside the device's own slot: left to itself it was seen beside the device
    // drawn here, four eyepieces. Looked for twice a second, not every frame: the player has many renderers.
    private const int TwinSearchFrames = 30;
    private static readonly List<MeshRenderer> _bodyRenderers = new List<MeshRenderer>();
    private static readonly List<Renderer> _twins = new List<Renderer>();
    private static readonly HashSet<string> _deviceNames = new HashSet<string>();
    private static int _twinFrame = -1000;

    private static void HideTwins()
    {
        if (Time.frameCount - _twinFrame >= TwinSearchFrames)
        {
            _twinFrame = Time.frameCount;
            _twins.Clear();
            _deviceNames.Clear();
            foreach (Renderer r in _slotRenderers)
                _deviceNames.Add(r.name);
            Transform slot = DeviceSlot();
            slot.root.GetComponentsInChildren(false, _bodyRenderers);
            foreach (MeshRenderer r in _bodyRenderers)
            {
                if (_deviceNames.Contains(r.name) && !r.transform.IsChildOf(slot))
                    _twins.Add(r);
            }
        }
        foreach (Renderer r in _twins)
        {
            if (r == null)
                continue;
            _hidden.Add((r, r.shadowCastingMode));
            r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
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

    private static readonly int DirectNowVP = Shader.PropertyToID("_DirectNowVP"), DirectOldVP = Shader.PropertyToID("_DirectOldVP"), DirectOldM = Shader.PropertyToID("_DirectOldM"), DirectHalf = Shader.PropertyToID("_DirectHalf");
    private static readonly Dictionary<Renderer, Matrix4x4> _oldPieces = new Dictionary<Renderer, Matrix4x4>();
    private static readonly Vector3[] _oldShifts = new Vector3[2];
    private static Matrix4x4 _oldVP;
    private static int _motionFrame = -100;

    private static void DrawMotion(CommandBuffer buffer, Camera main, Matrix4x4 turned, Matrix4x4 projection, Vector3 leftShift, Vector3 rightShift)
    {
        Matrix4x4 nowVP = main.nonJitteredProjectionMatrix * turned;
        // After a gap there is no last frame to go by: as if it had not moved.
        bool continued = _motionFrame == Time.frameCount - 1;
        _motionFrame = Time.frameCount;
        if (!continued)
        {
            _oldPieces.Clear();
            _oldVP = nowVP;
            _oldShifts[0] = leftShift;
            _oldShifts[1] = rightShift;
        }
        buffer.SetViewProjectionMatrices(turned, projection);
        buffer.SetGlobalMatrix(DirectNowVP, nowVP);
        buffer.SetGlobalMatrix(DirectOldVP, _oldVP);
        buffer.SetRenderTarget(BuiltinRenderTextureType.MotionVectors);
        for (int half = 0; half < 2; half++)
        {
            Matrix4x4 fromEye = Matrix4x4.Translate(-(half == 0 ? leftShift : rightShift)), fromOldEye = Matrix4x4.Translate(-_oldShifts[half]);
            buffer.SetGlobalFloat(DirectHalf, half);
            foreach (Piece piece in _pieces)
            {
                Matrix4x4 old = _oldPieces.TryGetValue(piece.Renderer, out Matrix4x4 was) ? was : piece.FromCamera;
                buffer.SetGlobalMatrix(DirectOldM, fromOldEye * old);
                for (int s = 0; s < piece.Mesh.subMeshCount; s++)
                    buffer.DrawMesh(piece.Mesh, fromEye * piece.FromCamera, _directMaterial!, s, MotionPass);
            }
        }
        buffer.SetViewProjectionMatrices(main.worldToCameraMatrix, main.projectionMatrix);
        buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        foreach (Piece piece in _pieces)
            _oldPieces[piece.Renderer] = piece.FromCamera;
        _oldVP = nowVP;
        _oldShifts[0] = leftShift;
        _oldShifts[1] = rightShift;
    }

    // Unity's deferred lights go by the stencil: its low bits say which lamps leave a thing out, by its layer. All of it written as one
    // value, the device had every lamp in the map on it, and glinted as nothing else did. So the value the game itself leaves under
    // the device is read while the game draws it (device up), once for each device, and written where the device is put in.
    private const int StencilRetryFrames = 60;
    private static readonly int DirectStencilRefId = Shader.PropertyToID("_DirectStencilRef");
    private static CommandBuffer? _stencilProbe;
    private static RenderTexture? _stencilSeen;
    private static Camera? _stencilCamera;
    private static Transform? _stencilOf;
    private static int _stencilAt = -1000;
    private static bool _stencilWaiting;
    private static float _stencilRef = 128f;

    private static void ProbeStencil(Camera main)
    {
        if (_body == null || _directShader == null || ReferenceEquals(_stencilOf, _body))
            return;
        if (_stencilWaiting)
        {
            _stencilWaiting = false;
            _stencilCamera?.RemoveCommandBuffer(CameraEvent.AfterGBuffer, _stencilProbe);
            ReadStencil();
            return;
        }
        if (Time.frameCount - _stencilAt < StencilRetryFrames)
            return;
        _stencilAt = Time.frameCount;
        int width = main.pixelWidth, height = main.pixelHeight;
        if (_stencilSeen == null || _stencilSeen.width != width || _stencilSeen.height != height)
        {
            if (_stencilSeen != null)
            {
                _stencilSeen.Release();
                Object.Destroy(_stencilSeen);
            }
            _stencilSeen = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { name = "Justachae NVG stencil seen", filterMode = FilterMode.Point };
        }
        _directMaterial ??= new Material(_directShader) { name = "Justachae NVG device in the main camera", hideFlags = HideFlags.DontSave };
        _stencilProbe ??= new CommandBuffer { name = "Justachae NVG stencil of the device" };
        _stencilProbe.Clear();
        _stencilProbe.SetRenderTarget(_stencilSeen, BuiltinRenderTextureType.CameraTarget);
        _stencilProbe.ClearRenderTarget(false, true, Color.clear);
        DeviceSlot().GetComponentsInChildren(false, _slotRenderers);
        for (int bit = 0; bit < 8; bit++)
        {
            foreach (Renderer r in _slotRenderers)
            {
                if (r is not MeshRenderer || !r.enabled || !Opaque(r) || !FinestLevel(r) || !r.TryGetComponent(out MeshFilter filter) || filter.sharedMesh == null)
                    continue;
                for (int s = 0; s < filter.sharedMesh.subMeshCount; s++)
                    _stencilProbe.DrawRenderer(r, _directMaterial, s, StencilBitsPass + bit);
            }
        }
        _stencilProbe.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        main.AddCommandBuffer(CameraEvent.AfterGBuffer, _stencilProbe);
        _stencilCamera = main;
        _stencilWaiting = true;
    }

    private static void ReadStencil()
    {
        RenderTexture? active = RenderTexture.active;
        var read = new Texture2D(_stencilSeen!.width, _stencilSeen.height, TextureFormat.RGBA32, false, true);
        RenderTexture.active = _stencilSeen;
        read.ReadPixels(new Rect(0, 0, _stencilSeen.width, _stencilSeen.height), 0, 0);
        RenderTexture.active = active;
        var counts = new int[256];
        foreach (Color32 c in read.GetPixels32())
            counts[c.r]++;
        Object.Destroy(read);
        int best = 0;
        for (int v = 1; v < 256; v++)
        {
            if (counts[v] > counts[best] || best == 0)
                best = v;
        }
        // Not in view this time: looked for again.
        if (counts[best] < 16)
            return;
        _stencilOf = _body;
        _stencilRef = best;
    }

    private static void ClearDirect()
    {
        _directBuffer?.Clear();
        _directAgain?.Clear();
        _directEnd?.Clear();
        _directMotion?.Clear();
    }

    private static readonly int DirectMonocularId = Shader.PropertyToID("_DirectMonocular");

    private static void DrawDirect(Camera main, Vector3 leftShift, Vector3 rightShift, float split, Vector4 monocular)
    {
        _directBuffer ??= new CommandBuffer { name = "Justachae NVG device in the main camera" };
        _directAgain ??= new CommandBuffer { name = "Justachae NVG device after the decals" };
        _directEnd ??= new CommandBuffer { name = "Justachae NVG device's fade evened" };
        _directMotion ??= new CommandBuffer { name = "Justachae NVG device's motion" };
        CommandBuffer buffer = _directBuffer;
        buffer.Clear();
        _directAgain.Clear();
        _directEnd.Clear();
        _directMotion.Clear();
        if (_directShader == null)
            return;
        if (_directCamera != main)
        {
            if (_directCamera != null)
            {
                _directCamera.RemoveCommandBuffer(CameraEvent.AfterGBuffer, buffer);
                _directCamera.RemoveCommandBuffer(CameraEvent.BeforeLighting, _directAgain);
                _directCamera.RemoveCommandBuffer(CameraEvent.AfterImageEffectsOpaque, _directEnd);
                _directCamera.RemoveCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, _directMotion);
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
            // Ahead of what reads the motion.
            main.RemoveCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, _directMotion);
            CommandBuffer[] opaqueEffects = main.GetCommandBuffers(CameraEvent.BeforeImageEffectsOpaque);
            main.RemoveCommandBuffers(CameraEvent.BeforeImageEffectsOpaque);
            main.AddCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, _directMotion);
            foreach (CommandBuffer b in opaqueEffects)
                main.AddCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, b);
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

        for (int half = 0; half < 2; half++)
        {
            int[] ids = half == 0 ? DirectLeft : DirectRight;
            int depth = half == 0 ? DirectLeftDepth : DirectRightDepth;
            Vector3 shift = half == 0 ? leftShift : rightShift;
            for (int t = 0; t < 4; t++)
                _directTargets[half][t] = DirectTexture(_directTextures[half], t, width, height, 0, DirectFormats[t], t < 2 ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear, "Justachae NVG device " + half + " " + t);
            RenderTexture depthTexture = DirectTexture(_directDepths, half, width, height, 32, RenderTextureFormat.Depth, RenderTextureReadWrite.Linear, "Justachae NVG device depth " + half);
            buffer.SetRenderTarget(_directTargets[half], depthTexture);
            buffer.ClearRenderTarget(true, true, Color.clear);
            // From where this eye truly stands: the view direction a material works out comes from the camera's place, which the
            // view matrix alone leaves where the main camera is. (Far out in a map a place in the world is kept only to some tens
            // of micrometres: the device is drawn with the eye at the origin, by its true offsets, or it shook.)
            Matrix4x4 fromEye = Matrix4x4.Translate(-shift);
            buffer.SetViewProjectionMatrices(turned, projection);
            buffer.SetGlobalVector(CameraPlaceId, new Vector4(0f, 0f, 0f, 1f));
            foreach (Piece piece in _pieces)
            {
                piece.Renderer.GetPropertyBlock(_pieceBlock);
                int count = Mathf.Min(piece.Materials.Length, piece.Mesh.subMeshCount);
                for (int s = 0; s < count; s++)
                {
                    if (piece.Passes[s] < 0)
                        continue;
                    buffer.DrawMesh(piece.Mesh, fromEye * piece.FromCamera, piece.Materials[s], s, piece.Passes[s], _pieceBlock);
                }
            }
            for (int t = 0; t < 4; t++)
                buffer.SetGlobalTexture(ids[t], _directTextures[half][t]!);
            buffer.SetGlobalTexture(depth, depthTexture);
        }

        // The camera's place given back before anything else is drawn.
        buffer.SetGlobalVector(CameraPlaceId, new Vector4(place.x, place.y, place.z, 1f));

        buffer.SetGlobalFloat(DirectSplit, split);
        buffer.SetGlobalVector(DirectMonocularId, monocular);
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

        // The sealed eyecups' insides go dark as the device comes down: from where each half of the device stands, not from the
        // main camera's depth, which holds what is nearest at its near plane.
        Vector3 leftOf = leftShift, rightOf = rightShift;
        // The eyecup on the left of the frame is the left eye's half's, whichever of the device's it is.
        Transform lensA = _lensNodes[CentreLensA], lensB = _lensNodes[Mathf.Min(CentreLensB, _lensNodes.Length - 1)];
        bool aIsLeft = Vector3.Dot(FromCamera(lensA, place).GetColumn(3), main.transform.right) <= Vector3.Dot(FromCamera(lensB, place).GetColumn(3), main.transform.right);
        Transform lensLeft = aIsLeft ? lensA : lensB;
        Shader.SetGlobalFloat(ShadeLeftCupId, aIsLeft ? 0f : 1f);
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
                buffer.SetGlobalFloat(DirectHalf, half);
                foreach (Piece piece in _pieces)
                {
                    for (int s = 0; s < piece.Mesh.subMeshCount; s++)
                        buffer.DrawMesh(piece.Mesh, fromEye * piece.FromCamera, _directMaterial, s, PlacesPass);
                }
            }
        }

        _directMaterial.SetFloat(DirectStencilRefId, _stencilRef);
        buffer.SetRenderTarget(_mainGBuffer, BuiltinRenderTextureType.CameraTarget);
        buffer.SetViewProjectionMatrices(main.worldToCameraMatrix, main.projectionMatrix);
        buffer.DrawMesh(_directQuad, Matrix4x4.identity, _directMaterial, 0, InsertPass);

        // After the decals, which are thrown on whatever the depth holds.
        buffer = _directAgain;
        buffer.SetRenderTarget(_mainSurface, BuiltinRenderTextureType.CameraTarget);
        buffer.DrawMesh(_directQuad, Matrix4x4.identity, _directMaterial, 0, SurfacePass);
        buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        // The device's own motion on the screen: it goes with the head, and left to itself it had what lay behind it, so what is
        // carried from frame to frame by the motion (the reflections, the upscaler) drifted over it like mist.
        if ((main.depthTextureMode & DepthTextureMode.MotionVectors) != 0)
            DrawMotion(_directMotion, main, turned, projection, leftShift, rightShift);
        // Once all is lit: the fade.
        buffer = _directEnd;
        buffer.SetGlobalVector(DirectTexel, new Vector4(1f / Mathf.Max(width, 1), 1f / Mathf.Max(height, 1), 0f, 0f));
        buffer.GetTemporaryRT(DirectLitNow, width, height, 0, FilterMode.Point, RenderTextureFormat.DefaultHDR);
        buffer.Blit(BuiltinRenderTextureType.CameraTarget, DirectLitNow);
        buffer.SetGlobalTexture(DirectLitNow, DirectLitNow);
        buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        buffer.DrawMesh(_directQuad, Matrix4x4.identity, _directMaterial, 0, EvenPass);
        buffer.ReleaseTemporaryRT(DirectLitNow);
    }
}
