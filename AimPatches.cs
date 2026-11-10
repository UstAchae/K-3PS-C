using System.Reflection;
using EFT;
using EFT.Animations;
using EFT.Animations.NewRecoil;
using HarmonyLib;

namespace EFTNewCamera;

// The game methods patched for the body, the aim and the weapon. One the game does not have (or one that fails to
// patch) is logged and left out, and the rest still go in.
internal static class AimPatches
{
    private static bool _patched;

    internal static void Install()
    {
        if (_patched)
            return;
        _patched = true;

        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".aimarms");
        Patch(harmony, AccessTools.Method(typeof(PlayerAnimator), nameof(PlayerAnimator.SetAiming)),
            prefix: Hook(typeof(AimArms), nameof(AimArms.SetAimingPrefix)));
        Patch(harmony, AccessTools.Method(typeof(ProceduralWeaponAnimation), "FindAimTransforms"),
            postfix: Hook(typeof(IronSightAlignment), nameof(IronSightAlignment.Invalidate)));
        Patch(harmony, AccessTools.Method(typeof(ProceduralWeaponAnimation), "FindAimTransforms"),
            postfix: Hook(typeof(Scopes), nameof(Scopes.DepthAsFirstPerson)));
        Patch(harmony, AccessTools.Method(typeof(PlayerBones), nameof(PlayerBones.RotateHead)),
            prefix: Hook(typeof(AimArms), nameof(AimArms.RotateHeadPrefix)),
            postfix: Hook(typeof(AimArms), nameof(AimArms.RotateHeadPostfix), Priority.Last));
        Patch(harmony, AccessTools.Method(typeof(FirearmsAnimator), nameof(FirearmsAnimator.SetFireMode)),
            postfix: Hook(typeof(AimArms), nameof(AimArms.FireModePostfix)));
        Patch(harmony, AccessTools.Method(typeof(FirearmsAnimator), nameof(FirearmsAnimator.TriggerFiremodeCheck)),
            postfix: Hook(typeof(AimArms), nameof(AimArms.FireModeCheckPostfix)));
        Patch(harmony, AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.InitiateShot)),
            postfix: Hook(typeof(AimArms), nameof(AimArms.BoltShotPostfix)));
        Patch(harmony, AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.AdjustShotVectors)),
            prefix: Hook(typeof(AimArms), nameof(AimArms.AdjustShotPrefix)));
        Patch(harmony, AccessTools.Method(typeof(Physical), nameof(Physical.Aim)),
            prefix: Hook(typeof(AimStamina), nameof(AimStamina.AimPrefix)));
        Patch(harmony, AccessTools.Method(typeof(Physical), nameof(Physical.HoldBreath)),
            postfix: Hook(typeof(AimStamina), nameof(AimStamina.HoldBreathPostfix)));
        Patch(harmony, AccessTools.Method(typeof(Player), "VisualPass"),
            postfix: Hook(typeof(AimArms), nameof(AimArms.VisualPassPostfix), Priority.Last));
        Patch(harmony, AccessTools.Method(typeof(Player), nameof(Player.MovementUpdate)),
            postfix: Hook(typeof(AimArms), nameof(AimArms.MovementUpdatePostfix)));
        Patch(harmony, AccessTools.Method(typeof(Player), nameof(Player.Look)),
            prefix: Hook(typeof(AimArms), nameof(AimArms.LookPrefix)),
            postfix: Hook(typeof(AimArms), nameof(AimArms.LookPostfix)));
        Patch(harmony, LaserReplay.FindLateUpdate(),
            prefix: Hook(typeof(LaserReplay), nameof(LaserReplay.LaserLateUpdatePrefix)),
            transpiler: Hook(typeof(LaserReplay), nameof(LaserReplay.DrawForReplayCamera)));
        Patch(harmony, AccessTools.PropertyGetter(typeof(Player), nameof(Player.IsVisible)),
            postfix: Hook(typeof(AimArms), nameof(AimArms.VisiblePostfix)));
        Patch(harmony, AccessTools.Method(typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.SetStrategy), new[] { typeof(EPointOfView) }),
            prefix: Hook(typeof(AimArms), nameof(AimArms.SetStrategyPrefix)));
        Patch(harmony, AccessTools.Method(AccessTools.TypeByName("ThirdPersonStrategy"), "ProcessEffectors"),
            prefix: Hook(typeof(AimArms), nameof(AimArms.BreatheAsFirstPerson)));
        Patch(harmony, AccessTools.Method(typeof(FirearmHandsInputTranslator), nameof(FirearmHandsInputTranslator.TranslateCommand)),
            prefix: Hook(typeof(ChestStance), nameof(ChestStance.TranslatePrefix)));
        Patch(harmony, AccessTools.Method(typeof(FirearmHandsInputTranslator), nameof(FirearmHandsInputTranslator.TranslateCommand)),
            prefix: Hook(typeof(CalibratedSight), nameof(CalibratedSight.TranslatePrefix)));
        Patch(harmony, AccessTools.Method(typeof(Player), nameof(Player.GetRotationMultiplier)),
            postfix: Hook(typeof(ChestStance), nameof(ChestStance.RotationPostfix)));
        Patch(harmony, AccessTools.Method(typeof(Player), "OnItemAddedOrRemoved"),
            postfix: Hook(typeof(NightVisionEyepieces), nameof(NightVisionEyepieces.ItemMovedPostfix)));
        Patch(harmony, AccessTools.Method(typeof(MovementContext), nameof(MovementContext.SetAimingSlowdown)),
            prefix: Hook(typeof(ChestStance), nameof(ChestStance.SlowdownPrefix)));
        Patch(harmony, AccessTools.Method(typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.ProcessEffectors)),
            prefix: Hook(typeof(AimArms), nameof(AimArms.EveryFramePrefix)));
        Patch(harmony, AccessTools.Method(typeof(NewRecoilShotEffect), nameof(NewRecoilShotEffect.FixedUpdate)),
            prefix: Hook(typeof(AimArms), nameof(AimArms.RecoilStepPrefix)));
        Patch(harmony, AccessTools.Method(typeof(OldRecoilShotEffect), nameof(OldRecoilShotEffect.FixedUpdate)),
            prefix: Hook(typeof(AimArms), nameof(AimArms.RecoilStepPrefix)));
        Patch(harmony, AccessTools.Method(typeof(NewRecoilShotEffect), nameof(NewRecoilShotEffect.RecalculateRecoilParamsOnChangeWeapon)),
            postfix: Hook(typeof(RecoilMode), nameof(RecoilMode.RecalculatePostfix), Priority.First));
    }

    private static HarmonyMethod Hook(System.Type owner, string name, int priority = -1)
    {
        return new HarmonyMethod(AccessTools.Method(owner, name)) { priority = priority };
    }

    private static void Patch(Harmony harmony, MethodBase? target, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null,
        HarmonyMethod? transpiler = null)
    {
        string name = (prefix ?? postfix ?? transpiler)!.method.Name;
        if (target == null)
        {
            NewCameraPlugin.Log?.LogWarning($"Not patched ({name}): the game method was not found.");
            return;
        }
        try
        {
            harmony.Patch(target, prefix: prefix, postfix: postfix, transpiler: transpiler);
        }
        catch (System.Exception e)
        {
            NewCameraPlugin.Log?.LogError($"Patching {target.DeclaringType?.Name}.{target.Name} ({name}) failed: {e}");
        }
    }
}
