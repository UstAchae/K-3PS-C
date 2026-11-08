using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.UI;
using HarmonyLib;
using UnityEngine;

namespace EFTNewCamera;

// The in-raid HUD's panels hidden one by one (F12). A hidden panel is scaled to nothing: it goes on working unseen, and
// the game, which fades its panels through their transparency and moves none by its scale, does not bring it back.
internal static class HudHiding
{
    internal sealed class Panel
    {
        internal readonly string Key;
        internal readonly string Description;
        internal readonly System.Func<EftBattleUIScreen, Component?> Find;
        internal bool Hidden;

        internal Panel(string key, string description, System.Func<EftBattleUIScreen, Component?> find)
        {
            Key = key;
            Description = description;
            Find = find;
        }
    }

    private static readonly Panel Ammo = new Panel("隐藏弹药和射击模式提示", "验弹、检查枪膛的显示，以及射击模式、归零的提示。", s => s._ammoCountPanel);

    internal static readonly Panel[] Panels =
    {
        new Panel("隐藏血条", "身体各部位的血量面板。", s => s._characterHealthPanel),
        new Panel("隐藏体力和姿态", "左下角的体力条和姿态小人。", s => s._battleStancePanel),
        new Panel("隐藏快捷栏", "快捷栏的物品格子。", s => s._quickAccessPanel),
        new Panel("隐藏交互选项", "开门、搜刮这类选项。看不见选项，操作仍然有效。", s => s._actionPanel),
        Ammo,
        new Panel("隐藏选弹提示", "切换子弹时的提示。", s => s.AmmoSelector),
        new Panel("隐藏瞄具提示", "切换瞄具倍率、模式时的提示。", s => s._opticCratePanel),
        new Panel("隐藏战术装置提示", "切换激光、手电时的提示。", s => s._tacticalInteractionPanel),
        new Panel("隐藏手势面板", "手势和语音的选择面板。", s => s._gesturesPanel),
        new Panel("隐藏指南针", "指南针的方位。", s => s._azimuthPanel),
        new Panel("隐藏时间", "局内时间面板。", s => s._locationTimePanel),
    };

    private static readonly Dictionary<Transform, Vector3> Scales = new();

    internal static bool AmmoHidden => Ammo.Hidden && NewCameraSwitch.Enabled;

    internal static void Install()
    {
        MethodInfo? show = AccessTools.Method(typeof(EftBattleUIScreen), "Show", new[] { typeof(GamePlayerOwner) });
        if (show == null)
        {
            NewCameraPlugin.Log?.LogWarning("Not patched (HUD hiding): the battle screen has no Show.");
            return;
        }
        new Harmony(NewCameraPlugin.PluginGuid + ".hud").Patch(show, postfix: new HarmonyMethod(AccessTools.Method(typeof(HudHiding), nameof(Apply))));
    }

    // On a setting or the master switch changing, and each time the battle screen is shown.
    internal static void Apply()
    {
        if (!MonoBehaviourSingleton<CommonUI>.Instantiated)
            return;
        EftBattleUIScreen? screen = MonoBehaviourSingleton<CommonUI>.Instance.EftBattleUIScreen;
        if (screen == null)
            return;
        foreach (Panel panel in Panels)
        {
            Component? component = panel.Find(screen);
            if (component == null)
                continue;
            Transform transform = component.transform;
            if (panel.Hidden && NewCameraSwitch.Enabled)
            {
                if (!Scales.ContainsKey(transform))
                    Scales[transform] = transform.localScale;
                transform.localScale = Vector3.zero;
            }
            else if (Scales.TryGetValue(transform, out Vector3 scale))
            {
                transform.localScale = scale;
                Scales.Remove(transform);
            }
        }
    }
}
