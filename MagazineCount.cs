using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Comfort.Common;
using EFT;
using EFT.UI;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace EFTNewCamera;

// Checking a magazine, and the in-raid HUD.
//
// The game shows a magazine's count, and marks the magazine as checked, only in the first-person view (Idling.CheckAmmo),
// and this mod keeps the player in the third-person one, so neither happened. Now both do. The count is the game's own
// panel, with its words, lettering, fading and timing; only its place changes: while it shows a check, its text is kept
// centred on the top round in the magazine, following it through the animation, and it goes back to its corner after.
// A gun with no magazine model to follow (an internal one) leaves it in the corner.
//
// The HUD is hidden (F12) by a transparent group over the battle screen: everything in it goes on working unseen.
internal static class MagazineCount
{
    internal static bool HideUi;

    // Along the round from its base (patron_001's place: the magazine's own z, the round pointing to about z = 0), how
    // far is left to go at the place the text is centred on.
    private const float TowardTip = 0.4f;
    // The panel fades in 0.5 s, stays 3 s and fades out 0.5 s (BattleUIComponentAnimation).
    private const float StartedSeconds = 0.6f, MaxSeconds = 4.6f;

    private static AmmoCountPanel? _panel;
    private static RectTransform? _panelRect;
    private static CanvasGroup? _fading;
    private static Vector2 _corner;
    private static Player? _player;
    private static Player.FirearmController? _hands;
    private static Transform? _round;
    private static float _started;
    private static bool _fromCheck;

    internal static void Install()
    {
        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".magazine");
        MethodInfo? check = AccessTools.Method(typeof(Player.FirearmController.Idling), nameof(Player.FirearmController.Idling.CheckAmmo));
        MethodInfo? show = AccessTools.Method(typeof(GamePlayerOwner), nameof(GamePlayerOwner.PlayerOnOnShowAmmoDetails));
        MethodInfo? panelShow = AccessTools.Method(typeof(AmmoCountPanel), nameof(AmmoCountPanel.Show));
        MethodInfo? screen = AccessTools.Method(typeof(EftBattleUIScreen), "Show", new[] { typeof(GamePlayerOwner) });
        if (check == null || show == null || panelShow == null || screen == null)
        {
            NewCameraPlugin.Log?.LogWarning($"Not patched (magazine count, HUD): check {check != null}, show {show != null}, panel {panelShow != null}, screen {screen != null}.");
            return;
        }
        harmony.Patch(check, transpiler: new HarmonyMethod(AccessTools.Method(typeof(MagazineCount), nameof(CheckAmmoInAnyView))));
        harmony.Patch(show, prefix: new HarmonyMethod(AccessTools.Method(typeof(MagazineCount), nameof(ShowDetailsPrefix))),
            finalizer: new HarmonyMethod(AccessTools.Method(typeof(MagazineCount), nameof(ShowDetailsDone))));
        harmony.Patch(panelShow, prefix: new HarmonyMethod(AccessTools.Method(typeof(MagazineCount), nameof(PanelShowPrefix))));
        harmony.Patch(screen, postfix: new HarmonyMethod(AccessTools.Method(typeof(MagazineCount), nameof(ApplyHideUi))));
    }

    private static IEnumerable<CodeInstruction> CheckAmmoInAnyView(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo getter = AccessTools.PropertyGetter(typeof(Player), nameof(Player.FirstPersonPointOfView));
        MethodInfo ours = AccessTools.Method(typeof(FirstPersonAudio), nameof(FirstPersonAudio.IsEffectiveFirstPerson));
        int replaced = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(getter))
            {
                replaced++;
                yield return new CodeInstruction(OpCodes.Call, ours).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                continue;
            }
            yield return instruction;
        }
        if (replaced == 0)
            NewCameraPlugin.Log?.LogWarning("Magazine count: no point-of-view read in Idling.CheckAmmo.");
    }

    // The game about to show a check's count on its panel.
    private static bool ShowDetailsPrefix(GamePlayerOwner __instance)
    {
        if (!NewCameraSwitch.Enabled)
            return true;
        Player? player = __instance.Player;
        if (player == null || !player.IsYourPlayer)
            return true;
        if (HideUi)
            return false;
        _fromCheck = true;
        Player.FirearmController? hands = player.HandsController as Player.FirearmController;
        Transform? round = hands != null ? FindTopRound(hands) : null;
        AmmoCountPanel? panel = MonoBehaviourSingleton<CommonUI>.Instantiated ? MonoBehaviourSingleton<CommonUI>.Instance.EftBattleUIScreen?._ammoCountPanel : null;
        if (round == null || panel == null)
        {
            StopFollowing();
            return true;
        }
        if (!ReferenceEquals(panel, _panel) || _round == null)
        {
            StopFollowing();
            _panel = panel;
            _panelRect = (RectTransform)panel.transform;
            _corner = _panelRect.anchoredPosition;
            BattleUIComponentAnimation? animation = panel.GetComponent<BattleUIComponentAnimation>();
            _fading = animation != null ? animation._canvasGroup : null;
        }
        _player = player;
        _hands = hands;
        _round = round;
        _started = Time.unscaledTime;
        Canvas? canvas = panel.GetComponentInParent<Canvas>();
        NewCameraPlugin.Log?.LogInfo($"Magazine count: following {round.parent?.name}/{round.name}; panel canvas {canvas?.rootCanvas.renderMode}, parent layout {panel.transform.parent?.GetComponent<UnityEngine.UI.LayoutGroup>() != null}.");
        return true;
    }

    private static System.Exception? ShowDetailsDone(System.Exception? __exception)
    {
        _fromCheck = false;
        return __exception;
    }

    // Anything else the panel is asked to show (the zeroing, a fire mode) stays in its corner.
    private static void PanelShowPrefix()
    {
        if (!_fromCheck)
            StopFollowing();
    }

    // Looked up once per check, under the gun in the hands.
    private static Transform? FindTopRound(Player.FirearmController hands)
    {
        GameObject? gun = hands.ControllerGameObject;
        if (gun == null)
            return null;
        Transform? slot = TransformTools.FindTransformRecursive(gun.transform, "mod_magazine");
        return slot != null ? TransformTools.FindTransformRecursive(slot, "patron_001") : null;
    }

    // Before the FPS camera renders, with the gun and the camera in their places for this frame.
    internal static void OnCameraPreRender(Camera camera)
    {
        if (_panel == null || _player == null)
            return;
        if (camera == null || !camera.CompareTag("MainCamera"))
            return;
        float elapsed = Time.unscaledTime - _started;
        if (_round == null || !NewCameraSwitch.Enabled || !ReferenceEquals(_player.HandsController, _hands) || elapsed >= MaxSeconds
            || (elapsed >= StartedSeconds && (_fading == null || _fading.alpha <= 0.001f)))
        {
            StopFollowing();
            return;
        }

        Transform magazine = _round.parent;
        Vector3 local = magazine.InverseTransformPoint(_round.position);
        Vector3 place = magazine.TransformPoint(new Vector3(local.x, local.y, local.z * TowardTip));
        Vector3 viewport = camera.WorldToViewportPoint(place);
        if (viewport.z <= 0f)
            return;
        Vector2 onScreen = Fisheye.ToScreen(new Vector2(viewport.x, viewport.y));
        Canvas root = _panel.GetComponentInParent<Canvas>().rootCanvas;
        Camera? uiCamera = root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
        if (!RectTransformUtility.ScreenPointToWorldPointInRectangle(_panelRect, new Vector2(onScreen.x * Screen.width, onScreen.y * Screen.height), uiCamera, out Vector3 target))
            return;
        // The panel's text, as drawn, centred there, wherever it sits in the panel.
        _panelRect!.position += target - TextCentre(_panel);
    }

    private static Vector3 TextCentre(AmmoCountPanel panel)
    {
        Vector3 centre = Centre(panel._ammoCount);
        if (panel._ammoDetails != null && panel._ammoDetails.gameObject.activeSelf)
            centre = (centre + Centre(panel._ammoDetails)) * 0.5f;
        return centre;
    }

    private static Vector3 Centre(TextMeshProUGUI text) => text.rectTransform.TransformPoint(text.textBounds.center);

    private static void StopFollowing()
    {
        if (_panelRect != null && _player != null)
            _panelRect.anchoredPosition = _corner;
        _player = null;
        _hands = null;
        _round = null;
    }

    // On the setting and the master switch changing, and each time the battle screen is shown.
    internal static void ApplyHideUi()
    {
        if (!MonoBehaviourSingleton<CommonUI>.Instantiated)
            return;
        EftBattleUIScreen? screen = MonoBehaviourSingleton<CommonUI>.Instance.EftBattleUIScreen;
        if (screen == null)
            return;
        bool hide = HideUi && NewCameraSwitch.Enabled;
        CanvasGroup? group = screen.GetComponent<CanvasGroup>();
        if (group == null)
        {
            if (!hide)
                return;
            group = screen.gameObject.AddComponent<CanvasGroup>();
        }
        group.alpha = hide ? 0f : 1f;
    }
}
