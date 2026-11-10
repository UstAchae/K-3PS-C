using System.Collections.Generic;
using EFT;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// The objective's defocus as a lens makes it (step 3 of the eyepieces' picture, NightVisionComposite.cs). A point at a
// distance d from a lens of aperture D focused at s is spread over a disc, of even light, whose diameter seen from the
// lens is D·|1/d − 1/s|: the hands and the gun a few tenths of a metre off become discs degrees wide, the world a few
// metres off a fraction of one. Light is only moved, never made: a thing spread wide is the fainter for it.
// A wide aperture sees round what is near: a thing narrower than the lens (a barrel) hides little of what lies behind
// it, and that stays sharp, seen through the thing's spread light. So what lies behind the player's own gun, hands
// and body is drawn too: a second camera, the FPS camera's copy, renders the scene with all of them left to their
// shadow (BehindSelf), and the frame is put together as the lens lays it on the tube:
//   what is the player's own, each point spread over its disc, with how much of the lens it blocks;
//   over what lies behind it (the frame where nothing of the player's is in the way; the second camera's there);
//   the world near enough to be spread more than a quarter-size pixel, spread the same way, between the two.
// Spread by drawing each point's disc (NvgDefocus.shader), at a quarter of the frame's size: discs, not Gaussians.
// What the player's own is: nearer in the frame than in the second camera's picture. Behind the objective (the helmet,
// the device) nothing is seen through it.
internal static partial class NightVisionEyepieces
{
    private const float DefocusAperture = 0.0225f;      // the objective's, 27 mm at f/1.2 (as the tritium's)
    private const float DefocusMaxRadius = 96f;         // a disc's radius at most, in quarter-size pixels
    private const int DefocusScale = 4;

    private static Shader? _defocusShader;
    private static Material? _defocus;
    private static Camera? _behindCamera;
    private static RenderTexture? _behindColor, _behindDepth;
    private static CommandBuffer? _behindDepthBuffer, _discBuffer;
    private static int _behindFrame = -10;
    private static readonly MaterialPropertyBlock _nearDiscs = new MaterialPropertyBlock(), _worldDiscs = new MaterialPropertyBlock();

    private static readonly int BehindId = Shader.PropertyToID("_Behind");
    private static readonly int BehindDepthId = Shader.PropertyToID("_BehindDepth");
    private static readonly int DefocusLensId = Shader.PropertyToID("_DefocusLens");
    private static readonly int SrcId = Shader.PropertyToID("_Src");
    private static readonly int CocId = Shader.PropertyToID("_Coc");
    private static readonly int CocChannelId = Shader.PropertyToID("_CocChannel");
    private static readonly int GridId = Shader.PropertyToID("_Grid");
    private static readonly int NearAccId = Shader.PropertyToID("_NearAcc");
    private static readonly int WorldAccId = Shader.PropertyToID("_WorldAcc");

    // The player's own drawn meshes (each of the finest level of its LOD group), found again only as what they carry
    // changes (the hands, an item moved to or from them), and a moment after, as the game may make a model late.
    private const float SelfLoadSeconds = 1f;
    private static readonly List<Renderer> _selfRenderers = new List<Renderer>();
    private static readonly List<Renderer> _selfScratch = new List<Renderer>();
    private static readonly List<(Renderer renderer, bool off, ShadowCastingMode shadows)> _selfHidden = new List<(Renderer, bool, ShadowCastingMode)>();
    private static Player? _selfOwner;
    private static object? _selfHands;
    private static Transform? _selfHeld;
    private static bool _selfDirty = true;
    private static float _selfAgainAt = float.PositiveInfinity;

    internal static void ItemMovedPostfix(Player __instance)
    {
        if (__instance != null && __instance == _selfOwner)
            SelfChanged();
    }

    private static void SelfChanged()
    {
        _selfDirty = true;
        _selfAgainAt = Time.time + SelfLoadSeconds;
    }

    private static void GatherSelf(Player player)
    {
        Transform? held = player.ProceduralWeaponAnimation?.HandsContainer?.Weapon;
        object? hands = player.HandsController;
        if (player != _selfOwner || !ReferenceEquals(hands, _selfHands) || held != _selfHeld)
        {
            _selfOwner = player;
            _selfHands = hands;
            _selfHeld = held;
            SelfChanged();
        }
        if (Time.time >= _selfAgainAt)
        {
            _selfAgainAt = float.PositiveInfinity;
            _selfDirty = true;
        }
        if (!_selfDirty)
            return;
        _selfDirty = false;
        _selfRenderers.Clear();
        Transform root = ((Component)player).transform;
        AddSelf(root);
        if (held != null && !held.IsChildOf(root))
            AddSelf(held);
    }

    private static void AddSelf(Transform root)
    {
        root.GetComponentsInChildren(true, _selfScratch);
        foreach (Renderer r in _selfScratch)
        {
            if (r is MeshRenderer || r is SkinnedMeshRenderer)
                _selfRenderers.Add(r);
        }
        _selfScratch.Clear();
    }

    // Whether the objective's defocus is drawn this frame, the way the lens makes it.
    private static bool DefocusWanted => _defocus != null && _body != null && !_device.Thermal && Active && NearBlurDistance > 0f;

    // Just before the FPS camera renders, its pose final: the scene as it lies behind the player's own things.
    internal static void RenderBehindSelf(Camera camera)
    {
        if (camera != Camera.main || !DefocusWanted || camera.targetTexture != null && camera.targetTexture == _behindColor)
            return;
        if ((_body!.TransformPoint(_objectiveFront) - camera.transform.position).magnitude > MaxEyeDistance)
            return;
        Player? player = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null)
            return;
        GatherSelf(player);

        int width = Mathf.Max(camera.pixelWidth, 1), height = Mathf.Max(camera.pixelHeight, 1);
        if (_behindColor == null || _behindColor.width != width || _behindColor.height != height)
        {
            if (_behindColor != null)
                Object.Destroy(_behindColor);
            if (_behindDepth != null)
                Object.Destroy(_behindDepth);
            _behindColor = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf) { name = "Justachae NVG behind the player's own", hideFlags = HideFlags.DontSave };
            _behindDepth = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat) { name = "Justachae NVG behind the player's own, depth", hideFlags = HideFlags.DontSave };
        }
        if (_behindCamera == null)
        {
            GameObject go = new GameObject("Justachae NVG behind the player's own");
            Object.DontDestroyOnLoad(go);
            _behindCamera = go.AddComponent<Camera>();
            _behindCamera.enabled = false;
        }
        Camera behind = _behindCamera;
        behind.CopyFrom(camera);
        behind.rect = new Rect(0f, 0f, 1f, 1f);
        behind.targetTexture = _behindColor;
        behind.depthTextureMode |= DepthTextureMode.Depth;
        behind.projectionMatrix = camera.nonJitteredProjectionMatrix;

        // Its depth, as distance along the view, kept for the frame's image effect.
        _behindDepthBuffer ??= new CommandBuffer { name = "Justachae NVG behind, depth" };
        behind.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _behindDepthBuffer);
        _behindDepthBuffer.Clear();
        _behindDepthBuffer.Blit(BuiltinRenderTextureType.CurrentActive, _behindDepth, _defocus, 0);
        behind.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _behindDepthBuffer);

        // The player's own left to their shadow (or left out, those that cast none).
        _selfHidden.Clear();
        foreach (Renderer r in _selfRenderers)
        {
            if (r == null)
            {
                _selfDirty = true;
                continue;
            }
            if (!r.enabled || r.forceRenderingOff)
                continue;
            _selfHidden.Add((r, r.forceRenderingOff, r.shadowCastingMode));
            if (r.shadowCastingMode == ShadowCastingMode.Off)
                r.forceRenderingOff = true;
            else
                r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
        }
        try
        {
            foreach (AmbientLight ambient in _ambientLights)
            {
                if (ambient != null && ambient.isActiveAndEnabled)
                    ambient.ManualOnRenderObject(behind);
            }
            RedrawAmbient(behind);
            behind.Render();
            _behindFrame = Time.frameCount;
        }
        finally
        {
            foreach ((Renderer r, bool off, ShadowCastingMode shadows) in _selfHidden)
            {
                if (r == null)
                    continue;
                r.forceRenderingOff = off;
                r.shadowCastingMode = shadows;
            }
            _selfHidden.Clear();
            behind.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _behindDepthBuffer);
            behind.targetTexture = null;
        }
    }

    // Step 3 the lens's way: false (the frame left to the older blur) when the scene behind was not drawn this frame.
    private static bool DefocusThrough(Camera view, RenderTexture frame, ref RenderTexture? made)
    {
        if (!DefocusWanted || _behindFrame != Time.frameCount || _behindColor == null || _behindDepth == null)
            return false;
        Material defocus = _defocus!;
        int width = Mathf.Max(frame.width / DefocusScale, 1), height = Mathf.Max(frame.height / DefocusScale, 1);
        float tanUp = Mathf.Tan(view.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float pixelsPerRadian = height / (2f * tanUp);
        float ahead = Mathf.Clamp(Vector3.Dot(_body!.TransformPoint(_objectiveFront) - _renderedFrom, _renderedForward), 0f, MaxEyeDistance);
        // x: 1 over the focus distance (far away: 0); y: a disc's radius in quarter-size pixels per unit of |1/d − 1/s|;
        // z: how far the objective is ahead of the camera; w: the largest radius drawn.
        defocus.SetVector(DefocusLensId, new Vector4(0f, 0.5f * DefocusAperture * pixelsPerRadian, ahead, DefocusMaxRadius));
        defocus.SetVector(TanHalfId, TanHalf(view, frame));
        defocus.SetTexture(BehindId, _behindColor);
        defocus.SetTexture(BehindDepthId, _behindDepth);
        BindScope(defocus);

        RenderTexture nearColor = Temporary(width, height), coc = Temporary(width, height), worldColor = Temporary(width, height);
        Graphics.Blit(frame, nearColor, defocus, 1);
        Graphics.Blit(frame, coc, defocus, 2);
        Graphics.Blit(frame, worldColor, defocus, 3);

        RenderTexture nearAcc = Temporary(width, height), worldAcc = Temporary(width, height);
        CommandBuffer discs = _discBuffer ??= new CommandBuffer { name = "Justachae NVG objective discs" };
        discs.Clear();
        // Into a render texture the frame's up is flipped where textures start at the top.
        Vector4 grid = new Vector4(width, height, SystemInfo.graphicsUVStartsAtTop ? -1f : 1f, 0f);
        DrawDiscs(discs, _nearDiscs, nearAcc, nearColor, coc, 0f, grid, width * height);
        DrawDiscs(discs, _worldDiscs, worldAcc, worldColor, coc, 1f, grid, width * height);
        Graphics.ExecuteCommandBuffer(discs);

        defocus.SetTexture(NearAccId, nearAcc);
        defocus.SetTexture(WorldAccId, worldAcc);
        made = RenderTexture.GetTemporary(frame.width, frame.height, 0, frame.format);
        Graphics.Blit(frame, made, defocus, 5);
        AddTritium(view, made);
        ReleaseMask();
        _blurMask = Temporary(width, height);
        Graphics.Blit(frame, _blurMask, defocus, 6);

        defocus.SetTexture(NearAccId, Texture2D.blackTexture);
        defocus.SetTexture(WorldAccId, Texture2D.blackTexture);
        defocus.SetTexture(BehindId, Texture2D.blackTexture);
        defocus.SetTexture(BehindDepthId, Texture2D.blackTexture);
        RenderTexture.ReleaseTemporary(nearColor);
        RenderTexture.ReleaseTemporary(coc);
        RenderTexture.ReleaseTemporary(worldColor);
        RenderTexture.ReleaseTemporary(nearAcc);
        RenderTexture.ReleaseTemporary(worldAcc);
        if (!_defocusNoted)
        {
            _defocusNoted = true;
            NewCameraPlugin.Log?.LogInfo($"NVG objective: defocus as the lens makes it, discs over the scene behind the player's own ({_selfRenderers.Count} of their meshes left out of it, {width}x{height} discs a layer, {_behindColor.width}x{_behindColor.height} behind).");
        }
        return true;
    }
    private static bool _defocusNoted;

    // Each quarter-size pixel of a layer drawn as its disc, added up.
    private static void DrawDiscs(CommandBuffer buffer, MaterialPropertyBlock block, RenderTexture into, RenderTexture source, RenderTexture coc, float channel, Vector4 grid, int count)
    {
        buffer.SetRenderTarget(into);
        buffer.ClearRenderTarget(false, true, Color.clear);
        block.Clear();
        block.SetTexture(SrcId, source);
        block.SetTexture(CocId, coc);
        block.SetFloat(CocChannelId, channel);
        block.SetVector(GridId, grid);
        buffer.DrawProcedural(Matrix4x4.identity, _defocus!, 4, MeshTopology.Triangles, count * 6, 1, block);
    }
}
