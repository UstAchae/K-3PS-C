using UnityEngine;

namespace EFTNewCamera;

// The device's objective lens, as the picture forms on the tube: head-mounted image intensifiers look through a
// wide-open lens (about 27 mm, f/1.2: the AN/PVS-14's, the N-15's, the GPNVG-18's) focused far away, and their depth of
// field is shallow. Whatever is near is spread into a disc as wide, seen from the lens, as the lens's aperture over its
// distance from the lens; here half of NearBlurLimit at NearBlurDistance, more nearer, less further: the hands and
// the gun are soft, iron sights cannot be used, and a few metres off all is sharp. Done to the frame the lenses' picture is made from, before the game's or BRNVG's
// filter, so it holds for either (NvgObjective.shader: smooth Gaussians at a few sizes, as Call of Duty 4 does).
internal static partial class NightVisionEyepieces
{
    internal static float NearBlurDistance = 1f;        // from the lens, where the blur is half its limit (m)
    internal static float NearBlurLimit = 0.75f;        // what a blur's radius eases toward, the nearer (degrees)
    private const int Scale = 4;                        // the blur is made at a quarter of the frame's size
    private const int Levels = 5;                       // blurs spread over 1.5, 3, 6, 12, 24 quarter-size pixels

    private static Shader? _objectiveShader;
    private static Material? _objective;
    private static Vector3 _objectiveFront;             // the lenses' front in the device's frame (its eye side is +Y)
    private static readonly int LensId = Shader.PropertyToID("_Lens");
    private static readonly int TanHalfId = Shader.PropertyToID("_TanHalf");
    private static readonly int StepId = Shader.PropertyToID("_Step");
    private static readonly int NearBlurId = Shader.PropertyToID("_NearBlur");
    private static readonly int MaskId = Shader.PropertyToID("_Mask");
    private static RenderTexture? _blurMask;            // how blurred the frame was, for the picture made from it
    private static readonly int[] AllIds = { Shader.PropertyToID("_All0"), Shader.PropertyToID("_All1"), Shader.PropertyToID("_All2"),
        Shader.PropertyToID("_All3"), Shader.PropertyToID("_All4") };
    private static readonly int[] NearIds = { Shader.PropertyToID("_Near0"), Shader.PropertyToID("_Near1"), Shader.PropertyToID("_Near2"),
        Shader.PropertyToID("_Near3"), Shader.PropertyToID("_Near4") };

    // Where the FPS camera rendered from this frame, as it began to render (a free camera puts it back on the head before
    // the image effects, where the frame is worked on).
    private static Vector3 _renderedFrom, _renderedForward;
    private static Matrix4x4 _renderedViewProjection, _renderedView, _renderedProjection;
    private static int _renderedFrame = -1;

    internal static void NoteRendered(Camera camera)
    {
        if (camera == _scopeBufferOn)
            NoteOptic(camera);
        if (!camera.CompareTag("MainCamera"))
            return;
        _renderedFrom = camera.transform.position;
        _renderedForward = camera.transform.forward;
        _renderedView = camera.worldToCameraMatrix;
        _renderedProjection = camera.nonJitteredProjectionMatrix;
        _renderedViewProjection = _renderedProjection * _renderedView;
        _renderedFrame = Time.frameCount;
        NoteScope();
    }

    // The objective's front, from the device's model: the far end of it from the eyes, across from the tubes.
    private static void MeasureObjective(Transform body)
    {
        Mesh? mesh = body.GetComponent<MeshFilter>()?.sharedMesh;
        if (mesh == null && body.GetComponent<SkinnedMeshRenderer>() is SkinnedMeshRenderer skinned)
            mesh = skinned.sharedMesh;
        Vector3 across = Vector3.zero;
        foreach (Tube tube in _device.Tubes)
            across += tube.Centre;
        across /= Mathf.Max(_device.Tubes.Length, 1);
        float front = mesh != null ? mesh.bounds.min.y : across.y;
        _objectiveFront = new Vector3(across.x, front, across.z);
    }

    // The magnified sight's lens mask, ahead of the IR layer, which leaves the lens to the sight's own picture (WithIr):
    // as ThroughObjective would make it, once a frame.
    private static int _scopePreparedFrame = -1;

    private static void PrepareScopeEarly(Component camera, RenderTexture frame)
    {
        Camera? view = camera.GetComponent<Camera>();
        // Whatever the objective's blur is set to: the lens is the sight's own picture in any case.
        if (_objective == null || view == null || _body == null || _device.Thermal || !Active)
            return;
        if (_renderedFrame != Time.frameCount || (_body.TransformPoint(_objectiveFront) - _renderedFrom).magnitude > MaxEyeDistance)
            return;
        float tanUp = Mathf.Tan(view.fieldOfView * 0.5f * Mathf.Deg2Rad);
        _objective.SetVector(TanHalfId, new Vector2(tanUp * frame.width / frame.height, tanUp));
        PrepareScope(view, frame);
        _scopePreparedFrame = Time.frameCount;
    }

    // The frame as the objective lays it on the tube: made into a temporary (made) when there is a lens to do it.
    private static RenderTexture ThroughObjective(Component camera, RenderTexture frame, ref RenderTexture? made)
    {
        Camera? view = camera.GetComponent<Camera>();
        if (_objective == null || view == null || _body == null || _device.Thermal || !Active || NearBlurDistance <= 0f || NearBlurLimit <= 0f)
            return frame;
        // Only while the camera rendered from the eyes: a free camera elsewhere does not look through the objective.
        if (_renderedFrame != Time.frameCount || (_body.TransformPoint(_objectiveFront) - _renderedFrom).magnitude > MaxEyeDistance)
            return frame;

        float tanUp = Mathf.Tan(view.fieldOfView * 0.5f * Mathf.Deg2Rad);
        int width = Mathf.Max(frame.width / Scale, 1), height = Mathf.Max(frame.height / Scale, 1);
        float pixelsPerRadian = height / (2f * tanUp);
        float ahead = Mathf.Clamp(Vector3.Dot(_body.TransformPoint(_objectiveFront) - _renderedFrom, _renderedForward), 0f, MaxEyeDistance);
        _objective.SetVector(LensId, new Vector4(NearBlurDistance, 0f, ahead,
            NearBlurLimit * Mathf.Deg2Rad * pixelsPerRadian));
        _objective.SetVector(TanHalfId, new Vector2(tanUp * frame.width / frame.height, tanUp));
        if (_scopePreparedFrame != Time.frameCount)
            PrepareScope(view, frame);

        // Everything, blurred at each size; what is near, cut out (premultiplied), the same; and how far what is near
        // is spread, blurred so that it reaches past its edge.
        RenderTexture[] all = Blurred(frame, 0, width, height, _allLevels);
        RenderTexture[] near = Blurred(frame, 4, width, height, _nearLevels);
        RenderTexture[] nearBlur = Blurred(frame, 5, width, height, _nearBlurLevels);
        for (int i = 0; i < Levels; i++)
        {
            _objective.SetTexture(AllIds[i], all[i]);
            _objective.SetTexture(NearIds[i], near[i]);
        }
        _objective.SetTexture(NearBlurId, nearBlur[2]);
        made = RenderTexture.GetTemporary(frame.width, frame.height, 0, frame.format);
        Graphics.Blit(frame, made, _objective, 3);
        AddTritium(view, made);
        ReleaseMask();
        _blurMask = Temporary(width, height);
        Graphics.Blit(frame, _blurMask, _objective, 6);
        for (int i = 0; i < Levels; i++)
        {
            _objective.SetTexture(AllIds[i], Texture2D.blackTexture);
            _objective.SetTexture(NearIds[i], Texture2D.blackTexture);
        }
        _objective.SetTexture(NearBlurId, Texture2D.blackTexture);
        Release(all);
        Release(near);
        Release(nearBlur);
        return made;
    }

    // The frame cut down to a quarter by a pass, then spread at each size: each level the last one halved and spread
    // again, smooth at every size, with nothing left to show as blocks.
    private static readonly RenderTexture[] _allLevels = new RenderTexture[Levels], _nearLevels = new RenderTexture[Levels], _nearBlurLevels = new RenderTexture[3];

    private static RenderTexture[] Blurred(RenderTexture frame, int pass, int width, int height, RenderTexture[] levels)
    {
        int count = levels.Length;
        RenderTexture cut = Temporary(width, height);
        Graphics.Blit(frame, cut, _objective!, pass);
        levels[0] = Spread(cut);
        RenderTexture.ReleaseTemporary(cut);
        for (int i = 1; i < count; i++)
        {
            RenderTexture smaller = Temporary(levels[i - 1].width / 2, levels[i - 1].height / 2);
            Graphics.Blit(levels[i - 1], smaller);
            levels[i] = Spread(smaller);
            RenderTexture.ReleaseTemporary(smaller);
        }
        return levels;
    }

    private static void Release(RenderTexture[] textures)
    {
        foreach (RenderTexture texture in textures)
            RenderTexture.ReleaseTemporary(texture);
    }

    // The lenses' picture with its grain eased where the frame was blurred (pass 7): into a temporary (made) when it is.
    internal static Texture EaseGrain(Texture picture, ref RenderTexture? made)
    {
        if (_objective == null || _blurMask == null || picture is not RenderTexture source)
            return picture;
        RenderTexture eased = RenderTexture.GetTemporary(source.width, source.height, 0, source.format);
        _objective.SetTexture(MaskId, _blurMask);
        Graphics.Blit(source, eased, _objective, 7);
        _objective.SetTexture(MaskId, Texture2D.blackTexture);
        if (made != null)
            RenderTexture.ReleaseTemporary(made);
        made = eased;
        return eased;
    }

    internal static void ReleaseMask()
    {
        if (_blurMask != null)
            RenderTexture.ReleaseTemporary(_blurMask);
        _blurMask = null;
    }

    // A Gaussian over a texel and a half each way, across then up, into a new temporary.
    private static RenderTexture Spread(RenderTexture source)
    {
        RenderTexture across = Temporary(source.width, source.height);
        _objective!.SetVector(StepId, new Vector2(1f, 0f));
        Graphics.Blit(source, across, _objective, 1);
        RenderTexture up = Temporary(source.width, source.height);
        _objective.SetVector(StepId, new Vector2(0f, 1f));
        Graphics.Blit(across, up, _objective, 2);
        RenderTexture.ReleaseTemporary(across);
        return up;
    }

    private static RenderTexture Temporary(int width, int height)
    {
        RenderTexture texture = RenderTexture.GetTemporary(Mathf.Max(width, 1), Mathf.Max(height, 1), 0, RenderTextureFormat.ARGBHalf);
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        return texture;
    }
}
