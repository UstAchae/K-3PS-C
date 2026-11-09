using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using Comfort.Common;
using EFT;
using EFT.Animations;
using EFT.Animations.NewRecoil;
using HarmonyLib;
using UnityEngine;

namespace EFTNewCamera;

// Whose recoil the gun has (F12 「瞄准」 → 后坐力): the game's own, all of it as its first person has it, put on the
// third-person gun, body and view (AimArms: Recoil, FollowGun, BodyRecoil), or SPTRecoilReworkLegacy's, when that mod is
// installed (the same, on the springs it sets, less the view's turn after the gun, which it stops; its own pull of the
// look works as it does without this mod).
// It has no switch of its own (its patches are always in), so while it is not chosen its patches are taken out of the
// game, and put back as they were when it is. Its server part changes some guns' recoil figures in the database; that
// stays, whatever is chosen here.
internal static class RecoilMode
{
    internal const string Legacy = "原版后坐力", Rework = "Recoil Rework";
    private const string ReworkGuid = "com.pein.camerarecoilmod";

    private static Assembly? _rework;
    private static bool _reworkIn = true;
    private static string _mode = Legacy;
    private static readonly List<Removed> Taken = new();

    private readonly struct Removed
    {
        internal readonly MethodBase Original;
        internal readonly HarmonyPatchType Kind;
        internal readonly Patch Patch;

        internal Removed(MethodBase original, HarmonyPatchType kind, Patch patch)
        {
            Original = original;
            Kind = kind;
            Patch = patch;
        }
    }

    // The values of the shot's recoil that the game sets once for the player and that Recoil Rework overwrites,
    // taken before it first does (the game's), so going back from it gives the game's again.
    private static NewRecoilShotEffect? _savedFor;
    private static float _cameraIntensity, _cameraReturn, _cameraDamping, _handsIntensity, _handsReturn, _handsDamping;

    internal static bool ReworkInstalled => _rework != null;

    // Its patches are in the game now (chosen, or the mod switched off).
    internal static bool ReworkWorking => _rework != null && _reworkIn;

    // At load, after Recoil Rework (a soft dependency, so it loads first).
    internal static void Find()
    {
        if (Chainloader.PluginInfos.TryGetValue(ReworkGuid, out BepInEx.PluginInfo info) && info.Instance != null)
            _rework = info.Instance.GetType().Assembly;
    }

    internal static string[] Choices => ReworkInstalled ? new[] { Legacy, Rework } : new[] { Legacy };

    internal static void Set(string mode)
    {
        _mode = mode == Rework && ReworkInstalled ? Rework : Legacy;
        Refresh();
    }

    // Also when the master switch changes: with the mod off, Recoil Rework works as it would without it.
    internal static void Refresh()
    {
        SetRework(_mode == Rework || !NewCameraSwitch.Enabled);
    }

    private static void SetRework(bool on)
    {
        if (_rework == null || on == _reworkIn)
            return;
        _reworkIn = on;
        if (on)
            PutBack();
        else
            TakeOut();

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null)
            return;
        if (on)
            AddComponents(player);
        else
            RestoreGameValues(player);
        // The gun in the hands has its recoil worked out again, now by (or without) Recoil Rework. What it sets as the
        // gun is drawn (the hands' turn and its roll) follows from the next gun drawn.
        try
        {
            player.ProceduralWeaponAnimation?.Shootingg?.OnWeaponParametersChanged();
        }
        catch (System.Exception e)
        {
            NewCameraPlugin.Log?.LogWarning($"Recoil: the gun's recoil was not worked out again ({e.Message}).");
        }
    }

    private static void TakeOut()
    {
        Taken.Clear();
        foreach (MethodBase original in Harmony.GetAllPatchedMethods())
        {
            HarmonyLib.Patches? info = Harmony.GetPatchInfo(original);
            if (info == null)
                continue;
            Collect(original, HarmonyPatchType.Prefix, info.Prefixes);
            Collect(original, HarmonyPatchType.Postfix, info.Postfixes);
            Collect(original, HarmonyPatchType.Transpiler, info.Transpilers);
            Collect(original, HarmonyPatchType.Finalizer, info.Finalizers);
        }
        foreach (Removed removed in Taken)
            new Harmony(removed.Patch.owner).Unpatch(removed.Original, removed.Patch.PatchMethod);
    }

    private static void Collect(MethodBase original, HarmonyPatchType kind, IEnumerable<Patch> patches)
    {
        foreach (Patch patch in patches)
        {
            if (patch.PatchMethod?.DeclaringType?.Assembly == _rework)
                Taken.Add(new Removed(original, kind, patch));
        }
    }

    private static void PutBack()
    {
        foreach (Removed removed in Taken)
        {
            Patch patch = removed.Patch;
            HarmonyMethod method = new HarmonyMethod(patch.PatchMethod, patch.priority, patch.before, patch.after);
            Harmony harmony = new Harmony(patch.owner);
            switch (removed.Kind)
            {
                case HarmonyPatchType.Prefix:
                    harmony.Patch(removed.Original, prefix: method);
                    break;
                case HarmonyPatchType.Postfix:
                    harmony.Patch(removed.Original, postfix: method);
                    break;
                case HarmonyPatchType.Transpiler:
                    harmony.Patch(removed.Original, transpiler: method);
                    break;
                case HarmonyPatchType.Finalizer:
                    harmony.Patch(removed.Original, finalizer: method);
                    break;
            }
        }
        Taken.Clear();
    }

    // Recoil Rework puts its parts on the player as the raid starts; chosen in the raid, they are put on now.
    private static void AddComponents(Player player)
    {
        foreach (string name in new[] { "SPTRecoilRework.Components.RealRecoilComponent", "SPTRecoilRework.Components.CameraOffsetComponent" })
        {
            System.Type? type = _rework?.GetType(name);
            if (type != null && player.gameObject.GetComponent(type) == null)
                player.gameObject.AddComponent(type);
        }
    }

    // NewRecoilShotEffect.RecalculateRecoilParamsOnChangeWeapon, before anyone else's postfix.
    internal static void RecalculatePostfix(NewRecoilShotEffect __instance)
    {
        if (ReferenceEquals(__instance, _savedFor) || __instance.CameraRotationRecoil == null || __instance.HandPositionRecoil == null)
            return;
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !ReferenceEquals(player.ProceduralWeaponAnimation?.Shootingg?.NewShotRecoil, __instance))
            return;
        _savedFor = __instance;
        _cameraIntensity = __instance.CameraRotationRecoil.Intensity;
        _cameraReturn = __instance.CameraRotationRecoil.ReturnSpeed;
        _cameraDamping = __instance.CameraRotationRecoil.Damping;
        _handsIntensity = __instance.HandPositionRecoil.Intensity;
        _handsReturn = __instance.HandPositionRecoil.ReturnSpeed;
        _handsDamping = __instance.HandPositionRecoil.Damping;
    }

    private static void RestoreGameValues(Player player)
    {
        NewRecoilShotEffect? effect = player.ProceduralWeaponAnimation?.Shootingg?.NewShotRecoil;
        if (effect == null || !ReferenceEquals(effect, _savedFor))
            return;
        effect.CameraRotationRecoil.Intensity = _cameraIntensity;
        effect.CameraRotationRecoil.ReturnSpeed = _cameraReturn;
        effect.CameraRotationRecoil.Damping = _cameraDamping;
        effect.HandPositionRecoil.Intensity = _handsIntensity;
        effect.HandPositionRecoil.ReturnSpeed = _handsReturn;
        effect.HandPositionRecoil.Damping = _handsDamping;
    }
}
