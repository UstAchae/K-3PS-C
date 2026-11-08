using EFT;
using EFT.Hideout;
using EFT.UI;
using HarmonyLib;

namespace EFTNewCamera;

// An area's screen opened while walking about in the hideout (an interaction's option), over the view as it is, as the
// stash's screen opens. The game would go to its overview: the free camera, flown to from the player's place, with the
// player put out of sight until the screen closes. Here only what the screen itself needs is done: the battle screen
// closed, and the player's input ignored. The overview from the hideout's start, with no walking to go back to, is as it was.
internal static class HideoutAreaView
{
    private static readonly AccessTools.FieldRef<GamePlayerOwner, IBattleUIScreenController> BattleScreen =
        AccessTools.FieldRefAccess<GamePlayerOwner, IBattleUIScreenController>("BattleUIScreenController");
    private static readonly AccessTools.FieldRef<HideoutScreenOverlay, HideoutScreenRear> Rear =
        AccessTools.FieldRefAccess<HideoutScreenOverlay, HideoutScreenRear>("_hideoutRear");

    private static bool _fromWalking;
    private static HideoutPlayerOwner? _holding;

    // Over the walking view now: Escape is the screen's, as in the game's overview.
    internal static bool Holding => _holding != null;

    internal static void Install()
    {
        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".hideoutarea");
        harmony.Patch(AccessTools.Method(typeof(HideoutScreenRear), nameof(HideoutScreenRear.EnterHideoutMode)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HideoutAreaView), nameof(EnterPrefix))));
        harmony.Patch(AccessTools.Method(typeof(HideoutPlayerOwner), nameof(HideoutPlayerOwner.SetPointOfView)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HideoutAreaView), nameof(ViewPrefix))));
        harmony.Patch(AccessTools.Method(typeof(HideoutScreenOverlay), nameof(HideoutScreenOverlay.EnterHideoutMode)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HideoutAreaView), nameof(LeavePostfix))));
    }

    private static void EnterPrefix(bool firstPerson, bool returnToFirstPerson)
    {
        _fromWalking = !firstPerson && returnToFirstPerson && NewCameraSwitch.Enabled;
    }

    private static bool ViewPrefix(HideoutPlayerOwner __instance, bool firstPerson)
    {
        if (_holding != null && !ReferenceEquals(_holding, __instance))
            _holding = null;
        if (_holding != null)
        {
            if (!firstPerson)
                return false;
            _holding = null;
            __instance.FirstPersonMode = true;
            BattleScreen(__instance).ShowScreen(EFT.UI.Screens.EScreenState.Queued);
            GamePlayerOwner.SetIgnoreInput(false);
            return false;
        }
        if (firstPerson || !_fromWalking || !__instance.FirstPersonMode)
            return true;
        _fromWalking = false;
        _holding = __instance;
        __instance.FirstPersonMode = false;
        if (!BattleScreen(__instance).Closed)
            BattleScreen(__instance).CloseScreen();
        GamePlayerOwner.SetIgnoreInput(true);
        return false;
    }

    // The game waits out its flight back before it gives the player back; there is none.
    private static void LeavePostfix(HideoutScreenOverlay __instance)
    {
        if (_holding != null)
            Rear(__instance).SetPointOfView(firstPerson: true);
    }
}
