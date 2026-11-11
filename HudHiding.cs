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
        internal readonly System.Func<EftBattleUIScreen?, Component?> Find;
        internal readonly bool Everywhere;   // not on the battle screen: shown in the menus too, hidden there as well
        internal bool Hidden;

        internal Panel(string key, string description, System.Func<EftBattleUIScreen?, Component?> find, bool everywhere = false)
        {
            Key = key;
            Description = description;
            Find = find;
            Everywhere = everywhere;
        }
    }

    private static readonly Panel Ammo = new Panel("隐藏弹药和射击模式提示", "验弹、检查枪膛的显示，以及射击模式的提示。", s => s?._ammoCountPanel);
    private static readonly Panel Optic = new Panel("隐藏瞄具提示", "切换瞄具倍率、模式时的提示，以及归零距离（比如 M2A1 的距离、AGS 的角度和高度）。", s => s?._opticCratePanel);

    internal static readonly Panel[] Panels =
    {
        new Panel("隐藏血条", "身体各部位的血量面板。", s => s?._characterHealthPanel),
        new Panel("隐藏体力和姿态", "左下角的体力条和姿态小人。", s => s?._battleStancePanel),
        new Panel("隐藏快捷栏", "快捷栏的物品格子，以及换弹时选弹匣的面板（按住 R 滚动滚轮）。", s => s?._quickAccessPanel),
        new Panel("隐藏交互选项", "开门、搜刮这类选项。看不见选项，操作仍然有效。", s => s?._actionPanel),
        Ammo,
        Optic,
        new Panel("隐藏战术装置提示", "切换激光、手电时的提示。", s => s?._tacticalInteractionPanel),
        new Panel("隐藏手势面板", "手势和语音的选择面板。", s => s?._gesturesPanel),
        new Panel("隐藏时间", "局内时间面板。", s => s?._locationTimePanel),
        // The corner label the game writes its version in (SPT writes its own there).
        new Panel("隐藏版本号", "左下角的版本号（SPT 和游戏的版本）。它在主菜单里也显示，打开后那里也隐藏。",
            _ => MonoBehaviourSingleton<PreloaderUI>.Instantiated ? MonoBehaviourSingleton<PreloaderUI>.Instance._alphaVersionLabel : null, everywhere: true),
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
        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".hud");
        harmony.Patch(show, postfix: new HarmonyMethod(AccessTools.Method(typeof(HudHiding), nameof(Apply))));
        // The version's label is made with the loading screen's UI, before any setting is applied.
        MethodInfo? preloader = AccessTools.Method(typeof(PreloaderUI), "Awake");
        if (preloader != null)
            harmony.Patch(preloader, postfix: new HarmonyMethod(AccessTools.Method(typeof(HudHiding), nameof(Apply))));
        else
            NewCameraPlugin.Log?.LogWarning("Not patched (HUD hiding): the loading UI has no Awake; the version label is hidden from the next setting change.");
        // The range shares the ammo's panel on screen, but it is the sight's: it is stopped on its way from the player. A
        // chamber check's name goes to that panel directly.
        harmony.Patch(AccessTools.Method(typeof(GamePlayerOwner), nameof(GamePlayerOwner.OnShowAmmoCountZeroingPanel)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HudHiding), nameof(RangePrefix))));
    }

    private static bool RangePrefix()
    {
        return !(Optic.Hidden && NewCameraSwitch.Enabled);
    }

    // On a setting or the master switch changing, and each time the battle screen is shown.
    internal static void Apply()
    {
        EftBattleUIScreen? screen = MonoBehaviourSingleton<CommonUI>.Instantiated ? MonoBehaviourSingleton<CommonUI>.Instance.EftBattleUIScreen : null;
        foreach (Panel panel in Panels)
        {
            if (screen == null && !panel.Everywhere)
                continue;
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
