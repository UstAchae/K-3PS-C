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
// the lens drawn as ever, then the copy mixed back half and half (where the lens is not drawn the two are the same,
// so nothing else changes): the sight's picture seen through, the other eye's view behind it. Outside the experiment the
// call is the game's, unchanged.
internal static class SightLensHalves
{
    private static readonly AccessTools.FieldRef<SSAAPropagator, RenderTexture[]> HdrTargets =
        AccessTools.FieldRefAccess<SSAAPropagator, RenderTexture[]>("_resampledColorTargetHDR");
    private static readonly AccessTools.FieldRef<SSAAPropagator, RenderTexture> DepthTarget =
        AccessTools.FieldRefAccess<SSAAPropagator, RenderTexture>("_resampledDepthTarget");
    private static readonly FieldInfo? LensMaterialField = AccessTools.Field(typeof(SSAAPropagator), "_opticLensMaterial");
    private static readonly FieldInfo? CollimatorMaterialField = AccessTools.Field(typeof(SSAAPropagator), "_collimatorMaterial");
    private static readonly MethodInfo? DrawRendererMethod = AccessTools.Method(typeof(CommandBuffer), nameof(CommandBuffer.DrawRenderer),
        new[] { typeof(Renderer), typeof(Material), typeof(int), typeof(int) });
    private static readonly int PreLensId = Shader.PropertyToID("_JustachaeSightPreLens");
    private static readonly int PostLensId = Shader.PropertyToID("_JustachaeSightPostLens");
    private static readonly int OtherId = Shader.PropertyToID("_Other");
    private static readonly int OtherShareId = Shader.PropertyToID("_OtherShare");
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

    // Each DrawRenderer(lens or red dot, its material, 0, -1) in the method: our Draw, with the propagator, in its place.
    private static IEnumerable<CodeInstruction> TakeLensDraws(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        MethodInfo draw = AccessTools.Method(typeof(SightLensHalves), nameof(Draw));
        for (int i = 3; i < codes.Count; i++)
        {
            if (!codes[i].Calls(DrawRendererMethod!))
                continue;
            object? field = codes[i - 3].operand;
            if (!(codes[i - 3].opcode == OpCodes.Ldfld && (Equals(field, LensMaterialField) || Equals(field, CollimatorMaterialField))))
                continue;
            codes[i] = new CodeInstruction(OpCodes.Call, draw).MoveLabelsFrom(codes[i]);
            codes.Insert(i, new CodeInstruction(OpCodes.Ldarg_0));
            i++;
            _taken++;
        }
        return codes;
    }

    private static void Draw(CommandBuffer buffer, Renderer renderer, Material material, int submesh, int pass, SSAAPropagator propagator)
    {
        RenderTexture[]? targets = HdrTargets(propagator);
        RenderTexture? frame = targets != null && targets.Length > 0 ? targets[0] : null;
        RenderTexture? depth = DepthTarget(propagator);
        Shader? tubeShader = NightVisionEyepieces.TubeShader;
        if (!NightVisionEyepieces.TwoEyesNow || frame == null || depth == null || tubeShader == null)
        {
            buffer.DrawRenderer(renderer, material, submesh, pass);
            return;
        }
        _halves ??= new Material(tubeShader) { name = "Justachae sight lens halves", hideFlags = HideFlags.DontSave };
        _halves.SetFloat(OtherShareId, 0.5f);
        buffer.GetTemporaryRT(PreLensId, frame.width, frame.height, 0, FilterMode.Point, frame.format);
        buffer.GetTemporaryRT(PostLensId, frame.width, frame.height, 0, FilterMode.Point, frame.format);
        buffer.Blit(frame, PreLensId);
        buffer.SetRenderTarget(frame, depth);
        buffer.DrawRenderer(renderer, material, submesh, pass);
        buffer.Blit(frame, PostLensId);
        buffer.SetGlobalTexture(OtherId, PreLensId);
        buffer.Blit(PostLensId, frame, _halves, 1);
        buffer.SetRenderTarget(frame, depth);
        buffer.ReleaseTemporaryRT(PreLensId);
        buffer.ReleaseTemporaryRT(PostLensId);
    }
}
