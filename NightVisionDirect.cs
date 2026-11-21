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
    private static readonly int[] DirectGlass = { Shader.PropertyToID("_DirectGlass0"), Shader.PropertyToID("_DirectGlass1") };
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
    private static readonly Dictionary<Renderer, PieceSource> _pieceSources = new Dictionary<Renderer, PieceSource>();
    // A skinned part of the device (the F-PANO's power cable, K-FPANO 0.1.6: plugged into the helmet's rail, bending as the
    // goggle flips): baked each frame into a mesh of its own, kept, and drawn as the other pieces are. (Hidden with the
    // rest and not drawn, it would have been gone while the eyepieces show.)
    private static readonly Dictionary<SkinnedMeshRenderer, Mesh> _bakedPieces = new Dictionary<SkinnedMeshRenderer, Mesh>();
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
            if (!r.enabled || !Opaque(r))
                continue;
            Mesh? drawn = null;
            PieceSource source;
            if (r is SkinnedMeshRenderer skin)
            {
                if (skin.sharedMesh == null)
                    continue;
                source = SourceOf(r);
                if (!source.Finest || !source.Any)
                    continue;
                if (!_bakedPieces.TryGetValue(skin, out drawn) || drawn == null)
                {
                    if (_bakedPieces.Count > 16)
                    {
                        foreach (Mesh old in _bakedPieces.Values)
                        {
                            if (old != null)
                                Object.Destroy(old);
                        }
                        _bakedPieces.Clear();
                    }
                    drawn = new Mesh { name = "Justachae NVG piece " + skin.name, hideFlags = HideFlags.DontSave };
                    drawn.MarkDynamic();
                    _bakedPieces[skin] = drawn;
                }
                // (With its scale left to its transform, as FromCamera has it.)
                skin.BakeMesh(drawn, true);
            }
            else if (r is MeshRenderer)
            {
                source = SourceOf(r);
                if (!source.Finest || !source.Any || source.Filter == null || source.Filter.sharedMesh == null)
                    continue;
                drawn = source.Filter.sharedMesh;
            }
            else
                continue;
            _pieces.Add(new Piece
            {
                Renderer = r, Mesh = drawn, Materials = source.Materials, Passes = source.Passes,
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

    private static PieceSource SourceOf(Renderer r)
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

    private static readonly int DirectNowVP = Shader.PropertyToID("_DirectNowVP"), DirectOldVP = Shader.PropertyToID("_DirectOldVP"), DirectOldM = Shader.PropertyToID("_DirectOldM"), DirectOldMSame = Shader.PropertyToID("_DirectOldMSame"), DirectHalf = Shader.PropertyToID("_DirectHalf");
    private static readonly Dictionary<Renderer, Matrix4x4> _oldPieces = new Dictionary<Renderer, Matrix4x4>();
    private static readonly Vector3[] _oldShifts = new Vector3[2];
    // From where each part was drawn last frame, in each half (PieceShift: it changes as a pod swings and as the aim comes
    // up; worked out again from this frame's swing, the motion written was wrong while either changed, and the frame's
    // smoothing drew stripes across the device: the user's video, 2026-11-18).
    private static readonly Dictionary<Renderer, Vector3>[] _oldPieceShifts = { new Dictionary<Renderer, Vector3>(), new Dictionary<Renderer, Vector3>() };
    private static Matrix4x4 _oldVP;
    private static int _motionFrame = -100;

    // The goggle eye's gun's motion (both guns seen; NvgDirect.shader, pass 17), while it is seen more than through: from
    // its pieces as drawn this frame (DrawGun) against where they stood last frame.
    private static readonly Dictionary<Renderer, Matrix4x4> _oldGunPieces = new Dictionary<Renderer, Matrix4x4>();
    private static Matrix4x4 _gunOldVP;
    private static int _gunMotionFrame = -100;
    private const int GunMotionPass = 17, BareMotionPass = 18, BareMotionFromNextPass = 21;
    private static readonly int GunMotionCopyId = Shader.PropertyToID("_GunMotionCopy");
    private static Vector3 _bareShiftNow, _bareShiftOld;
    private static readonly RenderTexture?[] _bareMotion = new RenderTexture?[1];
    private static int _bareMotionFrame = -100;
    // The bare eye's gun's motion this frame (uv), or null (TwoEyes gathers its picture over frames by it).
    internal static RenderTexture? BareMotion => _bareMotionFrame >= Time.frameCount - 1 ? _bareMotion[0] : null;

    private static void DrawGunMotion(CommandBuffer buffer, Camera main, Matrix4x4 turned, Matrix4x4 projection, bool gun, bool motionVectors)
    {
        if (!gun || _gunDraws.Count == 0 || _directMaterial == null)
        {
            _gunMotionFrame = -100;
            return;
        }
        Matrix4x4 nowVP = main.nonJitteredProjectionMatrix * turned;
        bool continued = _gunMotionFrame == Time.frameCount - 1;
        _gunMotionFrame = Time.frameCount;
        if (!continued)
        {
            _oldGunPieces.Clear();
            _gunOldVP = nowVP;
            _bareShiftOld = _bareShiftNow;
        }
        // The bare eye's gun's motion, into its own target (by the projection it was drawn with).
        if (_gunDepths[0] is RenderTexture bareDepth)
        {
            RenderTexture motion = DirectTexture(_bareMotion, 0, bareDepth.width, bareDepth.height, 0, RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear, "Justachae NVG bare eye's gun motion");
            buffer.SetRenderTarget(motion);
            buffer.ClearRenderTarget(false, true, Color.clear);
            buffer.SetViewProjectionMatrices(turned, BareGunProjection);
            buffer.SetGlobalMatrix(DirectNowVP, nowVP);
            buffer.SetGlobalMatrix(DirectOldVP, _gunOldVP);
            Matrix4x4 move = Matrix4x4.Translate(-_bareShiftNow), moveOld = Matrix4x4.Translate(-_bareShiftOld);
            foreach ((Mesh mesh, Matrix4x4 matrix, Material material, int sub, int pass, Renderer renderer) in _gunDraws)
            {
                if (mesh == null || renderer == null)
                    continue;
                buffer.SetGlobalMatrix(DirectOldM, moveOld * (_oldGunPieces.TryGetValue(renderer, out Matrix4x4 was) ? was : matrix));
                buffer.DrawMesh(mesh, move * matrix, _directMaterial, sub, BareMotionPass);
            }
            buffer.SetViewProjectionMatrices(main.worldToCameraMatrix, main.projectionMatrix);
            buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            _bareMotionFrame = Time.frameCount;
        }
        if (motionVectors)
        {
            Matrix4x4 goggleNow = Matrix4x4.Translate(-_goggleShiftNow), goggleOld = Matrix4x4.Translate(-(continued ? _goggleShiftOld : _goggleShiftNow));
            buffer.SetViewProjectionMatrices(turned, projection);
            buffer.SetGlobalMatrix(DirectNowVP, nowVP);
            buffer.SetGlobalMatrix(DirectOldVP, _gunOldVP);
            buffer.SetRenderTarget(BuiltinRenderTextureType.MotionVectors);
            foreach ((Mesh mesh, Matrix4x4 matrix, Material material, int sub, int pass, Renderer renderer) in _gunDraws)
            {
                if (mesh == null || renderer == null)
                    continue;
                buffer.SetGlobalMatrix(DirectOldM, goggleOld * (_oldGunPieces.TryGetValue(renderer, out Matrix4x4 was) ? was : matrix));
                buffer.DrawMesh(mesh, goggleNow * matrix, _directMaterial, sub, GunMotionPass);
            }
            buffer.SetViewProjectionMatrices(main.worldToCameraMatrix, main.projectionMatrix);
            buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        }
        // The pixels given the bare eye's gun: the frame keeps there what the goggle eye sees (made from the pixels next to
        // them, TwoEyes.FillApart), but the camera's motion was worked out from that gun's depth, near: as the camera
        // moved (leaning most of all) the frame's smoothing over frames looked for those pixels in the wrong place, every
        // other pixel, and the picture under the gun, seen through it, came out mottled (the user's video, 2026-11-18). Given
        // the motion of the pixel next to them (the other kind of the chessboard, never the bare eye's gun).
        if (motionVectors)
        {
            buffer.GetTemporaryRT(GunMotionCopyId, -1, -1, 0, FilterMode.Point, RenderTextureFormat.RGHalf);
            buffer.Blit(BuiltinRenderTextureType.MotionVectors, GunMotionCopyId);
            buffer.SetRenderTarget(BuiltinRenderTextureType.MotionVectors);
            buffer.DrawMesh(_directQuad, Matrix4x4.identity, _directMaterial, 0, BareMotionFromNextPass);
            buffer.ReleaseTemporaryRT(GunMotionCopyId);
            buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        }
        foreach ((Mesh mesh, Matrix4x4 matrix, Material material, int sub, int pass, Renderer renderer) in _gunDraws)
        {
            if (renderer != null)
                _oldGunPieces[renderer] = matrix;
        }
        _gunOldVP = nowVP;
        _bareShiftOld = _bareShiftNow;
        _goggleShiftOld = _goggleShiftNow;
    }

    private static void DrawMotion(CommandBuffer buffer, Camera main, Matrix4x4 turned, Matrix4x4 projection, Vector3 leftShift, Vector3 rightShift)
    {
        Matrix4x4 nowVP = main.nonJitteredProjectionMatrix * turned;
        // After a gap there is no last frame to go by: as if it had not moved.
        bool continued = _motionFrame == Time.frameCount - 1;
        _motionFrame = Time.frameCount;
        if (!continued)
        {
            _oldPieces.Clear();
            _oldPieceShifts[0].Clear();
            _oldPieceShifts[1].Clear();
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
            buffer.SetGlobalFloat(DirectHalf, half);
            foreach (Piece piece in _pieces)
            {
                // (Each part from where it is drawn, PieceShift, and from where it was last frame.)
                Vector3 shiftNow = PieceShift(piece.Renderer.transform, half, leftShift, rightShift);
                Matrix4x4 fromEye = Matrix4x4.Translate(-shiftNow),
                    fromOldEye = Matrix4x4.Translate(-(_oldPieceShifts[half].TryGetValue(piece.Renderer, out Vector3 shiftWas) ? shiftWas : shiftNow));
                _oldPieceShifts[half][piece.Renderer] = shiftNow;
                Matrix4x4 old = _oldPieces.TryGetValue(piece.Renderer, out Matrix4x4 was) ? was : piece.FromCamera;
                buffer.SetGlobalMatrix(DirectOldM, fromOldEye * old);
                buffer.SetGlobalMatrix(DirectOldMSame, fromEye * old);
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
            // (Not "?.": a camera destroyed since, the hideout's on entering a raid, is not null to it, and the call threw.)
            if (_stencilCamera != null)
                _stencilCamera.RemoveCommandBuffer(CameraEvent.AfterGBuffer, _stencilProbe);
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

    private static readonly int DirectOverId = Shader.PropertyToID("_DirectOver"),
        DirectBareRightId = Shader.PropertyToID("_DirectBareRight");
    private static readonly int DirectSeeThroughId = Shader.PropertyToID("_DirectSeeThrough");
    // How far the device drawn here is seen through, all over (F12 夜视仪透明度): 0 solid. Its pixels kept and left out
    // by the chessboard as a monocular's fade is, what lies behind showing between.
    internal static float DeviceSeeThrough;

    // Each side's eyepiece's pod (and its eyecup's pod node), by name, and how far it has swung away, 0 to 1
    // (NightVisionEyes.RenderLenses).
    private static readonly string?[] _sidePods = new string?[2], _sideCups = new string?[2];
    private static readonly float[] _sideGone = new float[2];
    private const float AgreeBy = 3f;   // the halves agree once a pod is a third of the way gone

    // Which side's pod a part of the device is on: 0 the left's, 1 the right's, -1 neither (the body, the mount).
    private static int PodSideOf(Transform t)
    {
        if (_sidePods[0] == null && _sidePods[1] == null)
            return -1;
        for (Transform? at = t; at != null && at != _body; at = at.parent)
        {
            string name = at.name;
            for (int side = 0; side < 2; side++)
            {
                if (name == _sidePods[side] || _sideCups[side] != null && name == _sideCups[side])
                    return side;
            }
        }
        return -1;
    }

    // From where a part of the device is drawn in a half of the frame (0 the left eye's, 1 the right's). With both
    // eyepieces down, the half's own eye. With one swung away, the same in both halves, so they agree where they meet:
    // the body from the side that has swung (the camera's own view), an eyepiece's own parts from its own side (the one
    // still down from its eye, brought in; the one swung up from the camera). Eased by the swing.
    private static Vector3 PieceShift(Transform part, int half, Vector3 leftShift, Vector3 rightShift)
    {
        Vector3 own = half == 0 ? leftShift : rightShift, other = half == 0 ? rightShift : leftShift;
        int side = PodSideOf(part);
        if (side == half)
            return own;
        // Over the first third of the swing: while the halves still differ, what crosses the line between them is drawn
        // twice over, a little apart (stripes across the pod as it swung, the user's video, 2026-11-18).
        // (The body: toward the other side as that side's eyepiece goes, but not once this side's own has gone too: both
        // are the camera's then, and going over to the other and back the body slid as the second eyepiece swung.)
        float toOther = side < 0
            ? Mathf.SmoothStep(0f, 1f, _sideGone[1 - half] * AgreeBy) * (1f - _sideGone[half])
            : Mathf.SmoothStep(0f, 1f, Mathf.Max(_sideGone[0], _sideGone[1]) * AgreeBy);
        return toOther <= 0f ? own : Vector3.Lerp(own, other, toOther);
    }

    private static void DrawDirect(Camera main, Vector3 leftShift, Vector3 rightShift, float split)
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
            buffer.SetViewProjectionMatrices(turned, projection);
            buffer.SetGlobalVector(CameraPlaceId, new Vector4(0f, 0f, 0f, 1f));
            foreach (Piece piece in _pieces)
            {
                Matrix4x4 fromEye = Matrix4x4.Translate(-PieceShift(piece.Renderer.transform, half, leftShift, rightShift));
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
        buffer.SetGlobalFloat(DirectSeeThroughId, Mathf.Clamp01(DeviceSeeThrough));
        // The strip the halves are eased into each other over closes as they come to agree (each pixel of it is one
        // half's or the other's, and what differs between them shows there as stripes while it moves).
        buffer.SetGlobalFloat(DirectStrip, Mathf.Max(Overscan * (1f - Mathf.Clamp01(Mathf.Max(_sideGone[0], _sideGone[1]) * AgreeBy)), 0.0005f));
        // (TwoEyes.Device: how far the two eyes' views are laid one over the other, how much of each is seen, and how much
        // of the bare eye's stands within the goggle eye's eyepiece.)
        buffer.SetGlobalVector(DirectOverId, new Vector4(_blend.Mix, _blend.GoggleShare, _blend.BareShare, _blend.BareBeforePicture));
        buffer.SetGlobalFloat(DirectBareRightId, _goggleEyeRight ? 0f : 1f);
        buffer.SetGlobalVector(DirectInset, new Vector4(DirectInsetPixels / Mathf.Max(Screen.width, 1), DirectInsetPixels / Mathf.Max(Screen.height, 1), 0f, 0f));
        for (int side = 0; side < 2; side++)
        {
            if (_lensMaps[side] != null)
                buffer.SetGlobalTexture(DirectLens[side], _lensMaps[side]);
            buffer.SetGlobalTexture(DirectGlass[side], _glassMaps[side] != null ? _glassMaps[side]! : (Texture)Texture2D.blackTexture);
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
        // The bare eye's gun (TwoEyes.cs), drawn from that eye into G-buffers of its own like the device.
        bool gun = DrawGun(buffer, main, place, turned, projection, width, height);
        buffer.SetRenderTarget(_mainGBuffer, BuiltinRenderTextureType.CameraTarget);
        buffer.SetViewProjectionMatrices(main.worldToCameraMatrix, main.projectionMatrix);
        buffer.DrawMesh(_directQuad, Matrix4x4.identity, _directMaterial, 0, InsertPass);
        if (gun)
        {
            buffer.DrawMesh(_directQuad, Matrix4x4.identity, _directMaterial, 0, GunInsertPass);
            // Which eye's gun each pixel was given (TwoEyes.cs takes the two apart by it).
            RenderTexture which = DirectTexture(_gunWhich, 0, width, height, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear, "Justachae NVG which eye's gun");
            which.filterMode = FilterMode.Point;
            buffer.SetRenderTarget(which);
            buffer.ClearRenderTarget(false, true, Color.clear);
            buffer.DrawMesh(_directQuad, Matrix4x4.identity, _directMaterial, 0, GunWhichPass);
            buffer.SetGlobalTexture(GunWhichId, which);
            buffer.SetRenderTarget(_mainGBuffer, BuiltinRenderTextureType.CameraTarget);
        }

        // After the decals, which are thrown on whatever the depth holds.
        buffer = _directAgain;
        buffer.SetRenderTarget(_mainSurface, BuiltinRenderTextureType.CameraTarget);
        buffer.DrawMesh(_directQuad, Matrix4x4.identity, _directMaterial, 0, SurfacePass);
        if (gun)
            buffer.DrawMesh(_directQuad, Matrix4x4.identity, _directMaterial, 0, GunSurfacePass);
        buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        // The device's own motion on the screen: it goes with the head, and left to itself it had what lay behind it, so what is
        // carried from frame to frame by the motion (the reflections, the upscaler) drifted over it like mist.
        bool motionVectors = (main.depthTextureMode & DepthTextureMode.MotionVectors) != 0;
        if (motionVectors)
            DrawMotion(_directMotion, main, turned, projection, leftShift, rightShift);
        // (The bare eye's gun's own motion whether or not the camera keeps motion vectors.)
        DrawGunMotion(_directMotion, main, turned, projection, gun, motionVectors);
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

    // The bare eye's gun (and its hands and arms), drawn from that eye into G-buffers of its own, as the device is; put into
    // the main camera's after the device (passes 13, 14). Skinned ones baked each frame into meshes of their own, kept.
    private const int GunInsertPass = 13, GunSurfacePass = 14, GunLensPass = 15, GunWhichPass = 16;
    private static readonly RenderTexture?[] _gunWhich = new RenderTexture?[1];
    private static readonly int GunWhichId = Shader.PropertyToID("_GunWhich"), DirectBareAimsId = Shader.PropertyToID("_DirectBareAims");
    private static readonly RenderTexture?[] _gunLensMask = new RenderTexture?[1];
    private static readonly int DirectGunLensId = Shader.PropertyToID("_DirectGunLens"), DirectGunTexelId = Shader.PropertyToID("_DirectGunTexel");
    private static readonly int[] DirectGun = { Shader.PropertyToID("_DirectG0"), Shader.PropertyToID("_DirectG1"), Shader.PropertyToID("_DirectG2"), Shader.PropertyToID("_DirectG3") };
    private static readonly int DirectGunDepth = Shader.PropertyToID("_DirectGD"), DirectGunShownId = Shader.PropertyToID("_DirectGunShown");
    private static readonly RenderTexture?[] _gunTextures = new RenderTexture?[4];
    // Both guns seen (TwoEyes.BothGuns): the goggle eye's drawn too, the frame's own kept to its shadow.
    private static readonly RenderTexture?[] _goggleGunTextures = new RenderTexture?[4], _goggleGunDepths = new RenderTexture?[1];
    private static readonly RenderTargetIdentifier[] _goggleGunTargets = new RenderTargetIdentifier[4];
    private static readonly int[] DirectGoggleGun = { Shader.PropertyToID("_DirectH0"), Shader.PropertyToID("_DirectH1"), Shader.PropertyToID("_DirectH2"), Shader.PropertyToID("_DirectH3") };
    private static readonly int DirectGoggleGunDepth = Shader.PropertyToID("_DirectHD"), DirectGoggleShownId = Shader.PropertyToID("_DirectGoggleShown");
    private static readonly List<(Mesh mesh, Matrix4x4 matrix, Material material, int sub, int pass, Renderer renderer)> _gunDraws = new List<(Mesh, Matrix4x4, Material, int, int, Renderer)>();
    private static readonly HashSet<Material> _gunGlass = new HashSet<Material>();
    // Whether the gun drawn this frame has a sight's glass among its forward parts, and that glass drawn solid from the
    // bare eye (TwoEyes.DrawWindowMask: where that eye looks through a window).
    internal static bool GunHasGlass
    {
        get
        {
            foreach ((Mesh mesh, Matrix4x4 matrix, Material material, int sub, int pass, Renderer renderer) in _gunForward)
            {
                if (material != null && _gunGlass.Contains(material))
                    return true;
            }
            return false;
        }
    }

    internal static bool DrawGunGlass(CommandBuffer buffer, Material solid, int solidPass)
    {
        bool any = false;
        foreach ((Mesh mesh, Matrix4x4 matrix, Material material, int sub, int pass, Renderer renderer) in _gunForward)
        {
            if (mesh == null || material == null || renderer == null || !_gunGlass.Contains(material))
                continue;
            buffer.DrawMesh(mesh, _forwardFromBare * matrix, solid, sub, solidPass);
            any = true;
        }
        return any;
    }
    private static Matrix4x4 _forwardFromBare = Matrix4x4.identity, _forwardFromGoggle = Matrix4x4.identity;
    private static Vector3 _goggleShiftNow, _goggleShiftOld;
    private static readonly RenderTexture?[] _gunDepths = new RenderTexture?[1];
    // The bare eye's gun's own depth (its lasers drawn against it, NightVisionIr.cs).
    internal static RenderTexture? GunDepth => _gunDepths[0];
    // The projection the bare eye's gun was drawn with this frame (unjittered, the device's near and far planes).
    internal static Matrix4x4 BareGunProjection = Matrix4x4.identity;
    private static readonly RenderTargetIdentifier[] _gunTargets = new RenderTargetIdentifier[4];
    private static readonly Dictionary<SkinnedMeshRenderer, Mesh> _bakedGun = new Dictionary<SkinnedMeshRenderer, Mesh>();
    private static readonly Dictionary<Renderer, (Material[] materials, int[] passes, int[] forward)> _gunSources = new Dictionary<Renderer, (Material[], int[], int[])>();
    private static readonly List<(Mesh mesh, Matrix4x4 matrix, Material material, int sub, int pass, Renderer renderer)> _gunForward = new List<(Mesh, Matrix4x4, Material, int, int, Renderer)>();
    private static CommandBuffer? _gunForwardBuffer;
    // At each frame's end (TwoEyes.EndOfFrame): filled again by the next DrawGun, or left empty when the gun is no longer
    // drawn (the goggle up): otherwise it kept drawing the last frame's parts, where they were.
    internal static void EmptyGunForward() => _gunForwardBuffer?.Clear();

    // After the two eyes' guns are taken apart (TwoEyes.FillApart, the same camera event): put on again, behind it.
    internal static void GunForwardAfter(Camera main)
    {
        if (_gunForwardBuffer == null || _gunForwardCamera != main)
            return;
        main.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _gunForwardBuffer);
        main.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _gunForwardBuffer);
    }
    private static Camera? _gunForwardCamera;

    // A see-through material's forward pass (ForwardPass gives none to a material that has a G-buffer pass too).
    private static int ForwardPassOf(Material? material)
    {
        if (material == null)
            return -1;
        for (int i = 0; i < material.passCount; i++)
        {
            string mode = material.shader.FindPassTagValue(i, new ShaderTagId("LightMode")).name ?? "";
            if (mode.Length == 0 || string.Equals(mode, "ForwardBase", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "Always", System.StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "SRPDefaultUnlit", System.StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    // A material's forward pass, for a part with none for the G-buffer.
    private static int ForwardPass(Material? material)
    {
        if (material == null || GBufferPass(material) >= 0)
            return -1;
        for (int i = 0; i < material.passCount; i++)
        {
            string mode = material.shader.FindPassTagValue(i, new ShaderTagId("LightMode")).name ?? "";
            if (mode.Length == 0 || string.Equals(mode, "ForwardBase", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "Always", System.StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "SRPDefaultUnlit", System.StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    // The gun's forward-only parts, after the scene's see-through things, from the bare eye (as the camera sees them
    // from there: the same matrices as its G-buffer parts, the camera at the origin).
    private static void DrawGunForward(Camera main, Matrix4x4 turned, Vector3 place, Matrix4x4 gunProjection)
    {
        _gunForwardBuffer ??= new CommandBuffer { name = "Justachae NVG gun's forward parts" };
        CommandBuffer buffer = _gunForwardBuffer;
        buffer.Clear();
        if (_gunForwardCamera != main)
        {
            if (_gunForwardCamera != null)   // (not "?.": a destroyed camera)
                _gunForwardCamera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, buffer);
            main.AddCommandBuffer(CameraEvent.AfterForwardAlpha, buffer);
            _gunForwardCamera = main;
        }
        if (_gunForward.Count == 0)
            return;
        buffer.SetViewProjectionMatrices(turned, main.projectionMatrix);
        buffer.SetGlobalVector(CameraPlaceId, new Vector4(0f, 0f, 0f, 1f));
        // Each eye's parts with its gun: the bare eye's into its gun's own picture (TwoEyes, laid over after the eyepieces)
        // against its gun's own depth, by the projection it was drawn with (the device's near and far planes), or what
        // stands behind its body showed through it; the camera's gun's into the frame.
        RenderTexture? bareView = TwoEyes.BareViewNow, bareDepth = _gunDepths[0];
        bool apart = bareView != null && bareDepth != null && bareView.width == bareDepth.width && bareView.height == bareDepth.height;
        for (int eye = 0; eye < 2; eye++)
        {
            bool intoBare = apart && eye == 0;
            if (intoBare)
            {
                buffer.SetRenderTarget(bareView!, bareDepth!);
                buffer.SetViewProjectionMatrices(turned, gunProjection);
            }
            else
                buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            // (The goggle eye's gun's forward parts not while that gun is seen through, the bare eye leading: they would
            // be whole.)
            if (eye == 1 && TwoEyes.Lead > 0.5f)
                continue;
            Matrix4x4 eyeMove = eye == 0 ? _forwardFromBare : _forwardFromGoggle;
            foreach ((Mesh mesh, Matrix4x4 matrix, Material material, int sub, int pass, Renderer renderer) in _gunForward)
            {
                if (mesh == null || material == null || renderer == null || intoBare && _gunGlass.Contains(material))
                    continue;
                renderer.GetPropertyBlock(_pieceBlock);
                SetAmbient(_pieceBlock);
                buffer.DrawMesh(mesh, eyeMove * matrix, material, sub, pass, _pieceBlock);
            }
            if (intoBare)
                buffer.SetViewProjectionMatrices(turned, main.projectionMatrix);
        }
        buffer.SetViewProjectionMatrices(main.worldToCameraMatrix, main.projectionMatrix);
        buffer.SetGlobalVector(CameraPlaceId, new Vector4(place.x, place.y, place.z, 1f));
    }
    private static readonly List<Material> _gunMaterialsNow = new List<Material>();

    // The light from all round (the light probes', or the scene's own where it has none) for the guns drawn here: a mesh
    // drawn by a command buffer is given none, as a renderer is, and the gun was lit by the lamps alone, black where no
    // lamp fell on it; given back to the game as the aim ended, it was bright from one frame to the next (the user's
    // video, 2026-11-19; though in the hideout that light is next to none, 0.006: not what made it bright there). One reading for the whole gun, where it is, each frame; as the shaders have it (unity_SHAr...).
    private static readonly int[] AmbientIds = { Shader.PropertyToID("unity_SHAr"), Shader.PropertyToID("unity_SHAg"), Shader.PropertyToID("unity_SHAb"),
        Shader.PropertyToID("unity_SHBr"), Shader.PropertyToID("unity_SHBg"), Shader.PropertyToID("unity_SHBb"), Shader.PropertyToID("unity_SHC") };
    private static readonly Vector4[] _ambient = new Vector4[7];

    private static void GatherAmbient(Camera main, List<Renderer> renderers)
    {
        Renderer? anchor = null;
        foreach (Renderer r in renderers)
        {
            if (r != null && r.enabled && r.gameObject.activeInHierarchy)
            {
                anchor = r;
                break;
            }
        }
        UnityEngine.Rendering.SphericalHarmonicsL2 sh = RenderSettings.ambientProbe;
        bool probed = anchor != null && anchor.lightProbeUsage != UnityEngine.Rendering.LightProbeUsage.Off;
        if (probed)
            LightProbes.GetInterpolatedProbe(main.transform.position + main.transform.forward * 0.3f, anchor, out sh);
        for (int c = 0; c < 3; c++)
        {
            _ambient[c] = new Vector4(sh[c, 3], sh[c, 1], sh[c, 2], sh[c, 0] - sh[c, 6]);
            _ambient[3 + c] = new Vector4(sh[c, 4], sh[c, 5], sh[c, 6] * 3f, sh[c, 7]);
        }
        _ambient[6] = new Vector4(sh[0, 8], sh[1, 8], sh[2, 8], 1f);
    }

    private static void SetAmbient(MaterialPropertyBlock block)
    {
        for (int i = 0; i < 7; i++)
            block.SetVector(AmbientIds[i], _ambient[i]);
    }

    private static bool DrawGun(CommandBuffer buffer, Camera main, Vector3 place, Matrix4x4 turned, Matrix4x4 projection, int width, int height)
    {
        List<Renderer>? renderers = TwoEyes.GunHere(main.transform.rotation, out Vector3 shift, out Vector3 goggleShift, out float shown);
        buffer.SetGlobalFloat(DirectGunShownId, 0f);
        buffer.SetGlobalFloat(DirectGoggleShownId, 0f);
        _gunForward.Clear();
        if (renderers == null || shown <= 0f || _directMaterial == null)
        {
            DrawGunForward(main, turned, place, projection);
            return false;
        }
        // Gathered once (skinned parts baked once), drawn from each eye: the bare eye's (moved by the opposite of its
        // offset) and, both guns seen, the goggle eye's (the camera's own place), each into G-buffers of its own.
        _gunDraws.Clear();
        Matrix4x4 jittered = main.projectionMatrix, steady = main.nonJitteredProjectionMatrix;
        // The bare eye's gun drawn without the camera's jitter (it is laid over after the frame is smoothed over frames, so
        // it is not smoothed: jittered, half its pixels shimmered); its parts, lens and lasers likewise (BareGunProjection).
        Matrix4x4 bareProjection = steady;
        bareProjection.m22 = projection.m22;
        bareProjection.m23 = projection.m23;
        BareGunProjection = bareProjection;
        Renderer? aimedLens = TwoEyes.SightLens;
        foreach (Renderer r in renderers)
        {
            // Only what the camera itself would draw: switched off is not drawn (the first-person arms, with the watch, are
            // switched off in third person, where the body's own arms hold the gun).
            if (r == null || !r.enabled || r.forceRenderingOff || !r.gameObject.activeInHierarchy || !FinestLevel(r))
                continue;
            Mesh? mesh = null;
            if (r is SkinnedMeshRenderer skin)
            {
                if (skin.sharedMesh == null)
                    continue;
                if (!_bakedGun.TryGetValue(skin, out mesh) || mesh == null)
                {
                    mesh = new Mesh { name = "Justachae gun " + skin.name, hideFlags = HideFlags.DontSave };
                    mesh.MarkDynamic();
                    _bakedGun[skin] = mesh;
                }
                skin.BakeMesh(mesh);
            }
            else if (r.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null)
                mesh = filter.sharedMesh;
            if (mesh == null)
                continue;
            r.GetSharedMaterials(_gunMaterialsNow);
            if (!_gunSources.TryGetValue(r, out (Material[] materials, int[] passes, int[] forward) source) || !SameMaterials(source.materials, _gunMaterialsNow))
            {
                Material[] materials = _gunMaterialsNow.ToArray();
                int[] passes = new int[materials.Length], forward = new int[materials.Length];
                for (int i = 0; i < materials.Length; i++)
                {
                    // (What the game draws see-through, a sight's window, by its queue: not into the G-buffer, where it
                    // would be solid, a holographic sight's window black; by its forward pass if it has one.)
                    bool seeThrough = materials[i] != null && materials[i].renderQueue > 2500;
                    passes[i] = seeThrough ? -1 : GBufferPass(materials[i]);
                    forward[i] = seeThrough ? ForwardPassOf(materials[i]) : ForwardPass(materials[i]);
                    // A sight's glass (a holographic sight's window): not drawn into the bare eye's gun's own picture, where
                    // nothing stands behind it and its see-through shader left it black (DrawGunForward).
                    if (materials[i] != null && seeThrough && (materials[i].shader.name.IndexOf("glass", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || materials[i].name.IndexOf("glass", System.StringComparison.OrdinalIgnoreCase) >= 0))
                        _gunGlass.Add(materials[i]);
                }
                source = (materials, passes, forward);
                if (_gunSources.Count > 512)
                {
                    _gunSources.Clear();
                    _gunGlass.Clear();
                }
                _gunSources[r] = source;
            }
            Matrix4x4 matrix = FromCamera(r.transform, place);
            int count = Mathf.Min(source.materials.Length, mesh.subMeshCount);
            for (int s = 0; s < count; s++)
            {
                // Parts with no G-buffer pass (glass, lights, some small fittings): drawn after the scene, by their own
                // forward pass, from each eye (DrawGunForward).
                if (source.passes[s] < 0)
                {
                    // (Not the aimed sight's lens: its picture is laid by the game, or by TwoEyes.DrawLensLayer.)
                    if (source.forward[s] >= 0 && r != aimedLens)
                        _gunForward.Add((mesh, matrix, source.materials[s], s, source.forward[s], r));
                    continue;
                }
                _gunDraws.Add((mesh, matrix, source.materials[s], s, source.passes[s], r));
            }
        }
        GatherAmbient(main, renderers);
        Matrix4x4 fromEye = Matrix4x4.Translate(-shift);
        _forwardFromBare = fromEye;
        // The goggle eye's gun, from that eye (the camera stands between the two eyes).
        Matrix4x4 fromGoggle = Matrix4x4.Translate(-goggleShift);
        _forwardFromGoggle = fromGoggle;
        _goggleShiftNow = goggleShift;
        RenderTexture depthTexture = DirectTexture(_gunDepths, 0, width, height, 32, RenderTextureFormat.Depth, RenderTextureReadWrite.Linear, "Justachae NVG gun depth");
        RenderTexture? goggleDepth = DirectTexture(_goggleGunDepths, 0, width, height, 32, RenderTextureFormat.Depth, RenderTextureReadWrite.Linear, "Justachae NVG goggle eye's gun depth");
        int drawn = 0;
        for (int eye = 0; eye < 2; eye++)
        {
            RenderTexture?[] textures = eye == 0 ? _gunTextures : _goggleGunTextures;
            RenderTargetIdentifier[] targets = eye == 0 ? _gunTargets : _goggleGunTargets;
            for (int t = 0; t < 4; t++)
                targets[t] = DirectTexture(textures, t, width, height, 0, DirectFormats[t], t < 2 ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear, (eye == 0 ? "Justachae NVG gun " : "Justachae NVG goggle eye's gun ") + t);
            RenderTexture depth = eye == 0 ? depthTexture : goggleDepth;
            buffer.SetRenderTarget(targets, depth);
            buffer.ClearRenderTarget(true, true, Color.clear);
            buffer.SetViewProjectionMatrices(turned, eye == 0 ? bareProjection : projection);
            buffer.SetGlobalVector(CameraPlaceId, new Vector4(0f, 0f, 0f, 1f));
            buffer.EnableShaderKeyword("LIGHTPROBE_SH");
            Matrix4x4 eyeMove = eye == 0 ? fromEye : fromGoggle;
            foreach ((Mesh mesh, Matrix4x4 matrix, Material material, int sub, int pass, Renderer renderer) in _gunDraws)
            {
                if (renderer == null)
                    continue;
                renderer.GetPropertyBlock(_pieceBlock);
                SetAmbient(_pieceBlock);
                buffer.DrawMesh(mesh, eyeMove * matrix, material, sub, pass, _pieceBlock);
                drawn++;
            }
        }
        buffer.DisableShaderKeyword("LIGHTPROBE_SH");
        for (int t = 0; t < 4; t++)
            buffer.SetGlobalTexture(DirectGoggleGun[t], _goggleGunTextures[t]!);
        buffer.SetGlobalTexture(DirectGoggleGunDepth, goggleDepth);
        buffer.SetGlobalFloat(DirectGoggleShownId, 1f);
        buffer.SetGlobalFloat(DirectBareAimsId, TwoEyes.BareAims ? 1f : 0f);
        _bareShiftNow = shift;
        // Its sight's lens: the gun left out there, so the frame keeps what lies behind it (the goggle's picture is made
        // from it, and the lens laid over that at its see-through: TwoEyes.DrawLensLayer).
        RenderTexture lensMask = DirectTexture(_gunLensMask, 0, width, height, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear, "Justachae NVG gun's sight lens");
        buffer.SetRenderTarget(lensMask, depthTexture);
        buffer.ClearRenderTarget(false, true, Color.clear);
        buffer.SetViewProjectionMatrices(turned, bareProjection);
        Renderer? lens = TwoEyes.BareLens;
        if (lens != null && lens.TryGetComponent(out MeshFilter lensFilter) && lensFilter.sharedMesh != null)
        {
            Matrix4x4 lensMatrix = fromEye * FromCamera(lens.transform, place);
            for (int s = 0; s < lensFilter.sharedMesh.subMeshCount; s++)
                buffer.DrawMesh(lensFilter.sharedMesh, lensMatrix, _directMaterial, s, GunLensPass);
        }
        buffer.SetGlobalTexture(DirectGunLensId, lensMask);
        buffer.SetGlobalVector(DirectGunTexelId, new Vector4(1f / Mathf.Max(width, 1), 1f / Mathf.Max(height, 1), 0f, 0f));
        for (int t = 0; t < 4; t++)
            buffer.SetGlobalTexture(DirectGun[t], _gunTextures[t]!);
        buffer.SetGlobalTexture(DirectGunDepth, depthTexture);
        buffer.SetGlobalFloat(DirectGunShownId, shown);
        buffer.SetGlobalVector(CameraPlaceId, new Vector4(place.x, place.y, place.z, 1f));
        DrawGunForward(main, turned, place, bareProjection);
        return drawn > 0;
    }
}
