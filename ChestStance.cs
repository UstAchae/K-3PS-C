using EFT;
using EFT.InputSystem;
using HarmonyLib;
using UnityEngine;

namespace EFTNewCamera;

// Aiming with the stock at the middle of the chest (high ready to engagement, centre-chest stock placement), as one
// does with a goggle down and an aiming laser: the gun as aimed, only lower and to the right and canted in, its bore
// along the line of sight; the eye looks over it, not through the sight. One key for both, told apart as games tell a
// tap from a hold: the aim key's press is held back until it is known which it is. Let go within a moment, it was a
// click, and the aim is taken then as the game takes it (its own toggle: a click again ends it); still down after the
// moment, it is a hold, and the aim is taken straight into this stance, which lasts while the key is held and ends the
// aim when it is let go. The game's aim stays on throughout, so the sway, breath and recoil are the aimed ones
// (AimArms places the gun); only its slowdown is lifted, and the mouse turns as when walking.
// With the game's aim key set to hold (its press type Continuous: aimed while held), the two change places: a hold is
// the game's aim, held, and a click takes the stance and keeps it (a click again ends it; a hold turns it into the aim,
// held, and back to the stance on its release; an aim ended by a move of the body ends it). The release the game sends
// after that click, or after that hold, is not let end the aim. With it set to a double click (the game's aim comes on
// the second click's release, and a hold gives the game nothing), a hold takes the stance as with a click: the hold is
// watched here, not held back from the game, and the double click left to the game.
internal static class ChestStance
{
    internal static bool Enabled = true;
    internal static KeyCode Key = KeyCode.Mouse1;  // the aim key as bound in the game (it does not report a release)
    internal static float HoldSeconds = 0.25f;
    internal static float Drop = 0.17f, Ahead = 0f, Side = -0.05f;  // the gun's own origin from the eye's line (m)
    internal static float Cant = 30f;                                   // the gun's turn about its line of sight (degrees, top to the left)

    internal static bool Active { get; private set; }
    // How far into the stance the gun is (0 or 1): kept as it was once the aim ends, so the gun leaves from where it was,
    // and set as the next aim is taken.
    internal static float Weight { get; private set; }
    // The gun's own origin from the eye's line, ahead by the usual aiming distance: lower and to the right.
    internal static Vector3 Offset => new Vector3(Side, -Drop, Ahead);

    private static bool _pending;        // a press of the aim key held back, not yet known to be a click or a hold
    private static bool _held;           // in the stance, the key still down
    private static float _pressTime;
    private static float _retryTime, _leftTime;
    private static bool _fromAim;        // the held-back press came while aimed
    private static bool _heldFromAim;    // the stance now was taken from the aim, and goes back to it
    private static bool _easingBack;
    private static bool _pendingHolds;   // the held-back press came with the game's aim key set to hold
    private static bool _latched;        // in the stance by a click (aim key set to hold), kept until clicked again
    private static bool _peeking;        // held up to the eye from that stance, back to it on the key's release

    private static readonly AccessTools.FieldRef<InputPreset> CurrentPreset = AccessTools.StaticFieldRefAccess<InputPreset>(AccessTools.Field(typeof(InputManager), "_currentPreset"));

    // The game's aim key set to aim while held, not to toggle. Read from the combinations the game works with: the
    // settings' press type is given to copies of the preset's own (InputPreset.UpdateBindings), which keep the default.
    private static EPressType AimType()
    {
        InputCombination[]? working = CurrentPreset()?._workingKeyCombinations;
        if (working == null)
            return EPressType.Press;
        for (int i = 0; i < working.Length; i++)
            if (working[i] is InputKeyCombination key && key.GameKey == EGameKey.Aim)
                return key.Type;
        return EPressType.Press;
    }
    private static bool _pendingWatched;  // a press watched here (aim key set to a double click), not held back
    private const float LeftSeconds = 0.4f;   // past the carry's blend back to the hip
    private const float EnterSeconds = 0.25f; // from the aim into the stance
    private const float RetrySeconds = 0.1f;

    // The aim key's command, before the game takes it: a press that would begin an aim is held back. In the stance (and
    // while a press is held back) the sight is not changed: the aim key is down, and Ctrl with it, meant for Ctrl+T
    // (the tactical device's mode), would otherwise be the game's Ctrl+aim, the next sight.
    internal static bool TranslatePrefix(ref ECommand command, ref InputNode.ETranslateResult __result)
    {
        // Not this mod's to touch while it is not applied (switched off, the free camera): a press held back here is then
        // never let go (Update is not run), and the game's own sight change, Alt or Ctrl with the aim key, stayed blocked.
        if (!NewCameraSwitch.Applied)
            return true;
        if ((Active || _pending) && (command == ECommand.ChangeScope || command == ECommand.ChangeScopeMagnification))
        {
            __result = default;
            return false;
        }
        // The aim key down takes Ctrl for its own Ctrl+aim, and Ctrl+T comes as T alone: with Ctrl down it is Ctrl+T, the
        // tactical device's next mode.
        if ((Active || _pending) && command == ECommand.ToggleTacticalDevice && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
            command = ECommand.NextTacticalDevice;
        // Aim key set to hold: its release, after a click held back or in the stance it took, is not let end the aim.
        if (command == ECommand.EndAlternativeShooting && (_pending && _pendingHolds || _latched))
        {
            __result = default;
            return false;
        }
        if (!Enabled || command != ECommand.ToggleAlternativeShooting || _pending || Active && !_latched)
            return true;
        if (!_latched && AimType() == EPressType.DoubleClick)
            return true;   // the double click's aim, the game's own
        // From the hip a click aims and a hold takes the stance; aimed, a click ends the aim and a hold turns it into the
        // stance.
        if (Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer?.HandsController is not Player.FirearmController firearm)
            return true;
        _fromAim = firearm.IsAiming;
        _pendingHolds = _latched || AimType() == EPressType.Continuous;
        _pendingWatched = false;
        _pending = true;
        _pressTime = Time.time;
        __result = default;
        return false;
    }

    // This mod let go of: nothing held back or kept, as if never pressed.
    internal static void Release()
    {
        _pending = _held = _latched = _peeking = _easingBack = _pendingWatched = Active = false;
        Weight = 0f;
    }

    // Each frame: a held-back press taken as a click or a hold once it is known, and the stance ended when let go.
    internal static void Update(Player player)
    {
        var hands = player.HandsController;
        // Aim key set to a double click: a press of it watched for a hold (the game gives none for it).
        if (Enabled && !_pending && !Active && Input.GetKeyDown(Key) && hands is Player.FirearmController && AimType() == EPressType.DoubleClick)
        {
            _fromAim = hands.IsAiming;
            _pendingHolds = false;
            _pendingWatched = _pending = true;
            _pressTime = Time.time;
        }
        if (_pending)
        {
            if (hands is not Player.FirearmController firearm || hands.IsAiming != _fromAim)
                _pending = false;              // the aim changed under it (a move of the body, a weapon switch)
            else if (!Input.GetKey(Key) && _pendingHolds)
            {
                // A click, the aim key set to hold: the stance taken and kept, or, in it, ended.
                _pending = false;
                if (_latched)
                {
                    _latched = _held = Active = false;
                    _leftTime = Time.time;
                    firearm.SetAim(false);
                }
                else
                {
                    Active = _held = _latched = true;
                    _heldFromAim = false;
                    Weight = 1f;
                    firearm.ToggleAim();
                    if (!firearm.IsAiming)
                        Active = _held = _latched = false;
                }
            }
            else if (Time.time - _pressTime >= HoldSeconds && _pendingHolds)
            {
                // A hold, the aim key set to hold: the game's aim while held (its release ends it); from the stance, the
                // gun eases up to the eye, and back down to the stance on the release.
                _pending = false;
                if (_latched)
                {
                    _held = Active = false;
                    _peeking = _easingBack = true;
                    float buff = 0f;
                    try { buff = Traverse.Create(firearm).Field("_buffInfo").Field("AimMovementSpeed").GetValue<float>(); } catch { }
                    player.MovementContext?.SetAimingSlowdown(true, 0.33f + buff);
                }
                else
                    firearm.ToggleAim();
            }
            else if (!Input.GetKey(Key) && _pendingWatched)
                _pending = false;              // a click, aim key set to a double click: the game's to take
            else if (!Input.GetKey(Key))
            {
                _pending = false;
                if (_fromAim)
                    firearm.SetAim(false);
                else
                {
                    Active = false;
                    Weight = 0f;
                    firearm.ToggleAim();
                }
            }
            else if (Time.time - _pressTime >= HoldSeconds)
            {
                _pending = false;
                Active = true;
                // From the hip the aim's own raise carries the gun there; from the aim it eases over from the eye.
                Weight = _fromAim ? 0f : 1f;
                _held = true;
                _heldFromAim = _fromAim;
                if (_fromAim)
                    player.MovementContext?.SetAimingSlowdown(false, 0f);
                else
                {
                    firearm.ToggleAim();
                    if (!firearm.IsAiming)
                        Active = _held = false;
                }
            }
            return;
        }
        bool aiming = hands != null && hands.IsAiming;
        bool down = Input.GetKey(Key);
        if (_peeking && aiming && !down)
        {
            // Let go after a hold from the stance kept by a click: back down to it, the aim's slowdown off.
            _peeking = _easingBack = false;
            Active = _held = true;
            player.MovementContext?.SetAimingSlowdown(false, 0f);
        }
        if (_held && !down && !_latched)
        {
            _held = false;
            bool was = Active;
            Active = false;
            _leftTime = Time.time;
            if (was && aiming && hands is Player.FirearmController letGo)
            {
                if (_heldFromAim)
                {
                    // Taken from the aim: back to it, the gun easing up to the eye, the aim's slowdown on again.
                    _easingBack = true;
                    float buff = 0f;
                    try { buff = Traverse.Create(letGo).Field("_buffInfo").Field("AimMovementSpeed").GetValue<float>(); } catch { }
                    player.MovementContext?.SetAimingSlowdown(true, 0.33f + buff);
                }
                else
                    letGo.SetAim(false);
            }
            return;
        }
        if (!aiming && _latched && hands is Player.FirearmController walled && walled.AimingInterruptedByOverlap)
        {
            // The gun against a wall: the game lets the aim go and takes it again once clear (FirearmController's
            // overlap); the stance is kept for that, and taken back with the aim, not left as the aim.
            Active = false;
            return;
        }
        if (!aiming && _latched)
        {
            // A stance kept by a click, its aim ended (a move of the body, a weapon switch): ended with it.
            _latched = _peeking = _held = Active = false;
            _leftTime = Time.time;
            return;
        }
        if (!aiming && !_held)
        {
            _easingBack = false;
            // Out of the stance and the aim: the gun has been carried down from it (AimArms keeps its place by the
            // stance's sight while Weight lasts), and then the stance is let go.
            Active = false;
            if (Weight > 0f && Time.time - _leftTime > LeftSeconds)
                Weight = 0f;
            return;
        }
        if (!aiming)
        {
            // Ended by a move of the body (a step up, a vault) while the key is still held: the stance waits, and the aim
            // is taken again once the move lets it (tried now and then, not every frame).
            Active = false;
            if (_held && hands is Player.FirearmController again && Time.time - _retryTime > RetrySeconds)
            {
                _retryTime = Time.time;
                again.ToggleAim();
            }
            return;
        }
        if (_held && !Active)
        {
            Active = true;
            Weight = 1f;
            player.MovementContext?.SetAimingSlowdown(false, 0f);
        }
        else if (!_held && !Active)
        {
            // Easing back to the aim it was taken from; or an aim not taken by a hold, as aimed.
            Weight = _easingBack ? Mathf.Max(0f, Weight - Time.deltaTime / EnterSeconds) : 0f;
            if (Weight <= 0f)
                _easingBack = false;
        }
        if (Active && Weight < 1f)
            Weight = Mathf.Min(1f, Weight + Time.deltaTime / EnterSeconds);
    }

    // The turn that cants the gun Cant degrees from the view's up (top to the left), about the line of sight, whatever
    // the game has turned it to for the sight aimed through: its own up there, as the carry's turn leaves it, put there.
    internal static Quaternion Canted(EFT.Animations.ProceduralWeaponAnimation animation, Quaternion turn, Quaternion view, float weight)
    {
        Vector3 forward = view * Vector3.forward;
        Transform? weapon = animation.HandsContainer?.Weapon;
        if (weapon == null)
            return Quaternion.AngleAxis(Cant * weight, forward) * turn;
        // The gun's own up: forward in its frame, as the game's guns have it, whatever sights it carries.
        Vector3 have = Vector3.ProjectOnPlane(turn * weapon.forward, forward);
        Vector3 want = Quaternion.AngleAxis(Cant, forward) * Vector3.ProjectOnPlane(view * Vector3.up, forward);
        if (have.sqrMagnitude < 1e-8f)
            return turn;
        return Quaternion.AngleAxis(Vector3.SignedAngle(have, want, forward) * weight, forward) * turn;
    }

    // The mouse's turn rate: in the stance as through a red dot or a holographic sight, whatever is aimed through (a
    // magnified sight's slower one is for looking through it): the gun's own aim sensitivity, which every collimator in
    // the game shares (0.65), times the player's aiming sensitivity, as the game works it out (UpdateSensitivity).
    private static readonly AccessTools.FieldRef<Player, System.Func<float>> GetAimingSensitivity = AccessTools.FieldRefAccess<Player, System.Func<float>>("GetAimingSensitivity");

    internal static void RotationPostfix(Player __instance, ref float __result)
    {
        if (!Active || !__instance.IsYourPlayer || __instance.HandsController is not Player.FirearmController { IsAiming: true } firearm)
            return;
        System.Func<float>? aiming = GetAimingSensitivity(__instance);
        if (aiming == null || firearm.Item?.Template == null)
            return;
        __result = Mathf.Lerp(__result, firearm.Item.Template.AimSensitivity * aiming(), Weight);
    }

    // The game's aim slowdown, not put on while in the stance.
    internal static bool SlowdownPrefix(MovementContext __instance, bool __0)
    {
        return !(__0 && Active && __instance == Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer?.MovementContext);
    }
}
