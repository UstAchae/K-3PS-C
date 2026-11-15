using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// A monocular's bare eye aiming (TwoRenders.cs): the gun is drawn into the frame from that eye, so the sight's lens the
// game lays in at its upscaler (SSAAPropagator, its "OutputOptic" part: the sight's and the gun's depth, then the lens)
// is laid in from that eye too. Its command buffer's view is moved over to the bare eye for that part, and given back
// after. Otherwise the game's, unchanged.
internal static class SightAtBareEye
{
    private static readonly MethodInfo? BeginSampleMethod = AccessTools.Method(typeof(CommandBuffer), nameof(CommandBuffer.BeginSample), new[] { typeof(string) });
    private static readonly MethodInfo? EndSampleMethod = AccessTools.Method(typeof(CommandBuffer), nameof(CommandBuffer.EndSample), new[] { typeof(string) });
    private static readonly AccessTools.FieldRef<SSAAPropagator, Camera> CameraOf = AccessTools.FieldRefAccess<SSAAPropagator, Camera>("_camera");
    private static readonly int CameraPlaceId = Shader.PropertyToID("_WorldSpaceCameraPos");
    private const string OpticPart = "OutputOptic";
    private static int _taken;

    internal static void Install()
    {
        if (BeginSampleMethod == null || EndSampleMethod == null)
        {
            NewCameraPlugin.Log?.LogWarning("Sight at the bare eye: the game's sample marks not found; left as it is.");
            return;
        }
        var harmony = new Harmony(NewCameraPlugin.PluginGuid + ".sightAtBareEye");
        var transpiler = new HarmonyMethod(typeof(SightAtBareEye), nameof(TakeMarks));
        foreach (string name in new[] { "FillCommandBuffer", "OnRenderImage" })
        {
            MethodInfo? method = AccessTools.Method(typeof(SSAAPropagator), name);
            if (method != null)
                harmony.Patch(method, transpiler: transpiler);
        }
        NewCameraPlugin.Log?.LogInfo($"Sight at the bare eye: {_taken} of the game's sample marks taken over (the sight's lens laid in from the bare eye when it aims).");
    }

    // Each BeginSample(name) / EndSample(name) on a command buffer in the method: ours, with the propagator, in its place.
    private static IEnumerable<CodeInstruction> TakeMarks(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        MethodInfo begin = AccessTools.Method(typeof(SightAtBareEye), nameof(Begin));
        MethodInfo end = AccessTools.Method(typeof(SightAtBareEye), nameof(End));
        for (int i = 0; i < codes.Count; i++)
        {
            bool isBegin = codes[i].Calls(BeginSampleMethod!), isEnd = codes[i].Calls(EndSampleMethod!);
            if (!isBegin && !isEnd)
                continue;
            codes[i] = new CodeInstruction(OpCodes.Call, isBegin ? begin : end).MoveLabelsFrom(codes[i]);
            codes.Insert(i, new CodeInstruction(OpCodes.Ldarg_0));
            i++;
            _taken++;
        }
        return codes;
    }

    private static void Begin(CommandBuffer buffer, string name, SSAAPropagator propagator)
    {
        buffer.BeginSample(name);
        if (name != OpticPart)
            return;
        Camera? camera = CameraOf(propagator);
        if (camera == null || !TwoRenders.SightFromBareEye(camera.transform.rotation, out Vector3 shift))
            return;
        Vector3 eye = camera.transform.position + shift;
        buffer.SetViewProjectionMatrices(camera.worldToCameraMatrix * Matrix4x4.Translate(-shift), camera.nonJitteredProjectionMatrix);
        buffer.SetGlobalVector(CameraPlaceId, new Vector4(eye.x, eye.y, eye.z, 1f));
        _moved = true;
    }

    private static bool _moved;

    private static void End(CommandBuffer buffer, string name, SSAAPropagator propagator)
    {
        if (name == OpticPart && _moved)
        {
            _moved = false;
            Camera? camera = CameraOf(propagator);
            if (camera != null)
            {
                Vector3 place = camera.transform.position;
                buffer.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
                buffer.SetGlobalVector(CameraPlaceId, new Vector4(place.x, place.y, place.z, 1f));
            }
        }
        buffer.EndSample(name);
    }
}
