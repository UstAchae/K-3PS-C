using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// The night-vision two-eye experiment (NightVisionOtherEye.cs): the two eyes' views half each, the sight's picture among
// them. The game lays a sight's lens (its picture, its reticle) and a red dot's into the frame at its upscaler
// (SSAAPropagator.FillCommandBuffer / OnRenderImage: DrawRenderer of the lens with its own material, after the frame is
// scaled to the screen), whole, over what lies behind it. Its call is taken over here: the frame copied just before,
// the lens drawn as ever, then, where the lens is: the sight's picture only to the aiming eye, and only while no eyepiece
// is before it (the picture is seen only from the sight's exit pupil, on its axis): half its picture, half what lay
// behind (the other eye's view); with an eyepiece before it, as that eyepiece's tube shows it (its colour, dimmed by the
// sight's exit pupil, NightVisionScope.cs), half too. The game lays the lens in after the eyepieces' picture is made, so it
// is made the tube's here.
// Outside the experiment the call is the game's, unchanged.
internal static class SightLensHalves
{
    private static readonly AccessTools.FieldRef<SSAAPropagator, RenderTexture[]> HdrTargets =
        AccessTools.FieldRefAccess<SSAAPropagator, RenderTexture[]>("_resampledColorTargetHDR");
    private static readonly AccessTools.FieldRef<SSAAPropagator, RenderTexture> DepthTarget =
        AccessTools.FieldRefAccess<SSAAPropagator, RenderTexture>("_resampledDepthTarget");
    private static readonly FieldInfo? LensMaterialField = AccessTools.Field(typeof(SSAAPropagator), "_opticLensMaterial");
    private static readonly FieldInfo? CollimatorMaterialField = AccessTools.Field(typeof(SSAAPropagator), "_collimatorMaterial");
    // The game calls the two-argument overload (its IL: ldfld renderer, ldfld material, callvirt DrawRenderer(Renderer, Material)).
    private static readonly MethodInfo? DrawRendererMethod = AccessTools.Method(typeof(CommandBuffer), nameof(CommandBuffer.DrawRenderer),
        new[] { typeof(Renderer), typeof(Material) });
    private static readonly int PreLensId = Shader.PropertyToID("_JustachaeSightPreLens");
    private static readonly int PostLensId = Shader.PropertyToID("_JustachaeSightPostLens");
    private static readonly int OtherId = Shader.PropertyToID("_Other");
    private static readonly int OtherShareId = Shader.PropertyToID("_OtherShare");
    private static readonly int AimBareId = Shader.PropertyToID("_AimBare");
    private static readonly int ScopeDimId = Shader.PropertyToID("_ScopeDim"), PhosphorId = Shader.PropertyToID("_Phosphor");
    private const int LensPass = 2;   // NvgTube.shader: the lens as the two eyes see it
    private static Material? _halves;
    private static int _taken;

    internal static void Install()
    {
        if (LensMaterialField == null || DrawRendererMethod == null)
        {
            NewCameraPlugin.Log?.LogWarning("Sight lens halves: the game's sight lens drawing not found; left as it is.");
            return;
        }
        var harmony = new Harmony(NewCameraPlugin.PluginGuid + ".sightLensHalves");
        var transpiler = new HarmonyMethod(typeof(SightLensHalves), nameof(TakeLensDraws));
        foreach (string name in new[] { "FillCommandBuffer", "OnRenderImage" })
        {
            MethodInfo? method = AccessTools.Method(typeof(SSAAPropagator), name);
            if (method != null)
                harmony.Patch(method, transpiler: transpiler);
        }
        NewCameraPlugin.Log?.LogInfo($"Sight lens halves: {_taken} of the game's sight lens draws taken over (laid in half in the two-eye experiment).");
    }

    // Each DrawRenderer(lens or red dot, its material) in the method: our Draw, with the propagator, in its place.
    private static IEnumerable<CodeInstruction> TakeLensDraws(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        MethodInfo draw = AccessTools.Method(typeof(SightLensHalves), nameof(Draw));
        for (int i = 1; i < codes.Count; i++)
        {
            if (!codes[i].Calls(DrawRendererMethod!))
                continue;
            object? field = codes[i - 1].operand;
            if (!(codes[i - 1].opcode == OpCodes.Ldfld && (Equals(field, LensMaterialField) || Equals(field, CollimatorMaterialField))))
                continue;
            codes[i] = new CodeInstruction(OpCodes.Call, draw).MoveLabelsFrom(codes[i]);
            codes.Insert(i, new CodeInstruction(OpCodes.Ldarg_0));
            i++;
            _taken++;
        }
        return codes;
    }

    // TEMP DIAG: every few seconds, the sight's lens as the game lays it: how many pixels it changed, and how bright
    // they were before and after (to find where a black lens comes from).
    private static int _diagAt;
    private static RenderTexture? _diagPre, _diagPost, _diagFinal, _diagRegion;
    private static float[]? _diagPreData, _diagPostData;
    private static bool _diagAfterDue;
    // After the lens is laid in as the eyes see it (NvgTube pass 2): how bright the lens's pixels are now, and how many
    // of them the aiming eye's own eyepiece region covers.
    private static void DiagAfter(CommandBuffer buffer, RenderTexture frame)
    {
        if (!_diagAfterDue || _diagPre == null)
            return;
        _diagAfterDue = false;
        int w = _diagPre.width, h = _diagPre.height;
        if (_diagFinal == null || _diagFinal.width != w || _diagFinal.height != h)
        {
            _diagFinal = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { hideFlags = HideFlags.DontSave };
            _diagRegion = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { hideFlags = HideFlags.DontSave };
        }
        buffer.Blit(frame, _diagFinal);
        buffer.Blit(Shader.PropertyToID("_AimLensRegion"), _diagRegion);
        float[]? fin = null, reg = null;
        void All()
        {
            if (fin == null || reg == null || _diagPreData == null || _diagPostData == null)
                return;
            int changed = 0, covered = 0;
            double after = 0;
            for (int p = 0; p < fin.Length / 4; p++)
            {
                float yb = 0.2126f * _diagPreData[p * 4] + 0.7152f * _diagPreData[p * 4 + 1] + 0.0722f * _diagPreData[p * 4 + 2];
                float ya = 0.2126f * _diagPostData[p * 4] + 0.7152f * _diagPostData[p * 4 + 1] + 0.0722f * _diagPostData[p * 4 + 2];
                if (Mathf.Abs(ya - yb) <= 1e-4f)
                    continue;
                changed++;
                after += 0.2126f * fin[p * 4] + 0.7152f * fin[p * 4 + 1] + 0.0722f * fin[p * 4 + 2];
                if (reg[p * 4] > 0.5f)
                    covered++;
            }
            NewCameraPlugin.Log?.LogInfo($"Sight lens DIAG after the eyes' lens pass: at the lens's {changed} pixels, mean light {(changed > 0 ? after / changed : 0):G3}; the aiming eye's eyepiece region over {covered} of them");
        }
        buffer.RequestAsyncReadback(_diagFinal, 0, TextureFormat.RGBAFloat, r => { if (!r.hasError) { fin = r.GetData<float>().ToArray(); All(); } });
        buffer.RequestAsyncReadback(_diagRegion, 0, TextureFormat.RGBAFloat, r => { if (!r.hasError) { reg = r.GetData<float>().ToArray(); All(); } });
    }
    private static void DiagLens(CommandBuffer buffer, int width, int height)
    {
        if (Time.frameCount < _diagAt)
            return;
        _diagAt = Time.frameCount + 300;
        _diagAfterDue = true;
        float[]? pre = null, post = null;
        void Both()
        {
            if (pre == null || post == null)
                return;
            _diagPreData = pre;
            _diagPostData = post;
            int changed = 0;
            double before = 0, after = 0;
            for (int p = 0; p < pre.Length / 4; p++)
            {
                float yb = 0.2126f * pre[p * 4] + 0.7152f * pre[p * 4 + 1] + 0.0722f * pre[p * 4 + 2];
                float ya = 0.2126f * post[p * 4] + 0.7152f * post[p * 4 + 1] + 0.0722f * post[p * 4 + 2];
                if (Mathf.Abs(ya - yb) > 1e-4f)
                {
                    changed++;
                    before += yb;
                    after += ya;
                }
            }
            NewCameraPlugin.Log?.LogInfo($"Sight lens DIAG: the lens changed {changed} of {pre.Length / 4} pixels; there, mean light before {(changed > 0 ? before / changed : 0):G3}, after {(changed > 0 ? after / changed : 0):G3}; aiming eye bare {NightVisionEyepieces.AimEyeBare}, sight light {NightVisionEyepieces.SightLightThroughEyepiece():F3}");
        }
        int w = Mathf.Max(width / 4, 1), h = Mathf.Max(height / 4, 1);
        if (_diagPre == null || _diagPre.width != w || _diagPre.height != h)
        {
            _diagPre = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { hideFlags = HideFlags.DontSave };
            _diagPost = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { hideFlags = HideFlags.DontSave };
        }
        buffer.Blit(PreLensId, _diagPre);
        buffer.Blit(PostLensId, _diagPost);
        buffer.RequestAsyncReadback(_diagPre, 0, TextureFormat.RGBAFloat, r => { if (!r.hasError) { pre = r.GetData<float>().ToArray(); Both(); } });
        buffer.RequestAsyncReadback(_diagPost, 0, TextureFormat.RGBAFloat, r => { if (!r.hasError) { post = r.GetData<float>().ToArray(); Both(); } });
    }

    private static void Draw(CommandBuffer buffer, Renderer renderer, Material material, SSAAPropagator propagator)
    {
        RenderTexture[]? targets = HdrTargets(propagator);
        RenderTexture? frame = targets != null && targets.Length > 0 ? targets[0] : null;
        RenderTexture? depth = DepthTarget(propagator);
        Shader? tubeShader = NightVisionEyepieces.TubeShader;
        if (!NightVisionEyepieces.TwoEyesNow || frame == null || depth == null || tubeShader == null)
        {
            buffer.DrawRenderer(renderer, material);
            return;
        }
        _halves ??= new Material(tubeShader) { name = "Justachae sight lens halves", hideFlags = HideFlags.DontSave };
        // The gun not see-through (NightVisionOtherEye.cs, GunSolid): its sight's lens the aiming eye's alone too.
        _halves.SetFloat(OtherShareId, NightVisionEyepieces.GunSolid ? 1f : 0.5f);
        // No judging whether an eyepiece is before the aiming eye: the aiming eye's own eyepiece's picture region (from
        // where that eye stands, NvgTube pass 2) says where its line of sight goes through it.
        _halves.SetFloat(AimBareId, 0f);
        _halves.SetFloat(ScopeDimId, NightVisionEyepieces.SightLightThroughEyepiece());
        _halves.SetColor(PhosphorId, NightVisionEyepieces.PhosphorNow);
        buffer.GetTemporaryRT(PreLensId, frame.width, frame.height, 0, FilterMode.Point, frame.format);
        buffer.GetTemporaryRT(PostLensId, frame.width, frame.height, 0, FilterMode.Point, frame.format);
        buffer.Blit(frame, PreLensId);
        buffer.SetRenderTarget(frame, depth);
        buffer.DrawRenderer(renderer, material);
        buffer.Blit(frame, PostLensId);
        DiagLens(buffer, frame.width, frame.height);
        buffer.SetGlobalTexture(OtherId, PreLensId);
        buffer.Blit(PostLensId, frame, _halves, LensPass);
        DiagAfter(buffer, frame);
        buffer.SetRenderTarget(frame, depth);
        buffer.ReleaseTemporaryRT(PreLensId);
        buffer.ReleaseTemporaryRT(PostLensId);
    }
}
