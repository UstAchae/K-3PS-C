using System.Text.RegularExpressions;
using EFT;
using EFT.CameraControl;
using EFT.InventoryLogic;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// A magnified sight looked through with the goggle down. The goggle's objective sits well in front of the eye, so the
// head cannot come to the sight: it stays back, and the objective comes to where the eye would be (the sight's own
// eye point, where the game puts the camera). The camera, which makes the goggle's picture, goes there with it, the
// eyes and the device a hand's breadth behind (NightVisionEyes: each eye's shift).
// The sight's picture is at infinity, as the goggle is focused, so it is sharp (no objective blur over the lens). Its
// light leaves the eyepiece through the sight's exit pupil (objective diameter over magnification) and only what of it the
// goggle's objective takes in reaches the tube: the sight's picture as bright as the overlap of the pupil's beam and the
// goggle's aperture, over the aperture. That is worked into what the tube sees alone (pass 3 of NvgObjective.shader, over
// the sight's lens): seen past the goggle's lens, by the bare eye, the sight keeps its light. At high power the pupil is a
// few millimetres across and the picture dim, and the tube's noise, laid on later at its usual strength, stands out the
// more; with the objective off the eye point the beam slides off the aperture.
internal static partial class NightVisionEyepieces
{
    internal static float ScopePullback;                 // how far the camera is ahead of the eyes now (m)
    private const float ScopePullbackSeconds = 0.15f;
    private const float DefaultScopeObjective = 0.032f;  // a sight's objective when its name gives none (m)
    private static readonly Regex ObjectiveInName = new Regex(@"\d+(?:[.,]\d+)?[x×](\d+)", RegexOptions.Compiled);

    private static Material? _scopeMask;
    private static float _scopeLight = 1f;              // the share of the sight's light that reaches the tube
    private static RenderTexture? _scopeLens;           // where the sight's lens is in the frame
    private static CommandBuffer? _scopeBuffer, _scopeLensBuffer;
    private static Camera? _scopeBufferOn;
    private static readonly int ScopeLensId = Shader.PropertyToID("_ScopeLens");
    private static readonly int ScopeOnId = Shader.PropertyToID("_ScopeOn");
    private static readonly int ScopeLightId = Shader.PropertyToID("_ScopeLight");
    private static readonly int ScopeTempId = Shader.PropertyToID("_JustachaeScopeLight");

    // The sight's lens as the camera renders it: by the time the frame is worked on the gun may have moved on.
    private static Mesh? _lensMesh;
    private static Matrix4x4 _lensMatrix;
    private static int _lensFrame = -1;

    // How far ahead of the head's camera place the camera goes this frame (eased in and out): to the objective, while
    // aiming a magnified sight with the goggle down.
    internal static float ScopePull(Player player, Vector3 cameraPlace, Quaternion cameraTurn)
    {
        float target = 0f;
        if (Active && !_device.Thermal && _body != null && player.HandsController != null && player.HandsController.IsAiming
            && Scopes.Current(player.ProceduralWeaponAnimation) is { IsOptic: true } && ChestStance.Weight <= 0f && GoggleBeforeAimingEye(cameraPlace, cameraTurn))
            target =Mathf.Clamp(Vector3.Dot(_body.TransformPoint(_objectiveFront) - cameraPlace, cameraTurn * Vector3.forward), 0f, MaxEyeDistance);
        ScopePullback = Mathf.Lerp(target, ScopePullback, Mathf.Exp(-Time.deltaTime / ScopePullbackSeconds));
        if (ScopePullback < 1e-4f)
            ScopePullback = 0f;
        return ScopePullback;
    }

    // A monocular before the other eye: the aiming eye looks through the sight bare, as without the goggle. Taken for the goggle's, the
    // sight's beam missed its objective altogether and the sight was black.
    private const float BeforeEyeWithin = 0.03f;   // m across, half the way to the other eye

    private static bool GoggleBeforeAimingEye(Vector3 cameraPlace, Quaternion cameraTurn)
    {
        if (!_device.Monocular || _lensNodes.Length == 0)
            return true;
        return Mathf.Abs(Vector3.Dot(_lensNodes[CentreLensA].position - cameraPlace, cameraTurn * Vector3.right)) <= BeforeEyeWithin;
    }

    // As the main camera begins to render: the lens's place noted, and the light of the sight that reaches the objective
    // worked out (the sight camera's command buffer on while there is a sight to look through, off otherwise).
    private static void NoteScope()
    {
        Camera? optic = null;
        try
        {
            optic = CameraManager.Instance?.OpticCameraManager?.Camera;
        }
        catch
        {
        }
        bool on = false;
        if (ScopePullback > 0.005f && optic != null && LoadShaders() && _objectiveShader != null)
        {
            Player? player = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer;
            var scope = Scopes.Current(player?.ProceduralWeaponAnimation);
            OpticSight? sight = CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight;
            Renderer? lens = sight != null ? sight.LensRenderer : null;
            Mesh? mesh = lens != null && lens.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
            if (scope?.Mod != null && lens != null && lens.enabled && mesh != null)
            {
                _lensMesh = mesh;
                _lensMatrix = lens.localToWorldMatrix;
                _lensFrame = Time.frameCount;
                on = true;

                // The pupil: on the sight camera's axis, at the eye point, where the game holds the eye and the
                // objective (the camera now) has come to; how far off it the objective is, across in the sight camera's
                // right and up, and along.
                Transform view = optic.transform;
                Vector3 lensCentre = _lensMatrix.MultiplyPoint3x4(mesh.bounds.center);
                Vector3 axis = view.forward;
                float relief = Vector3.Dot(lensCentre - _renderedFrom, axis);
                Vector3 off = _renderedFrom - (lensCentre - axis * relief);
                float magnification = MagnificationOf(scope.Mod, sight!, optic);
                float objective = ObjectiveOf(scope.Mod);
                _scopeObjectiveRadius = 0.5f * objective;
                float apart = new Vector2(Vector3.Dot(off, view.right), Vector3.Dot(off, view.up)).magnitude;
                float aperture = 0.5f * Aperture;
                _scopeLight = DiscsOverlap(0.5f * objective / magnification, aperture, apart) / (Mathf.PI * aperture * aperture);
            }
        }
        if (on && _scopeBufferOn != optic)
        {
            RemoveScopeBuffer();
            _scopeBuffer ??= MakeScopeBuffer();
            optic!.AddCommandBuffer(CameraEvent.AfterEverything, _scopeBuffer);
            _scopeBufferOn = optic;
            // The vials' discs in its picture are hidden by what stands nearer, from its own depth.
            _opticDepthBefore = optic.depthTextureMode;
            optic.depthTextureMode |= DepthTextureMode.Depth;
        }
        else if (!on)
            RemoveScopeBuffer();
    }

    // The area two discs of radius r and R, their centres d apart, have in common.
    private static float DiscsOverlap(float r, float R, float d)
    {
        if (d >= r + R)
            return 0f;
        if (d <= Mathf.Abs(R - r))
        {
            float least = Mathf.Min(r, R);
            return Mathf.PI * least * least;
        }
        float a = r * r * Mathf.Acos(Mathf.Clamp((d * d + r * r - R * R) / (2f * d * r), -1f, 1f));
        float b = R * R * Mathf.Acos(Mathf.Clamp((d * d + R * R - r * r) / (2f * d * R), -1f, 1f));
        float c = 0.5f * Mathf.Sqrt(Mathf.Max((-d + r + R) * (d + r - R) * (d - r + R) * (d + r + R), 0f));
        return a + b - c;
    }

    private static void RemoveScopeBuffer()
    {
        if (_scopeBufferOn != null && _scopeBuffer != null)
        {
            _scopeBufferOn.RemoveCommandBuffer(CameraEvent.AfterEverything, _scopeBuffer);
            _scopeBufferOn.depthTextureMode = _opticDepthBefore;
        }
        _scopeObjectiveRadius = 0f;
        _scopeBufferOn = null;
    }

    // The sight's lens drawn into a mask of the frame, where the objective's blur leaves the picture sharp; or none.
    private static void PrepareScope(Camera view, RenderTexture frame)
    {
        _objective!.SetTexture(ScopeLensId, Texture2D.blackTexture);
        _objective.SetFloat(ScopeOnId, 0f);
        if (ScopePullback <= 0.005f || _lensMesh == null || _lensFrame != Time.frameCount)
            return;
        _scopeMask ??= new Material(_objectiveShader!) { hideFlags = HideFlags.DontSave };
        if (_scopeLens == null || _scopeLens.width != frame.width || _scopeLens.height != frame.height)
        {
            if (_scopeLens != null)
                _scopeLens.Release();
            _scopeLens = new RenderTexture(frame.width, frame.height, 0, RenderTextureFormat.R8) { name = "Justachae NVG sight lens", filterMode = FilterMode.Bilinear };
            _scopeLens.Create();
        }
        _scopeMask.SetVector(TanHalfId, _objective.GetVector(TanHalfId));
        CommandBuffer buffer = _scopeLensBuffer ??= new CommandBuffer { name = "Justachae NVG sight lens" };
        buffer.Clear();
        buffer.SetRenderTarget(_scopeLens);
        buffer.ClearRenderTarget(false, true, Color.clear);
        buffer.SetViewProjectionMatrices(_renderedView, _renderedProjection);  // the buffer makes it the GPUs own
        for (int i = 0; i < _lensMesh.subMeshCount; i++)
            buffer.DrawMesh(_lensMesh, _lensMatrix, _scopeMask, i, 10);
        Graphics.ExecuteCommandBuffer(buffer);
        _objective.SetTexture(ScopeLensId, _scopeLens);
        _objective.SetFloat(ScopeOnId, 1f);
        _objective.SetFloat(ScopeLightId, _scopeLight);
    }
    // The sight's power now. The game's own figure (GetCurrentOpticZoom) is its table of powers by mode, which for a
    // variable sight turned smoothly with the wheel holds one end only. What the wheel turns is the sight camera's
    // field: SightComponent.ScopeZoomValue, between MinMaxFov.x, the widest, at the least power, and MinMaxFov.y, the
    // narrowest, at the most (ScopeZoomHandler: MinFov is .x, MaxFov .y, the field clamped between them, ZoomFactor
    // 0 at .x and 1 at .y). A sight's power goes as one over the tangent of half its field.
    private static float MagnificationOf(SightComponent mod, OpticSight sight, Camera optic)
    {
        float least = Mathf.Max(mod.GetMinOpticZoom(), 1f), most = Mathf.Max(mod.GetMaxOpticZoom(), least);
        if (sight.CameraData is ScopeSmoothCameraData smooth && most > least + 0.01f)
        {
            float widest = smooth.MinMaxFieldOfView.x;
            float now = mod.ScopeZoomValue > 0.01f ? mod.ScopeZoomValue : optic.fieldOfView;
            if (widest > 0.01f)
                return Mathf.Clamp(least * Mathf.Tan(widest * 0.5f * Mathf.Deg2Rad) / Mathf.Tan(now * 0.5f * Mathf.Deg2Rad), least, most);
        }
        return Mathf.Max(mod.GetCurrentOpticZoom(), 1f);
    }

    // The sight's objective diameter, from its name ("..._4_16x56..." 56 mm), or a common one.
    private static float ObjectiveOf(SightComponent mod)
    {
        string name = mod.Item.Template._name ?? "";
        MatchCollection found = ObjectiveInName.Matches(name);
        if (found.Count > 0 && float.TryParse(found[found.Count - 1].Groups[1].Value, out float mm) && mm >= 15f && mm <= 80f)
            return mm / 1000f;
        return DefaultScopeObjective;
    }
}
