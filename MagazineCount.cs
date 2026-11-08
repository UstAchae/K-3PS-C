using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Comfort.Common;
using EFT;
using EFT.UI;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EFTNewCamera;

// Checking a magazine, and the in-raid HUD.
//
// The game shows a magazine's count, and marks the magazine as checked, only in the first-person view (Idling.CheckAmmo),
// and this mod keeps the player in the third-person one, so neither happened. Now both do, and the count (the game's own
// words for it, by the player's magazine drills) is drawn at the magazine's mouth, the top round's place in its model
// (patron_001), following it through the animation, not at the bottom right of the screen. A gun with no magazine model
// to follow (an internal one) keeps the game's panel.
//
// The HUD is hidden (F12) by a transparent group over the battle screen: everything in it goes on working unseen.
internal static class MagazineCount
{
    internal static bool HideUi;

    private const float FadeInAfter = 0.35f, FadeSeconds = 0.12f;
    private const float MinSeconds = 0.8f, MaxSeconds = 8f;   // the animation is idle until it has started; never left up
    private static readonly Color TextColor = new Color(0.35f, 1f, 0.35f, 1f);

    private static GameObject? _root;
    private static RectTransform? _place;
    private static CanvasGroup? _group;
    private static TextMeshProUGUI? _count, _details;

    private static Player? _player;
    private static Player.FirearmController? _hands;
    private static Transform? _mouth;
    private static float _started, _endedAt = -1f;

    internal static void Install()
    {
        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".magazine");
        MethodInfo? check = AccessTools.Method(typeof(Player.FirearmController.Idling), nameof(Player.FirearmController.Idling.CheckAmmo));
        MethodInfo? show = AccessTools.Method(typeof(GamePlayerOwner), nameof(GamePlayerOwner.PlayerOnOnShowAmmoDetails));
        MethodInfo? screen = AccessTools.Method(typeof(EftBattleUIScreen), "Show", new[] { typeof(GamePlayerOwner) });
        if (check == null || show == null || screen == null)
        {
            NewCameraPlugin.Log?.LogWarning($"Not patched (magazine count, HUD): check {check != null}, show {show != null}, screen {screen != null}.");
            return;
        }
        harmony.Patch(check, transpiler: new HarmonyMethod(AccessTools.Method(typeof(MagazineCount), nameof(CheckAmmoInAnyView))));
        harmony.Patch(show, prefix: new HarmonyMethod(AccessTools.Method(typeof(MagazineCount), nameof(ShowDetailsPrefix))));
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

    private static bool ShowDetailsPrefix(GamePlayerOwner __instance, int ammoCount, int maxAmmoCount, int mastering, string details, bool foldingMechanimWeapon)
    {
        if (!NewCameraSwitch.Enabled)
            return true;
        Player? player = __instance.Player;
        if (player == null || !player.IsYourPlayer)
            return true;
        if (HideUi)
            return false;
        Player.FirearmController? hands = player.HandsController as Player.FirearmController;
        Transform? mouth = hands != null ? FindMouth(hands) : null;
        if (mouth == null || !EnsureLabel())
            return true;

        string message = foldingMechanimWeapon
            ? AmmoCountPanel.GetAmmoCountByLevelForFoldingMechanismWeapon(ammoCount, maxAmmoCount)
            : AmmoCountPanel.GetAmmoCountByLevel(ammoCount, maxAmmoCount, mastering);
        // A number the drills let the player know exactly is given with what the magazine holds.
        _count!.text = int.TryParse(message, out int exact) ? exact + "/" + maxAmmoCount : message;
        bool hasDetails = !string.IsNullOrEmpty(details);
        _details!.gameObject.SetActive(hasDetails);
        if (hasDetails)
            _details.text = details;

        _player = player;
        _hands = hands;
        _mouth = mouth;
        _started = Time.time;
        _endedAt = -1f;
        _group!.alpha = 0f;
        _root!.SetActive(true);
        NewCameraPlugin.Log?.LogInfo($"Magazine count: \"{_count.text}\" at {mouth.parent?.name}/{mouth.name}.");
        return false;
    }

    // Looked up once per check, under the gun in the hands.
    private static Transform? FindMouth(Player.FirearmController hands)
    {
        GameObject? gun = hands.ControllerGameObject;
        if (gun == null)
            return null;
        Transform? slot = TransformTools.FindTransformRecursive(gun.transform, "mod_magazine");
        return slot != null ? TransformTools.FindTransformRecursive(slot, "patron_001") : null;
    }

    private static bool EnsureLabel()
    {
        if (_root != null)
            return true;
        if (!MonoBehaviourSingleton<CommonUI>.Instantiated)
            return false;
        // The game's own lettering, from its ammo panel.
        TextMeshProUGUI? theirs = MonoBehaviourSingleton<CommonUI>.Instance.EftBattleUIScreen?._ammoCountPanel?._ammoCount;
        if (theirs == null || theirs.font == null)
            return false;

        _root = new GameObject("Justachae magazine count");
        Object.DontDestroyOnLoad(_root);
        Canvas canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;
        _group = _root.AddComponent<CanvasGroup>();
        _group.blocksRaycasts = false;
        _group.interactable = false;

        GameObject place = new GameObject("Place", typeof(RectTransform));
        place.transform.SetParent(_root.transform, false);
        _place = (RectTransform)place.transform;
        _place.anchorMin = _place.anchorMax = Vector2.zero;
        _place.pivot = new Vector2(0f, 0.5f);
        _place.sizeDelta = Vector2.zero;
        float scale = Screen.height / 1080f;
        _count = MakeText(place.transform, theirs.font, 30f * scale, 0f);
        _details = MakeText(place.transform, theirs.font, 17f * scale, -30f * scale);
        _root.SetActive(false);
        return true;
    }

    private static TextMeshProUGUI MakeText(Transform parent, TMP_FontAsset font, float size, float y)
    {
        GameObject go = new GameObject("Text", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
        text.font = font;
        text.fontSize = size;
        text.color = TextColor;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.enableWordWrapping = false;
        text.raycastTarget = false;
        text.outlineWidth = 0.15f;
        text.outlineColor = new Color32(0, 0, 0, 200);
        RectTransform rect = text.rectTransform;
        rect.anchorMin = rect.anchorMax = Vector2.zero;
        rect.pivot = new Vector2(0f, 0.5f);
        rect.anchoredPosition = new Vector2(0f, y);
        rect.sizeDelta = new Vector2(600f, 60f);
        return text;
    }

    // Before the FPS camera renders, with the gun and the camera in their places for this frame.
    internal static void OnCameraPreRender(Camera camera)
    {
        if (_mouth == null && _root == null)
            return;
        if (_player == null && (_root == null || !_root.activeSelf))
            return;
        if (camera == null || !camera.CompareTag("MainCamera"))
            return;

        float now = Time.time, elapsed = now - _started;
        bool gone = _mouth == null || _player == null || !NewCameraSwitch.Enabled || HideUi
            || !ReferenceEquals(_player.HandsController, _hands);
        if (gone)
        {
            Stop();
            return;
        }
        if (_endedAt < 0f && elapsed >= MinSeconds && (elapsed >= MaxSeconds || Settled(_hands!)))
            _endedAt = now;
        if (_endedAt >= 0f && now - _endedAt >= FadeSeconds)
        {
            Stop();
            return;
        }

        float alpha = _endedAt >= 0f
            ? 1f - (now - _endedAt) / FadeSeconds
            : Mathf.Clamp01((elapsed - FadeInAfter) / FadeSeconds);
        // In the inventory or a menu the battle screen is off, and so is this.
        bool screen = MonoBehaviourSingleton<CommonUI>.Instantiated
            && MonoBehaviourSingleton<CommonUI>.Instance.EftBattleUIScreen.gameObject.activeInHierarchy;
        Vector3 viewport = camera.WorldToViewportPoint(_mouth!.position);
        if (!screen || viewport.z <= 0f)
            alpha = 0f;
        _group!.alpha = alpha;
        if (alpha <= 0f)
            return;

        Vector2 onScreen = Fisheye.ToScreen(new Vector2(viewport.x, viewport.y));
        float scale = Screen.height / 1080f;
        _place!.anchoredPosition = new Vector2(onScreen.x * Screen.width + 26f * scale, onScreen.y * Screen.height + 10f * scale);
    }

    private static bool Settled(Player.FirearmController hands)
    {
        FirearmsAnimator? animator = hands.FirearmsAnimator;
        return animator == null || (animator.IsIdling() && !animator.Animator.IsInTransition(FirearmsAnimator.HANDS_LAYER_INDEX));
    }

    private static void Stop()
    {
        _player = null;
        _hands = null;
        _mouth = null;
        _endedAt = -1f;
        if (_root != null && _root.activeSelf)
            _root.SetActive(false);
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
