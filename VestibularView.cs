using EFT;
using EFT.Ballistics;
using UnityEngine;

namespace EFTNewCamera;

// Works like the vestibulo-ocular reflex: the camera stays where the head puts it, and only turns so it keeps
// looking at one point on the line the player means to look along (the mouse look plus free look), the set
// distance ahead of a steady eye. The head's bob and its turn from each step are turned out of view. Yaw and
// pitch are balanced fully; the turn about the view axis (roll) only by the torsion share, as real eyes counter-roll
// only a small part of a head tilt, and only its quick part: a lean (Q, E) is the player's own and stays.
// Mounting, scene guns, vaulting, climbing and the BTR are left as they are. Aiming is balanced as well.
// The point is whatever that line hits, found the way a bullet finds its target.
// While the hands or body are busy (switching or reloading, meds, inventory, looting, doors) the animation turns
// the head on purpose, to look at the hands or the bag. That turn is then part of where the player looks.
internal static class VestibularView
{
    internal static bool Enabled = true;
    internal static float Strength;
    internal static float Torsion = 0.25f;

    // How quickly the view takes on the head turn of an action, and lets it go after.
    private const float ActionFadeSeconds = 0.25f;
    // The head turn kept during an action is smoothed by this much, which takes out most of a step's shake
    // but follows the animation turning the head (about 0.3 s) closely.
    private const float ActionLookSeconds = 0.15f;
    private static float _action;
    private static Quaternion _actionLook = Quaternion.identity;

    private const float NearestDistance = 0.5f;
    private const float FarthestDistance = 100f;
    // How fast the eyes refocus when the line moves onto something nearer or farther, as eyes do in about 0.2 s.
    // Smoothed as 1 / distance, so a wall edge in front of the sky does not swing the point.
    private const float RefocusSeconds = 0.15f;
    private static readonly RaycastHit[] Hits = new RaycastHit[16];
    private static bool _hasFocus;
    private static float _focus;

    private const float FadeSeconds = 0.2f;
    // A roll held longer than about this is the player's own (a lean), not a step's.
    private const float HeldRollSeconds = 0.6f;
    private const float LeanCatchSeconds = 0.15f;
    private static float _heldRoll, _leanTilt, _leanCatchUntil;
    private static int _rollFrame = -10;
    // Two of these in a row separate a step (about two a second) from changing pose.
    private const float StepSeconds = 0.25f;

    private static float _weight;
    private static bool _has;
    private static int _owner;
    private static Vector3 _eyeFirst;
    private static Vector3 _eyeSteady;

    internal static void Reset()
    {
        _has = false;
        _hasFocus = false;
        _weight = 0f;
        _action = 0f;
        _actionLook = Quaternion.identity;
    }

    internal static Quaternion Stabilize(Player player, Vector3 cameraPosition, Quaternion cameraRotation)
    {
        MovementContext? movement = player.MovementContext;
        if (movement == null || !Enabled || Strength <= 0f)
        {
            Reset();
            return cameraRotation;
        }

        // The bones, and the helmet seen at the edge of the view, are posed with the previous movement update's
        // mouse. Balancing to the newest mouse would put the view a frame ahead of the head, and the helmet
        // would step against it whenever the mouse input comes unevenly. The BTR keeps the game's own rotator.
        Quaternion look = player.BtrState == EPlayerBtrState.Outside ? AimArms.PosedLook(player) : player.HandsRotation;
        Quaternion intended = AimArms.FreeLookTurn(player) * look;

        // The head's own turn against that look; kept, smoothed, while an action animation is turning it.
        _actionLook = Quaternion.Slerp(_actionLook, Quaternion.Inverse(intended) * cameraRotation,
            1f - Mathf.Exp(-Time.deltaTime / ActionLookSeconds)).normalized;
        _action = Mathf.MoveTowards(_action, Busy(player) ? 1f : 0f, Time.deltaTime / ActionFadeSeconds);
        if (_action > 0f)
            intended = intended * Quaternion.Slerp(Quaternion.identity, _actionLook, _action * _action * (3f - 2f * _action));
        Vector3 forward = intended * Vector3.forward;
        // The head sits on the body, so its steady place is kept in the body's own turn, not the mouse's.
        Quaternion yawFrame = Quaternion.Euler(0f, player.Transform.rotation.eulerAngles.y, 0f);
        Vector3 root = player.Transform.position;
        Vector3 eye = Quaternion.Inverse(yawFrame) * (cameraPosition - root);

        // The filters run every frame, also while the effect is off, so nothing jumps when it comes back.
        int owner = player.GetInstanceID();
        if (!_has || owner != _owner)
        {
            _eyeFirst = _eyeSteady = eye;
            _owner = owner;
            _has = true;
        }
        else
        {
            float follow = 1f - Mathf.Exp(-Time.deltaTime / StepSeconds);
            _eyeFirst = Vector3.Lerp(_eyeFirst, eye, follow);
            _eyeSteady = Vector3.Lerp(_eyeSteady, _eyeFirst, follow);
        }

        // Aiming too: the aimed gun is laid along the same mouse look the view is balanced to, so the sight stays put.
        bool free = !movement.IsInMountedState
            && !movement.IsStationaryWeaponInHands
            && movement.RotationAction == MovementContext.DefaultRotationFunction
            && player.BtrState == EPlayerBtrState.Outside;
        _weight = Mathf.MoveTowards(_weight, free ? 1f : 0f, Time.deltaTime / FadeSeconds);
        float amount = Strength * _weight * _weight * (3f - 2f * _weight);
        if (amount <= 0.0001f)
        {
            _hasFocus = false;
            return cameraRotation;
        }

        Vector3 eyeSteady = root + yawFrame * _eyeSteady;
        Vector3 target = eyeSteady + forward * Distance(player, eyeSteady, forward);
        Vector3 toTarget = target - cameraPosition;
        if (toTarget.sqrMagnitude < 0.0001f)
            toTarget = forward;
        // The shortest turn from where the camera looks to the point: it has no part about the view axis.
        Quaternion steady = PoseMath.TurnFromTo(cameraRotation * Vector3.forward, toTarget) * cameraRotation;
        if (Torsion > 0f)
        {
            // Only the quick roll, a step's, is countered: a held one is meant (a lean with Q or E), and kept. The held
            // part is the roll smoothed over a while, caught up at once while the lean is changing and a moment after.
            float roll = Roll(Quaternion.Inverse(intended) * steady);
            float tilt = movement.SmoothedTilt;
            if (Mathf.Abs(tilt - _leanTilt) > 0.001f)
                _leanCatchUntil = Time.time + LeanCatchSeconds;
            _leanTilt = tilt;
            if (Time.frameCount - _rollFrame > 1 || Time.time < _leanCatchUntil)
                _heldRoll = roll;
            else
                _heldRoll = Mathf.Lerp(roll, _heldRoll, Mathf.Exp(-Time.deltaTime / HeldRollSeconds));
            _rollFrame = Time.frameCount;
            steady = Quaternion.AngleAxis(-(roll - _heldRoll) * Torsion, steady * Vector3.forward) * steady;
        }
        return Quaternion.Slerp(cameraRotation, steady, amount);
    }

    private static float Distance(Player player, Vector3 eye, Vector3 forward)
    {
        float distance = Look(player, eye, forward);
        if (!_hasFocus)
        {
            _focus = 1f / distance;
            _hasFocus = true;
        }
        else
            _focus += (1f / distance - _focus) * (1f - Mathf.Exp(-Time.deltaTime / RefocusSeconds));
        return 1f / _focus;
    }

    // Distance to the first thing a bullet could hit along the line, leaving out the player's own body.
    private static float Look(Player player, Vector3 eye, Vector3 forward)
    {
        int count = Physics.RaycastNonAlloc(eye, forward, Hits, FarthestDistance, BallisticsCalculatorConstants.HitMask, QueryTriggerInteraction.Ignore);
        Transform self = ((Component)player).transform;
        float nearest = FarthestDistance;
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = Hits[i];
            if (hit.collider == null || hit.distance >= nearest)
                continue;
            if (player.PlayerBones != null && player.PlayerBones.BodyPartCollidersHashSet.Contains(hit.collider))
                continue;
            if (hit.collider.transform.IsChildOf(self))
                continue;
            nearest = hit.distance;
        }
        return Mathf.Max(nearest, NearestDistance);
    }

    // Hands or body in an action whose animation looks somewhere on its own.
    private static bool Busy(Player player)
    {
        try
        {
            Player.AbstractHandsController? hands = player.HandsController as Player.AbstractHandsController;
            if (hands != null && hands.IsInventoryOpen())
                return true;
            if (player.HandsController is Player.MedsController || player.HandsController is Player.QuickUseItemController)
                return true;
            // Sprinting is left out: actions stop a sprint, and the sprint pose may not count as the hands' idle.
            if (player.MovementContext?.CurrentState?.Name != EPlayerState.Sprint
                && player.HandsController is Player.FirearmController firearm && firearm.FirearmsAnimator != null
                && (!firearm.FirearmsAnimator.IsIdling() || firearm.FirearmsAnimator.Animator.IsInTransition(FirearmsAnimator.HANDS_LAYER_INDEX)))
                return true;
        }
        catch (System.Exception)
        {
        }

        switch (player.MovementContext?.CurrentState?.Name)
        {
            case EPlayerState.Loot:
            case EPlayerState.Pickup:
            case EPlayerState.Open:
            case EPlayerState.Close:
            case EPlayerState.Unlock:
            case EPlayerState.BreachDoor:
            case EPlayerState.DoorInteraction:
            case EPlayerState.Approach:
            case EPlayerState.Plant:
                return true;
            default:
                return false;
        }
    }

    // Turn about the view axis, in degrees, of a rotation given against the intended look.
    private static float Roll(Quaternion relative)
    {
        Vector3 forward = relative * Vector3.forward;
        Vector3 up = Quaternion.Inverse(Quaternion.LookRotation(forward)) * (relative * Vector3.up);
        return Mathf.Atan2(-up.x, up.y) * Mathf.Rad2Deg;
    }
}
