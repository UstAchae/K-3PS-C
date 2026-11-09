using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Comfort.Common;
using EFT;
using EFT.Animations;
using EFT.InventoryLogic;
using EFT.Settings;
using HarmonyLib;
using UnityEngine;
using static EFTNewCamera.PoseMath;

namespace EFTNewCamera;

internal static class AimArms
{
    private const float BlendSeconds = 0.2f;
    // Leaving an aim (as aimed or at the chest) to the hip: as long as the game's own lowering of the gun takes.
    private const float LeaveSeconds = 0.35f;
    private const float MeanSeconds = 1f;
    private const float AimInSeconds = 0.7f;
    private const float AimInMeanSeconds = 0.08f;
    private const float LeanCatchSeconds = 0.15f;
    private static float _leanTilt;
    private static float _leanCatchUntil;
    private const float GunSmoothSeconds = 0.04f;
    private const float HeadSmoothSeconds = 0.05f;
    private const float ReleaseSeconds = 0.3f;
    private const float HeadLockSeconds = 0.2f;

    internal static float ForwardDistance;
    internal sealed class SwayAmounts
    {
        public float GunMove;
        public float GunTurn;
        public float ArmMove;
        public float ArmTurn;
        public float HeadFollow;
        public float Breath;
    }

    internal static readonly SwayAmounts Normal = new() { GunMove = 0.05f, GunTurn = 0.05f, ArmMove = 0.05f, ArmTurn = 0.05f, HeadFollow = 0.1f, Breath = 1f };
    internal static readonly SwayAmounts Holding = new();
    internal static readonly SwayAmounts Exhausted = new() { GunMove = 1f, GunTurn = 1f, ArmMove = 1f, ArmTurn = 1f, HeadFollow = 1f, Breath = 1f };
    private const float BreathCenterSeconds = 6f;
    private static readonly SwayAmounts Now = new() { GunMove = 0.05f, GunTurn = 0.05f, ArmMove = 0.05f, ArmTurn = 0.05f, HeadFollow = 0.1f, Breath = 1f };
    private const float FollowDelay = 0.1f;   // seconds the gun trails the view when swinging the mouse
    internal static float WalkHeadSway;
    internal static float HeadModelPitch;
    private const float MaxDelayDegrees = 15f;

    private static bool _suppress;
    private static bool _blendAiming;
    private static int _blendSightId;
    private static float _blend = 1f;
    private static float _mountBlend;
    private static Vector3 _offset;
    // The gun part as drawn (against the camera it was placed for), and in which frame.
    private static Transform? _drawnPart;
    private static int _drawnFrame = -10;
    private static Vector3 _drawnSeen;
    private static Quaternion _drawnTurn = Quaternion.identity;

    private static void Remember(Transform? part, Vector3 cameraPosition, Quaternion cameraRotation, Vector3 pivot, Vector3 displacement, Quaternion turn)
    {
        _drawnPart = part;
        _drawnFrame = Time.frameCount;
        if (part == null)
            return;
        Quaternion back = Quaternion.Inverse(cameraRotation);
        _drawnSeen = back * (pivot + displacement + turn * (part.position - pivot) - cameraPosition);
        _drawnTurn = back * (turn * part.rotation);
    }

    private static Vector3 _blendFrom;
    private static bool _blendSeen;
    private static Vector3 _seenFrom;
    private static Quaternion _turn = Quaternion.identity;
    private static Quaternion _turnFrom = Quaternion.identity;
    private static Quaternion _viewTurnFrom = Quaternion.identity;
    private static Transform? _viewTurnPart;
    private static bool _hasMean;
    private static int _meanSightId;
    private static Vector3 _armMoveMean;
    private static Vector3 _gunMoveMean;
    private static Quaternion _armTurnMean = Quaternion.identity;
    private static Quaternion _gunTurnMean = Quaternion.identity;
    private static float _meanStart;
    private static Quaternion _armTurnCenter = Quaternion.identity;
    private static Quaternion _gunTurnCenter = Quaternion.identity;
    private static float _sightCatch;
    private static Quaternion _lagLook = Quaternion.identity;
    private static Vector3 _gunMoveSmooth;
    private static Quaternion _gunTurnSmooth = Quaternion.identity;
    private static bool _hasChase;
    private static Quaternion _chaseTurn = Quaternion.identity;
    private static bool _handAction;
    private static bool _chaseCatching;
    private static float _handActionStart;
    private static float _chaseCatchStart;
    private const float ChaseCatchSeconds = 0.4f;
    private static float _handActionMin;
    private const float BoltMinSeconds = 0.3f;
    private const float FireModeMinSeconds = 0.5f;
    private const float HandActionMaxSeconds = 2.5f;
    private static Vector3 _chase;
    private static bool _hasHeadMean;
    private static bool _hasCenter;
    private const float StateBlendSeconds = 0.3f;
    private static float _holdWeight;
    private static float _tiredWeight;
    private static int _turnFrame = -1;
    private static int _rotateHeadFrame = -1;
    private static Quaternion _headTurnRaw = Quaternion.identity;
    private static bool _hasYawCenter;
    private static Quaternion _lookCenter = Quaternion.identity;
    private static Quaternion _headTurnSmooth = Quaternion.identity;
    private static float _headLock;
    private static bool _hasObserved;
    private static Quaternion _smoothLook = Quaternion.identity;
    // The mouse the body bones were posed with. The body animator poses from what the previous frame's
    // movement update handed it, so the bones trail MovementContext.Rotation by one movement update.
    private static Quaternion _bodyLook = Quaternion.identity;
    private static Vector2 _posedMouse;
    private static int _posedFrame = -1;
    private static Vector2 _previousPosedMouse;
    private static int _previousPosedFrame = -1;
    private static Vector3 _neckPosition;
    private static Vector3 _headPositionNow;
    private static Vector3 _heldHeadPosition;
    private static Quaternion _heldHeadTurn = Quaternion.identity;
    private static float _release;
    private static Vector3 _smoothedHead;
    private static int _headSmoothFrame = -1;
    // How the gun was carried this frame (about the pivot), which the optic camera, the lasers and the shot take too.
    private static bool _hasCarry;
    private static Vector3 _carryDisplacement;
    private static Vector3 _carryPivot;
    private static Quaternion _carryTurn = Quaternion.identity;
    private static int _opticFrame = -1;
    private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, float> LineOfSightAngle =
        AccessTools.FieldRefAccess<ProceduralWeaponAnimation, float>("_currentLineOfSightDeltaAngle");
    internal static float RecoilKick = 0.25f;
    private static bool _mounted;
    private static Quaternion _viewKick = Quaternion.identity;
    private static int _shotFrame = -1;
    private static Vector3? _shotFireport;

    // The movement update hands Aim_angle / DeltaRotation to the body animator. Measured in game, the
    // bones seen this frame follow the value handed over in the previous frame, so both are kept.
    internal static void MovementUpdatePostfix(Player __instance)
    {
        if (!NewCameraSwitch.Enabled || !__instance.IsYourPlayer || __instance.MovementContext == null)
            return;

        if (_posedFrame != Time.frameCount)
        {
            _previousPosedMouse = _posedMouse;
            _previousPosedFrame = _posedFrame;
        }
        _posedMouse = __instance.MovementContext.Rotation;
        _posedFrame = Time.frameCount;
    }

    // Third-person VisualPass only places the weapon (sprint pose included) while the body
    // mesh is on screen. Looking up from the head camera takes the body out of view.
    internal static void VisiblePostfix(Player __instance, ref bool __result)
    {
        if (!__result && NewCameraSwitch.Enabled && __instance.IsYourPlayer)
            __result = true;
    }

    // Leaving a scene gun (AGS, mounted machine gun) sets the procedural animation strategy from the point of
    // view the player was created with, which is first person. In third person that puts first-person weapon
    // placement and FOV scaling on the third-person rig, and the gun floats off the arms. The player's current
    // point of view is used instead.
    // The game steps the hands' springs only on frames that had a physics tick (about 60 a second): its first-person
    // camera hangs on the same springs and steps with them, but ours follows the head every frame, so above 60 fps
    // the gun moved on every other frame against the view. Our player's springs are stepped every frame, by the
    // frame's own time, instead.
    // Not the shot's recoil (RecoilStepPrefix): its springs take no account of the time a step stands for
    // (RecoilProcessBase.CalculateRecoil), so stepped every frame they played the faster the higher the frame rate
    // (2.4 times at 144 fps). They step as many times as the game's own physics ticks, as in 1.1.1.
    internal static void EveryFramePrefix(ProceduralWeaponAnimation __instance, ref float deltaTime, ref int nFixedFrames)
    {
        if (!NewCameraSwitch.Enabled || nFixedFrames < 0 || Time.deltaTime <= 0f)
            return;
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !ReferenceEquals(player.ProceduralWeaponAnimation, __instance))
            return;
        _recoilStepFor = __instance.Shootingg?.CurrentRecoilEffect;
        _recoilSteps = nFixedFrames;
        _recoilStepTime = nFixedFrames > 0 ? deltaTime / nFixedFrames : 0f;
        deltaTime = Time.deltaTime;
        nFixedFrames = 1;
        CalibratedSight.EveryFrame(__instance);
    }

    private static object? _recoilStepFor;
    private static int _recoilSteps;
    private static float _recoilStepTime;
    private static bool _steppingRecoil;

    // NewRecoilShotEffect / OldRecoilShotEffect.FixedUpdate: the one step the frame's springs give the recoil is
    // taken as the game's own steps of that frame (none on a frame without a physics tick).
    internal static bool RecoilStepPrefix(object __instance)
    {
        if (_steppingRecoil || _recoilStepFor == null || !ReferenceEquals(__instance, _recoilStepFor))
            return true;
        _recoilStepFor = null;
        if (__instance is not EFT.Animations.Recoil.IRecoilShotEffect effect)
            return true;
        _steppingRecoil = true;
        try
        {
            for (int i = 0; i < _recoilSteps; i++)
                effect.FixedUpdate(_recoilStepTime);
        }
        finally
        {
            _steppingRecoil = false;
        }
        return false;
    }

    // The third-person procedural animation leaves out the breath and the hands' shake that sway the gun in first
    // person; with the first-person skeleton they are put back, as the first-person procedural animation runs them
    // (into the hands' springs, which the third-person one then steps on).
    internal static void BreatheAsFirstPerson(ProceduralWeaponAnimation pwa, float deltaTime, int nFixedFrames)
    {
        if (!NewCameraSwitch.Enabled || !NewCameraSwitch.FirstPersonSkeleton || nFixedFrames <= 0 || pwa.HandsContainer?.WeaponRootAnim == null
            || !pwa.enabled || Mathf.Approximately(deltaTime, 0f))
            return;
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !player.IsYourPlayer || !ReferenceEquals(player.ProceduralWeaponAnimation, pwa))
            return;
        float step = deltaTime / nFixedFrames;
        for (int i = 0; i < nFixedFrames; i++)
        {
            if ((pwa.Mask & EProceduralAnimationMask.Breathing) != 0)
                pwa.Breath.Process(step);
            if ((pwa.Mask & EProceduralAnimationMask.HandShake) != 0)
                pwa.HandShakeEffector.Process(step);
        }
    }

    internal static void SetStrategyPrefix(ProceduralWeaponAnimation __instance, ref EPointOfView pointOfView)
    {
        if (!NewCameraSwitch.Enabled)
            return;
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player != null && player.IsYourPlayer && ReferenceEquals(player.ProceduralWeaponAnimation, __instance)
            && player.PointOfView == EPointOfView.ThirdPerson)
            pointOfView = EPointOfView.ThirdPerson;
    }

    internal static void Apply(Player player)
    {
        _suppress = true;
        player.MovementContext?.PlayerAnimator?.SetAiming(false);
    }

    internal static void AlignToCamera(Player player, Vector3 cameraPosition, Quaternion cameraRotation)
    {
        AlignToCameraCore(player, cameraPosition, cameraRotation);
    }

    private static void AlignToCameraCore(Player player, Vector3 cameraPosition, Quaternion cameraRotation)
    {
        _mounted = player.MovementContext != null && player.MovementContext.IsInMountedState;
        UpdateHandAction(player);
        PlayerBones bones = player.PlayerBones;
        bool aiming = player.HandsController != null && player.HandsController.IsAiming;
        int sightId = 0;
        Vector3 target = Vector3.zero;
        Quaternion turn = Quaternion.identity;
        Vector3? sightPoint = null;
        Transform? gunPart = bones?.Weapon_Root_Anim;
        if (bones != null)
        {
            ProceduralWeaponAnimation? animation = player.ProceduralWeaponAnimation;
            Transform? bone = animation != null && animation.ScopeAimTransforms != null && animation.ScopeAimTransforms.Count > 0
                ? Scopes.Current(animation)?.Bone
                : null;
            // The stock at the chest: placed by an upright sight, not by the one aimed through (a canted one sits off to the side).
            if (bone != null && ChestStance.Weight > 0f && ChestStance.UprightSight(animation!) is Transform upright)
                bone = upright;
            Vector3 sightPosition = bone != null ? bone.position : Vector3.zero;
            Vector3 sightForward = Vector3.forward;
            bool ironAligned = bone != null && animation != null && ResolveIronSight(animation, bone, out sightPosition, out sightForward);
            // The rangefinder: its lens is the aim point, whatever the game's aim bone does.
            bool rangefinder = RangefinderSight.TryGet(player, out Transform rangefinderLens, out Transform rangefinderBody, out float rangefinderRelief);
            if (rangefinder)
            {
                bone = rangefinderLens;
                sightPosition = bone.position;
                sightForward = RangefinderSight.Forward(rangefinderBody);
                ironAligned = true;
            }
            if (bone != null)
            {
                sightPoint = sightPosition;
                gunPart = bone;
            }
            if (PlantedAim(player))
            {
                // The mounted hip pose is already the aim pose. Leave the gun there.
                _offset = Vector3.zero;
                _turn = Quaternion.identity;
                _blend = 1f;
                _blendAiming = true;
                _blendSightId = 0;
                _hasCarry = false;
                _viewKick = Quaternion.identity;
                return;
            }
            if (aiming && bone != null && animation != null)
            {
                sightId = bone.GetInstanceID();
                float relief = rangefinder ? rangefinderRelief - ForwardDistance : SightRelief(animation, bone);
                // From the aiming eye: the camera, or where the eye would be at the chest, where the camera stays.
                Vector3 aimPoint = new Vector3(CameraPosition.PoseAcross, 0f, ForwardDistance + relief);
                // The stock at the chest (ChestStance): the gun as aimed, lower and to the right of the eye.
                float chest = ChestStance.Weight * ChestStance.Weight * (3f - 2f * ChestStance.Weight);
                if (chest > 0f)
                    aimPoint += ChestStance.Offset * chest;
                Transform? root = bones.Weapon_Root_Anim;
                SteadyHead(player, cameraPosition, cameraRotation, out Vector3 steadyPosition, out Quaternion steadyRotation);
                Quaternion bodyRotation = _bodyLook * Quaternion.Inverse(_smoothLook) * steadyRotation;
                Vector3 bodyPosition = CameraPosition.Resolve(steadyPosition - CameraPosition.Resolve(Vector3.zero, steadyRotation), bodyRotation);
                if (bones.Weapon_Root_Third != null && root != null)
                    Sway(animation, bones.Weapon_Root_Third, root, bone, sightPosition, sightForward, ironAligned, sightId, bodyPosition, bodyRotation, aimPoint, out target, out turn);
                else
                {
                    target = bodyPosition - sightPosition + bodyRotation * aimPoint;
                    if (ironAligned)
                        turn = TurnFromTo(sightForward, bodyRotation * Vector3.forward);
                }

                if (rangefinder)
                    turn = RangefinderSight.Level(turn, rangefinderBody, bodyRotation);

                // Canted in about the line of sight, through the sight (the carry's pivot), by the same angle whatever sight
                // the game has turned the gun for.
                if (chest > 0f)
                    turn = ChestStance.Canted(animation, turn, bodyRotation, chest);
                Quaternion delay = _lagLook * Quaternion.Inverse(_bodyLook);
                Vector3 placed = sightPosition + target;
                target = steadyPosition + delay * (placed - bodyPosition) - sightPosition;
                turn = delay * turn;
            }
            else if (!aiming && FollowDelay > 0f && player.HandsController is Player.FirearmController && bones.Weapon_Root_Anim != null)
            {
                Quaternion delay = _lagLook * Quaternion.Inverse(_smoothLook);
                if (Settled(delay))
                    delay = Quaternion.identity;
                sightPoint = cameraPosition;
                turn = delay;
            }
        }

        if (!aiming)
        {
            _hasMean = false;
            _hasHeadMean = false;
        }

        if (!aiming && !_blendAiming && _offset.sqrMagnitude < 0.00000001f && Settled(_turn) && Settled(turn) && _blend >= 1f)
        {
            _hasCarry = false;
            Remember(gunPart, cameraPosition, cameraRotation, Vector3.zero, Vector3.zero, Quaternion.identity);
            _viewKick = _mounted && bones?.Weapon_Root_Anim != null
                ? ShotKick(player.ProceduralWeaponAnimation, bones.Weapon_Root_Anim.rotation)
                : Quaternion.identity;
            return;
        }

        if (bones == null)
        {
            _hasCarry = false;
            _viewKick = Quaternion.identity;
            return;
        }

        Vector3 pivotNow = sightPoint ?? (bones.Weapon_Root_Anim != null ? bones.Weapon_Root_Anim.position : Vector3.zero);
        Vector3? seenPivot = null;
        if (aiming != _blendAiming || sightId != _blendSightId)
        {
            _blendFrom = _offset;
            // Into or out of the stance at the chest: the gun goes along a straight line as seen, whatever the game's
            // own aim animation does under it (leaving, it starts from the eye, and would carry the gun up first). Where
            // the part was seen as it was last carried.
            _blendSeen = ChestStance.Weight > 0f && gunPart != null;
            // As it was drawn last frame, against that frame's camera: last frame's carry applied to this frame's bones
            // would put it off by however far the body moved in between, turned by the carry's turn (the stance's cant),
            // and the gun would start the blend with a jump while walking.
            bool drawnLast = gunPart != null && ReferenceEquals(gunPart, _drawnPart) && _drawnFrame == Time.frameCount - 1;
            _seenFrom = drawnLast
                ? _drawnSeen
                : gunPart != null
                    ? Quaternion.Inverse(cameraRotation) * (_carryPivot + _turn * (gunPart.position - _carryPivot) + _offset - cameraPosition)
                    : Vector3.zero;
            _turnFrom = _turn;
            _viewTurnFrom = drawnLast
                ? _drawnTurn
                : gunPart != null ? Quaternion.Inverse(cameraRotation) * (_turn * gunPart.rotation) : Quaternion.identity;
            _viewTurnPart = gunPart;
            _blend = 0f;
            _blendAiming = aiming;
            _blendSightId = sightId;
        }

        if (_blend < 1f)
        {
            // Out of the stance to the hip, over as long as the game's own lowering of the gun takes, which would
            // otherwise go on moving it once the blend was done.
            _blend = Mathf.Min(1f, _blend + Time.deltaTime / (!aiming ? LeaveSeconds : BlendSeconds));
            float t = _blend * _blend * (3f - 2f * _blend);
            // The gun turns, as seen, along the same curve: from how it lay in view when the blend began to how it lies
            // once there. The game's own aim-in turns it in steps (the first-person animation turns it fast and stops
            // halfway), which would otherwise show as the gun halting mid-way.
            if (gunPart != null && ReferenceEquals(gunPart, _viewTurnPart))
            {
                Quaternion there = Quaternion.Inverse(cameraRotation) * (turn * gunPart.rotation);
                _turn = cameraRotation * Quaternion.Slerp(_viewTurnFrom, there, t) * Quaternion.Inverse(gunPart.rotation);
            }
            else
                _turn = Quaternion.Slerp(_turnFrom, turn, t);
            if (_blendSeen && gunPart != null && ReferenceEquals(gunPart, _viewTurnPart))
            {
                // The part where it is seen along the line, the gun turned about the part itself all the while (the
                // pivot the stance had): the hip's pivot, the camera, would swing the still-turned gun about as it came.
                // Where the hip will have it: about its own pivot.
                Vector3 part = gunPart.position;
                Vector3 there = pivotNow + turn * (part - pivotNow) + target;
                Vector3 seen = Vector3.Lerp(_seenFrom, Quaternion.Inverse(cameraRotation) * (there - cameraPosition), t);
                _offset = cameraPosition + cameraRotation * seen - part;
                seenPivot = part;
            }
            else
                _offset = Vector3.Lerp(_blendFrom, target, t);
        }
        else
        {
            _offset = target;
            _turn = turn;
        }

        Vector3 displacement = _offset;
        Quaternion carryTurn = _turn;
        Vector3 pivot = seenPivot ?? sightPoint ?? (bones.Weapon_Root_Anim != null ? bones.Weapon_Root_Anim.position : Vector3.zero);
        Quaternion kick = Quaternion.identity;
        if (aiming && !_mounted)
            kick = Recoil(player.ProceduralWeaponAnimation, bones.Weapon_Root_Anim, pivot, ref displacement, ref carryTurn);
        else if (!aiming && _mounted && bones.Weapon_Root_Anim != null)
            kick = ShotKick(player.ProceduralWeaponAnimation, bones.Weapon_Root_Anim.rotation);
        _viewKick = !aiming && _mounted ? kick : Quaternion.identity;
        _carryDisplacement = displacement;
        _carryPivot = pivot;
        _carryTurn = carryTurn;
        _hasCarry = displacement.sqrMagnitude >= 0.00000001f || !Settled(carryTurn);
        Remember(gunPart, cameraPosition, cameraRotation, pivot, displacement, carryTurn);
        BodySkins.EnsureUpperarms(player);
        for (int i = BodySkins.Upperarms.Count - 1; i >= 0; i--)
        {
            Transform arm = BodySkins.Upperarms[i];
            if (arm == null)
            {
                BodySkins.Upperarms.RemoveAt(i);
                continue;
            }

            for (int j = 0; j < BodySkins.Upperarms.Count; j++)
            {
                Transform other = BodySkins.Upperarms[j];
                if (i != j && other != null && arm.IsChildOf(other))
                {
                    BodySkins.Upperarms.RemoveAt(i);
                    break;
                }
            }
        }

        // The upper arms stay in their shoulders. Only the hands go where the carried gun puts them, and each
        // arm bends to reach (two-bone IK). Carrying the whole arm pulled the upper arm out of its shoulder, so
        // one shoulder bulged and the other sank in.
        Transform? weapon = bones.Weapon_Root_Anim;
        Vector3 weaponPosition = weapon != null ? pivot + displacement + carryTurn * (weapon.position - pivot) : Vector3.zero;
        Quaternion weaponRotation = weapon != null ? carryTurn * weapon.rotation : Quaternion.identity;
        Vector3 aimPivot = pivot;
        Vector3 aimDisplacement = displacement;
        Quaternion aimTurn = carryTurn;
        System.Func<Vector3, Vector3> carried = point => aimPivot + aimDisplacement + aimTurn * (point - aimPivot);
        LimbIk.GripArms(bones, BodySkins.Upperarms, null, carried, carryTurn);
        foreach (Transform arm in BodySkins.Upperarms)
        {
            if (arm != null && !LimbIk.Reaching(arm))
                Carry(arm, pivot, displacement, carryTurn);
        }
        LimbIk.Reach(carried);

        if (weapon == null)
            return;
        weapon.SetPositionAndRotation(weaponPosition, weaponRotation);
        _shotFrame = Time.frameCount;
        _shotFireport = player.HandsController is Player.FirearmController firearm && firearm.CurrentFireport?.Original != null
            ? firearm.CurrentFireport.Original.position
            : (Vector3?)null;
    }

    private static void StartHandAction(float minSeconds)
    {
        _handAction = true;
        _handActionStart = Time.time;
        _handActionMin = minSeconds;
    }

    private static bool IsMainFirearmsAnimator(FirearmsAnimator animator)
    {
        return Singleton<GameWorld>.Instance?.MainPlayer?.HandsController is Player.FirearmController firearm
            && ReferenceEquals(firearm.FirearmsAnimator, animator);
    }

    internal static void FireModePostfix(FirearmsAnimator __instance, bool skipAnimation)
    {
        if (NewCameraSwitch.Enabled && !skipAnimation && IsMainFirearmsAnimator(__instance))
            StartHandAction(FireModeMinSeconds);
    }

    internal static void FireModeCheckPostfix(FirearmsAnimator __instance)
    {
        if (NewCameraSwitch.Enabled && IsMainFirearmsAnimator(__instance))
            StartHandAction(FireModeMinSeconds);
    }

    // The action lasts until the hands layer is back in idle, but at least long enough for the
    // trigger to reach the animator.
    private static void UpdateHandAction(Player player)
    {
        if (!_handAction)
            return;

        float elapsed = Time.time - _handActionStart;
        bool idle = true;
        try
        {
            FirearmsAnimator? animator = (player.HandsController as Player.FirearmController)?.FirearmsAnimator;
            if (animator != null)
                idle = animator.IsIdling() && !animator.Animator.IsInTransition(FirearmsAnimator.HANDS_LAYER_INDEX);
        }
        catch
        {
        }
        if (elapsed >= HandActionMaxSeconds || (elapsed >= _handActionMin && idle))
            _handAction = false;
    }

    internal static void AdjustShotPrefix(Player.FirearmController __instance, ref Vector3 position, ref Vector3 direction)
    {
        if (NewCameraSwitch.Enabled && __instance.Item is Weapon { BoltAction: true }
            && ReferenceEquals(Singleton<GameWorld>.Instance?.MainPlayer?.HandsController, __instance))
            StartHandAction(BoltMinSeconds);
        if (!NewCameraSwitch.Enabled)
            return;
        if (!_hasCarry || Time.frameCount - _shotFrame > 2)
            return;
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !ReferenceEquals(player.HandsController, __instance))
            return;
        if (_shotFireport.HasValue && (__instance.CurrentFireport?.Original?.position - _shotFireport.Value)?.sqrMagnitude < 0.000001f)
            return;

        // The game's own pose of the gun, then the carry, which was taken from the bones as they stood.
        position = _carryPivot + _carryDisplacement + _carryTurn * (position - _carryPivot);
        direction = _carryTurn * direction;
    }





    internal static void CarryOptic(Camera camera)
    {
        if (!_hasCarry || _opticFrame == Time.frameCount)
            return;

        _opticFrame = Time.frameCount;
        // The game placed the sight's camera from the gun as it posed it; the carry moves it with the gun.
        CarryWithGun(camera.transform);
    }

    internal static bool GunCarried => _hasCarry;

    internal static void CarryWithGun(Transform transform)
    {
        Carry(transform, _carryPivot, _carryDisplacement, _carryTurn);
    }

    // The gun left where the game put it (not aimed, or back from the aim).
    internal static bool GunAtRest => _offset.sqrMagnitude < 0.00000001f && Settled(_turn);

    private static Quaternion Recoil(ProceduralWeaponAnimation? animation, Transform? weapon, Vector3 pivot, ref Vector3 displacement, ref Quaternion turn)
    {
        if (animation == null || weapon == null || animation.HandsContainer == null)
            return Quaternion.identity;

        Quaternion weaponRotation = turn * weapon.rotation;
        Quaternion kick = ShotKick(animation, weaponRotation);
        if (Settled(kick))
            return Quaternion.identity;

        Vector3 weaponPosition = pivot + displacement + turn * (weapon.position - pivot);
        Vector3 localPivot = animation.HandsContainer.RecoilPivot;
        if (animation.IsMountedState)
            localPivot = animation.IsBipodUsed && !animation.IsVerticalMounting
                ? animation.HandsContainer.MountingRotationCenterBipods
                : animation.HandsContainer.MountingRotationCenter;
        Vector3 recoilPivot = weaponPosition + weaponRotation * localPivot;
        displacement = recoilPivot + kick * (pivot + displacement - recoilPivot) - pivot;
        turn = kick * turn;
        return kick;
    }

    private static Quaternion ShotKick(ProceduralWeaponAnimation? animation, Quaternion space)
    {
        if (animation == null || animation.Shootingg?.CurrentRecoilEffect == null || RecoilKick <= 0f)
            return Quaternion.identity;

        Vector3 recoil = animation.Shootingg.CurrentRecoilEffect.GetHandRotationRecoil();
        if (animation.Shootingg.CurrentRecoilEffect.WeaponRecoilEffect != null)
            recoil += animation.Shootingg.CurrentRecoilEffect.WeaponRecoilEffect.GetRotationRecoil();
        if (recoil.sqrMagnitude < 0.00000001f)
            return Quaternion.identity;

        return Quaternion.Euler(space * (recoil * RecoilKick));
    }

    // A bipod and a scene gun (the mounted machine gun or grenade launcher) both stay where they are.
    // Aiming moves the head onto that sight instead of moving the gun.
    private static bool PlantedAim(Player player)
    {
        if (player.HandsController == null || !player.HandsController.IsAiming || player.MovementContext == null)
            return false;

        return player.MovementContext.IsInMountedState || player.MovementContext.IsStationaryWeaponInHands;
    }

    internal static bool TrackMountedSight(Player player)
    {
        bool want = PlantedAim(player);
        PlayerBones? bones = player.PlayerBones;
        Transform? head = bones?.Head?.Original;
        if (bones == null || head == null || !SightCamera(player, out Vector3 cameraPosition, out Quaternion cameraRotation))
        {
            _mountBlend = 0f;
            return false;
        }

        _mountBlend = Mathf.MoveTowards(_mountBlend, want ? 1f : 0f, Time.deltaTime / BlendSeconds);
        float along = _mountBlend * _mountBlend * (3f - 2f * _mountBlend);
        if (along <= 0.0001f)
            return false;

        // The camera stays fixed on the head. Ease that camera onto the planted sight, and back off it.
        Quaternion fromCamera = CameraDirection.Resolve(head.rotation);
        Quaternion partialCamera = Quaternion.Slerp(fromCamera, cameraRotation, along);
        Vector3 partialCameraPosition = Vector3.Lerp(CameraPosition.Resolve(head.position, fromCamera), cameraPosition, along);
        Quaternion turn = CameraDirection.Unresolve(partialCamera) * Quaternion.Inverse(head.rotation);
        Vector3 displacement = partialCameraPosition - CameraPosition.Resolve(Vector3.zero, partialCamera) - head.position;
        MoveBody(player, bones, head, displacement, turn);
        ApplyMountedLook(bones, head);
        return true;
    }

    // Moves the body so the head lands where (displacement, turn) puts it while the gun stays planted: the hips
    // take the move and half the turn about the vertical, the upper body bends from its base for the rest, the legs
    // bend to keep their feet and the hands keep their grip.
    private static void MoveBody(Player player, PlayerBones bones, Transform head, Vector3 displacement, Quaternion turn)
    {
        Transform moved = FollowRoot(bones, head);
        BodySkins.EnsureUpperarms(player);
        BodySkins.EnsureBody(player);
        Transform? pelvis = bones.Pelvis?.Original;
        bool split = pelvis != null && moved.IsChildOf(pelvis);
        HeldPose[] planted = PlantedPoses(bones, split ? pelvis! : moved);
        System.Func<Vector3, Vector3> stay = point => point;
        LimbIk.GripArms(bones, BodySkins.Upperarms, moved, stay, Quaternion.identity);
        if (split)
            LimbIk.GripLegs(bones, pelvis!);

        // The gun stays planted, so the body has to come to the sight. Moving only the upper body squashed it
        // into the hips and twisted it off the legs. Now the hips carry the move and half the turn about the
        // vertical, the upper body bends from its base for the rest, and the legs bend to keep their feet.
        Vector3 headTarget = head.position + displacement;
        Vector3 waist = moved.position;
        Quaternion hips = split ? Scale(Twist(turn, Vector3.up), HipTurnShare) : Quaternion.identity;
        Quaternion torso = turn * Quaternion.Inverse(hips);
        Vector3 shift = headTarget - waist - turn * (head.position - waist);
        Vector3 bentWaist = waist + shift;
        System.Func<Vector3, Vector3> hipsMove = point => waist + shift + hips * (point - waist);
        System.Func<Vector3, Vector3> bodyMove = point => bentWaist + torso * (hipsMove(point) - bentWaist);
        if (split)
            Carry(pelvis!, waist, shift, hips);
        else
            Carry(moved, waist, shift, hips);
        Carry(moved, bentWaist, Vector3.zero, torso);
        CarryLooseSplit(bones.Ribcage?.Original, moved, pelvis, waist, shift, hips, bentWaist, torso);
        CarryLooseSplit(bones.Spine3?.Original, moved, pelvis, waist, shift, hips, bentWaist, torso);
        if (bones.Shoulders != null)
        {
            foreach (Transform shoulder in bones.Shoulders)
                CarryLooseSplit(shoulder, moved, pelvis, waist, shift, hips, bentWaist, torso);
        }
        CarryLooseSplit(bones.LeftShoulder?.Original, moved, pelvis, waist, shift, hips, bentWaist, torso);
        CarryLooseSplit(bones.RightShoulder?.Original, moved, pelvis, waist, shift, hips, bentWaist, torso);
        LimbIk.Reach(bodyMove, hipsMove);
        for (int i = 0; i < planted.Length; i++)
            planted[i].Transform.SetPositionAndRotation(planted[i].Position, planted[i].Rotation);
    }

    private static bool _hasMountYaw;
    private static float _mountYaw;

    // Hip-firing from a bipod or a mount: the game swings the gun about the mount point and leaves the body
    // where it was. The body is swung about the same point by the gun's turn since mounting, so it stays behind it.
    // The turn is measured against the upper body as animated: mouse turns while mounted also reach the body
    // animator (DeltaRotation), which already twists the chest part of the way. Measuring against the root
    // turned that part a second time, swinging the far shoulder onto the left hand and folding the elbow.
    internal static void FollowMountedGun(Player player)
    {
        MovementContext? movement = player.MovementContext;
        PlayerBones? bones = player.PlayerBones;
        Transform? head = bones?.Head?.Original;
        Transform? weapon = bones?.Weapon_Root_Anim;
        ProceduralWeaponAnimation? animation = player.ProceduralWeaponAnimation;
        if (movement == null || !movement.IsInMountedState || bones == null || head == null || weapon == null
            || animation?.HandsContainer == null || player.HandsController is not Player.FirearmController firearm)
        {
            _hasMountYaw = false;
            return;
        }

        Vector3 direction = Vector3.ProjectOnPlane(firearm.WeaponDirection, Vector3.up);
        if (direction.sqrMagnitude < 0.0001f)
            return;
        float gunYaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        float relative = Mathf.DeltaAngle(ChestYaw(bones, player.Transform.rotation.eulerAngles.y), gunYaw);
        if (!_hasMountYaw)
        {
            _mountYaw = relative;
            _hasMountYaw = true;
        }
        float swing = Mathf.DeltaAngle(_mountYaw, relative);
        if (Mathf.Abs(swing) < 0.01f)
            return;

        Vector3 localPivot = animation.IsBipodUsed && !animation.IsVerticalMounting
            ? animation.HandsContainer.MountingRotationCenterBipods
            : animation.HandsContainer.MountingRotationCenter;
        Vector3 pivot = weapon.position + weapon.rotation * localPivot;
        Quaternion turn = Quaternion.AngleAxis(swing, Vector3.up);
        Vector3 displacement = pivot + turn * (head.position - pivot) - head.position;
        BodySkins.EnsureUpperarms(player);
        MoveBody(player, bones, head, displacement, turn);
    }

    // Facing of the upper body about the vertical, from the line across the shoulders.
    private static float ChestYaw(PlayerBones bones, float fallback)
    {
        Transform? left = bones.LeftShoulder?.Original;
        Transform? right = bones.RightShoulder?.Original;
        if (left == null || right == null)
            return fallback;
        Vector3 facing = Vector3.Cross(Vector3.ProjectOnPlane(right.position - left.position, Vector3.up), Vector3.up);
        if (facing.sqrMagnitude < 0.0001f)
            return fallback;
        return Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg;
    }

    // The aim pose is applied after the game turns the head, so it would wipe a free look.
    // The game look is withheld while mounted aiming, and put back on the aim pose here.
    private static void ApplyMountedLook(PlayerBones bones, Transform head)
    {
        if (_smoothedHead.sqrMagnitude < 0.0001f)
            return;

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player != null && ReferenceEquals(player.PlayerBones, bones))
            ApplyLook(player, bones, _smoothedHead);
    }

    private const float RecoilChest = 2f;
    private const float RecoilHead = 1f;

    // The first-person camera kick of a shot goes into the chest and on into the head, which carries
    // the camera. The gun and hands keep their place in the world; only the body and view are pushed.
    internal static void BodyRecoil(Player player, ref Vector3 cameraPosition, ref Quaternion cameraRotation)
    {
        ProceduralWeaponAnimation? animation = player.ProceduralWeaponAnimation;
        PlayerBones? bones = player.PlayerBones;
        if (animation?.Shootingg?.CurrentRecoilEffect == null || bones == null || !(player.HandsController is Player.FirearmController))
            return;

        Vector3 recoil = animation.Shootingg.CurrentRecoilEffect.GetCameraRotationRecoil();
        if (animation.Shootingg.CurrentRecoilEffect.WeaponRecoilEffect != null)
            recoil += animation.Shootingg.CurrentRecoilEffect.WeaponRecoilEffect.GetCameraRotationRecoil();
        Quaternion kick = cameraRotation * Quaternion.Euler(recoil) * Quaternion.Inverse(cameraRotation);
        if (Settled(kick))
            return;

        Transform? chest = bones.Ribcage?.Original;
        Transform? neck = bones.Neck;
        Quaternion chestKick = Scale(kick, RecoilChest);
        Quaternion headKick = Scale(kick, RecoilHead);
        if (chest != null && !Settled(chestKick))
        {
            Transform upper = FollowRoot(bones, chest);
            BodySkins.EnsureUpperarms(player);
            List<HeldPose> held = new();
            Hold(held, bones.Weapon_Root_Anim, upper);
            Hold(held, bones.Weapon_Root_Third, upper);
            System.Func<Vector3, Vector3> stay = point => point;
            LimbIk.GripArms(bones, BodySkins.Upperarms, upper, stay, Quaternion.identity);
            Vector3 pivot = chest.position;
            Carry(chest, pivot, Vector3.zero, chestKick);
            if (bones.Shoulders != null)
            {
                foreach (Transform shoulder in bones.Shoulders)
                    CarryLoose(shoulder, chest, pivot, Vector3.zero, chestKick);
            }
            CarryLoose(bones.LeftShoulder?.Original, chest, pivot, Vector3.zero, chestKick);
            CarryLoose(bones.RightShoulder?.Original, chest, pivot, Vector3.zero, chestKick);
            Vector3 recoilPivot = pivot;
            Quaternion recoilTurn = chestKick;
            LimbIk.Reach(point => recoilPivot + recoilTurn * (point - recoilPivot));
            foreach (HeldPose pose in held)
                pose.Transform.SetPositionAndRotation(pose.Position, pose.Rotation);
            if (neck == null || neck.IsChildOf(chest))
            {
                cameraPosition = pivot + chestKick * (cameraPosition - pivot);
                cameraRotation = chestKick * cameraRotation;
            }
        }

        if (neck != null && !Settled(headKick))
        {
            Vector3 pivot = neck.position;
            Carry(neck, pivot, Vector3.zero, headKick);
            cameraPosition = pivot + headKick * (cameraPosition - pivot);
            cameraRotation = headKick * cameraRotation;
        }
    }

    private struct HeldPose
    {
        internal Transform Transform;
        internal Vector3 Position;
        internal Quaternion Rotation;
    }

    // The torso follows the head, from the waist up. Legs stay. A bone that is not under that
    // chain still has to take the same step, or the chest and shoulders stay in the hip pose.
    private static Transform FollowRoot(PlayerBones bones, Transform head)
    {
        Transform? pelvis = bones.Pelvis?.Original;
        Transform? root = bones.RootJoint;
        Transform best = head;
        Transform? cursor = head.parent;
        while (cursor != null && cursor != pelvis && cursor != root)
        {
            best = cursor;
            cursor = cursor.parent;
        }
        return best;
    }

    private static void CarryLoose(Transform? bone, Transform moved, Vector3 pivot, Vector3 displacement, Quaternion turn)
    {
        if (bone == null || bone == moved || bone.IsChildOf(moved) || moved.IsChildOf(bone))
            return;
        Carry(bone, pivot, displacement, turn);
    }

    private static HeldPose[] PlantedPoses(PlayerBones bones, Transform moved)
    {
        List<HeldPose> held = new();
        Hold(held, bones.Weapon_Root_Anim, moved);
        Hold(held, bones.Weapon_Root_Third, moved);
        return held.ToArray();
    }

    private const float HipTurnShare = 0.5f;

    // A bone outside the moved chain takes the hips' step unless it already sits under the hips, then the
    // upper body's bend.
    private static void CarryLooseSplit(Transform? bone, Transform moved, Transform? pelvis, Vector3 waist, Vector3 shift, Quaternion hips, Vector3 bentWaist, Quaternion torso)
    {
        if (bone == null || bone == moved || bone.IsChildOf(moved) || moved.IsChildOf(bone))
            return;
        if (pelvis == null || !bone.IsChildOf(pelvis))
            Carry(bone, waist, shift, hips);
        Carry(bone, bentWaist, Vector3.zero, torso);
    }

    private static void Hold(List<HeldPose> held, Transform? transform, Transform moved)
    {
        if (transform == null || transform == moved || !transform.IsChildOf(moved))
            return;

        // A child kept beside its parent gets a world pose while the parent is still moving,
        // which changes the grip. The parent's saved pose already brings the gun back with the hand.
        for (int i = held.Count - 1; i >= 0; i--)
        {
            Transform kept = held[i].Transform;
            if (transform == kept || transform.IsChildOf(kept))
                return;
            if (kept.IsChildOf(transform))
                held.RemoveAt(i);
        }

        held.Add(new HeldPose { Transform = transform, Position = transform.position, Rotation = transform.rotation });
    }

    private static bool SightCamera(Player player, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;
        ProceduralWeaponAnimation? animation = player.ProceduralWeaponAnimation;
        Transform? bone = animation != null && animation.ScopeAimTransforms != null && animation.ScopeAimTransforms.Count > 0
            ? Scopes.Current(animation)?.Bone
            : null;
        if (animation == null || bone == null)
            return false;

        ResolveIronSight(animation, bone, out Vector3 sightPosition, out Vector3 forward);
        if (forward.sqrMagnitude < 0.0001f)
            return false;
        forward.Normalize();
        bool ags = GrenadeSight.InHands(player);
        if (ags)
        {
            // The sight body only yaws. The picture inside the glass follows the barrel, and the eye sits on the sight.
            Vector3 eye = bone.position;
            Vector3 flat = Vector3.ProjectOnPlane(forward, Vector3.up);
            if (flat.sqrMagnitude < 0.0001f)
                return false;
            flat.Normalize();
            rotation = Quaternion.LookRotation(flat, Vector3.up);
            position = eye;
            return true;
        }

        Vector3 up = Vector3.ProjectOnPlane(bone.name == "aim_camera" ? bone.forward : bone.up, forward);
        if (up.sqrMagnitude < 0.0001f)
            up = Vector3.ProjectOnPlane(Vector3.up, forward);
        if (up.sqrMagnitude < 0.0001f)
            return false;
        // Sight bones do not agree on which axis is up: an "aim_camera" bone (such as a Mosin's iron sight)
        // and the mounted machine guns come out upside down. A planted sight is never upside down, so up is
        // whichever way faces the sky.
        if (Vector3.Dot(up, Vector3.up) < 0f)
            up = -up;

        rotation = Quaternion.LookRotation(forward, up);
        position = sightPosition - forward * (ForwardDistance + SightRelief(animation, bone));
        return true;
    }

    internal static Quaternion KickView(Quaternion view)
    {
        return _viewKick * view;
    }


    private static void Sway(ProceduralWeaponAnimation animation, Transform third, Transform anim, Transform sightBone, Vector3 sight, Vector3 sightForward, bool ironAligned, int sightId, Vector3 cameraPosition, Quaternion cameraRotation, Vector3 aimPoint, out Vector3 displacement, out Quaternion turn)
    {
        Quaternion toCamera = Quaternion.Inverse(cameraRotation);
        Vector3 sightOnThird = third.position + third.rotation * (Quaternion.Inverse(anim.rotation) * (sight - anim.position));
        Vector3 armMove = toCamera * (sightOnThird - cameraPosition);
        Vector3 rawGunMove = toCamera * (sight - sightOnThird);
        Quaternion armTurn = toCamera * third.rotation;
        Quaternion rawGunTurn = Quaternion.Inverse(third.rotation) * anim.rotation;

        bool sightChanged = !_hasMean || sightId != _meanSightId;
        if (sightChanged)
        {
            _gunMoveSmooth = rawGunMove;
            _gunTurnSmooth = rawGunTurn;
            _lagLook = _smoothLook;
            _armMoveMean = armMove;
            _gunMoveMean = rawGunMove;
            _armTurnMean = armTurn;
            _gunTurnMean = rawGunTurn;
            _armTurnCenter = armTurn;
            _gunTurnCenter = rawGunTurn;
            _meanSightId = sightId;
            _meanStart = Time.time;
            _hasMean = true;
            _hasChase = false;
            _sightCatch = 0.35f;
        }
        else
        {
            float smooth = 1f - Mathf.Exp(-Time.deltaTime / GunSmoothSeconds);
            _gunMoveSmooth = Vector3.Lerp(_gunMoveSmooth, rawGunMove, smooth);
            _gunTurnSmooth = Quaternion.Slerp(_gunTurnSmooth, rawGunTurn, smooth).normalized;
        }

        Vector3 gunMove = _gunMoveSmooth;
        Quaternion gunTurn = _gunTurnSmooth;
        float meanSeconds = Time.time - _meanStart < AimInSeconds ? AimInMeanSeconds : MeanSeconds;
        // Leaning is a change of pose, not sway: while the lean changes, and a little after, the means and centres
        // catch up at once, so the gun follows the lean instead of drifting there over a second or more.
        float tilt = Singleton<GameWorld>.Instance?.MainPlayer?.MovementContext?.SmoothedTilt ?? 0f;
        if (!sightChanged && Mathf.Abs(tilt - _leanTilt) > 0.001f)
            _leanCatchUntil = Time.time + LeanCatchSeconds;
        _leanTilt = tilt;
        if (Time.time < _leanCatchUntil)
        {
            meanSeconds = AimInMeanSeconds;
            _sightCatch = Mathf.Max(_sightCatch, Time.deltaTime);
        }
        float follow = 1f - Mathf.Exp(-Time.deltaTime / meanSeconds);
        _armMoveMean = Vector3.Lerp(_armMoveMean, armMove, follow);
        _gunMoveMean = Vector3.Lerp(_gunMoveMean, gunMove, follow);
        _armTurnMean = Quaternion.Slerp(_armTurnMean, armTurn, follow).normalized;
        _gunTurnMean = Quaternion.Slerp(_gunTurnMean, gunTurn, follow).normalized;

        Vector3 smoothSight = sight + cameraRotation * (gunMove - rawGunMove);
        float armMoveScale = _mounted ? 0f : Now.ArmMove;
        float gunMoveScale = _mounted ? 0f : Now.GunMove;
        float armTurnScale = _mounted ? 0f : Now.ArmTurn;
        float gunTurnScale = _mounted ? 0f : Now.GunTurn;
        float breathScale = _mounted ? 0f : Now.Breath;
        Vector3 sway = armMoveScale * (armMove - _armMoveMean) + gunMoveScale * (gunMove - _gunMoveMean);
        Vector3 pinned = toCamera * (cameraPosition + cameraRotation * (aimPoint + sway) - smoothSight);
        float side = 1f;
        bool chasing = _hasChase && (_handAction || _chaseCatching);
        if (_hasChase)
        {
            float speed = animation.AimingSpeed * animation.CameraSmoothBlender.Value;
            if (speed <= 0f)
                speed = 16f;
            side = 1f - Mathf.Exp(-speed * Time.deltaTime);
            float depth = 1f - Mathf.Exp(-animation.CameraSmoothTime * Time.deltaTime);
            _chase.x += (pinned.x - _chase.x) * side;
            _chase.y += (pinned.y - _chase.y) * side;
            _chase.z += (pinned.z - _chase.z) * depth;
        }
        else
        {
            _chase = pinned;
        }
        displacement = smoothSight - sight + cameraRotation * _chase;

        if (_sightCatch > 0f)
        {
            float catchUp = 1f - Mathf.Exp(-Time.deltaTime / 0.08f);
            _armTurnCenter = Quaternion.Slerp(_armTurnCenter, armTurn, catchUp).normalized;
            _gunTurnCenter = Quaternion.Slerp(_gunTurnCenter, rawGunTurn, catchUp).normalized;
            _sightCatch -= Time.deltaTime;
        }
        else
        {
            float slow = 1f - Mathf.Exp(-Time.deltaTime / BreathCenterSeconds);
            _armTurnCenter = Quaternion.Slerp(_armTurnCenter, _armTurnMean, slow).normalized;
            _gunTurnCenter = Quaternion.Slerp(_gunTurnCenter, _gunTurnMean, slow).normalized;
        }
        Quaternion armBase = _armTurnCenter * Scale(Quaternion.Inverse(_armTurnCenter) * _armTurnMean, breathScale);
        Quaternion gunBase = _gunTurnCenter * Scale(Quaternion.Inverse(_gunTurnCenter) * _gunTurnMean, breathScale);
        Quaternion keptArm = armBase * Scale(Quaternion.Inverse(_armTurnMean) * armTurn, armTurnScale);
        Quaternion keptGun = gunBase * Scale(Quaternion.Inverse(_gunTurnMean) * gunTurn, gunTurnScale);
        // The position above is always chased. The turn is chased only during a bolt cycle or fire mode
        // switch, so that animation shows first; otherwise chasing it lets breath sway through a held breath.
        Quaternion pinnedTurn = LineOfSight(animation, sightBone, sightForward, ironAligned, anim, armBase * gunBase) * keptArm * keptGun * Quaternion.Inverse(armTurn * rawGunTurn);
        // The catch-up after the action has a fixed length. Waiting for the chase to settle never ends
        // while breath sway keeps moving the target.
        if (_handAction)
            _chaseCatchStart = Time.time;
        if (chasing)
        {
            _chaseTurn = Quaternion.Slerp(_chaseTurn, pinnedTurn, side).normalized;
            if (!_handAction)
            {
                float t = Mathf.Clamp01((Time.time - _chaseCatchStart) / ChaseCatchSeconds);
                _chaseTurn = Quaternion.Slerp(_chaseTurn, pinnedTurn, t * t * (3f - 2f * t)).normalized;
                if (t >= 1f)
                    _chaseCatching = false;
            }
        }
        else
        {
            _chaseTurn = pinnedTurn;
        }
        _hasChase = true;
        if (_handAction)
            _chaseCatching = true;
        turn = cameraRotation * _chaseTurn * toCamera;
    }

    private static Quaternion LineOfSight(ProceduralWeaponAnimation animation, Transform sightBone, Vector3 sightForward, bool ironAligned, Transform anim, Quaternion weaponInCamera)
    {
        Vector3 axis = ironAligned ? sightForward : sightBone.name == "aim_camera" ? -sightBone.up : -sightBone.forward;
        Vector3 sightInCamera = weaponInCamera * (Quaternion.Inverse(anim.rotation) * axis);
        float zeroing = 0f;
        try
        {
            // The physical marker axis already contains the iron-sight slope.
            // Applying the native cached slope again would calibrate it twice.
            if (!ironAligned) zeroing = LineOfSightAngle(animation);
        }
        catch
        {
        }
        if (Mathf.Abs(zeroing) > 0.0001f)
            sightInCamera = Quaternion.AngleAxis(-zeroing, weaponInCamera * Vector3.right) * sightInCamera;
        return TurnFromTo(sightInCamera, Vector3.forward);
    }

    internal static void VisualPassPostfix(Player __instance)
    {
        if (!NewCameraSwitch.Enabled || !__instance.IsYourPlayer)
            return;

        _mounted = __instance.MovementContext != null && __instance.MovementContext.IsInMountedState;
        ApplyHeadLock(__instance);
        UpdateFreeLookReturn(__instance);
        SprintHeadPitch(__instance);
        CalmWalkHead(__instance);
        Transform? head = __instance.PointOfView != EPointOfView.FreeCamera ? __instance.PlayerBones?.Head?.Original : null;
        if (head != null)
        {
            LiftHead(head);
            HeadwearTilt.Apply(__instance, head);
        }
    }

    private static Vector3 _freeLookFrom;
    private static float _freeLookWeight;
    private static bool _freeLookReturning;
    private static bool _pitchUnderLook;

    // While the button is held the head follows the free look. After release, that offset
    // shrinks back to the sprint pose; the pose corrections have to grow in step with it,
    // or the head visits the raw animation and then jumps.
    private static void UpdateFreeLookReturn(Player player)
    {
        Vector3 raw = player.HeadRotation;
        float now = Mathf.Max(_smoothedHead.magnitude, raw.magnitude);
        bool live = raw.sqrMagnitude > 0.01f || _smoothedHead.sqrMagnitude > 0.01f;
        if (!live)
        {
            _freeLookReturning = false;
            _freeLookFrom = Vector3.zero;
            _freeLookWeight = 0f;
            return;
        }

        bool holding = !_freeLookReturning && (player.MouseLookControl || !player.IsResettingLook);
        if (holding)
        {
            _freeLookFrom = _smoothedHead.magnitude >= raw.magnitude ? _smoothedHead : raw;
            _freeLookWeight = 1f;
            return;
        }

        _freeLookReturning = true;
        if (_freeLookFrom.sqrMagnitude < 0.01f)
            _freeLookFrom = _smoothedHead.magnitude >= raw.magnitude ? _smoothedHead : raw;
        float span = _freeLookFrom.magnitude;
        _freeLookWeight = span < 0.01f ? 0f : Mathf.Clamp01(now / span);
    }

    private const float SprintPitchSeconds = 0.2f;
    private static float _sprintPitch;
    private const float PitchCenterSeconds = 0.5f;
    private static bool _hasPitchCenter;
    private static float _pitchCenter;

    // Sprint animation holds the head level; third-person RotateHead only adds free look, not mouse pitch.
    private static void SprintHeadPitch(Player player)
    {
        Transform? head = player.PlayerBones?.Head?.Original;
        if (head == null || player.MovementContext == null)
            return;

        bool sprinting = player.MovementContext.CurrentState != null
            && player.MovementContext.CurrentState.Name == EPlayerState.Sprint
            && (player.HandsController == null || !player.HandsController.IsAiming);
        _sprintPitch = Mathf.MoveTowards(_sprintPitch, sprinting ? 1f : 0f, Time.deltaTime / SprintPitchSeconds);

        if (_freeLookWeight <= 0f)
        {
            Vector3 forward = CameraDirection.Resolve(head.rotation) * Vector3.forward;
            float headPitch = -Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            if (!_hasPitchCenter)
            {
                _pitchCenter = headPitch;
                _hasPitchCenter = true;
            }
            else
                _pitchCenter = Mathf.Lerp(_pitchCenter, headPitch, 1f - Mathf.Exp(-Time.deltaTime / PitchCenterSeconds));
        }

        float sprintBlend = _sprintPitch * _sprintPitch * (3f - 2f * _sprintPitch);
        bool underLook = _pitchUnderLook;
        _pitchUnderLook = false;
        if (underLook || sprintBlend <= 0f)
            return;

        float missing = Mathf.DeltaAngle(_pitchCenter, player.MovementContext.Pitch) * sprintBlend;
        if (Mathf.Abs(missing) < 0.01f)
            return;

        ApplySprintPitch(player, missing);
    }

    private static bool SprintPitchAngle(Player player, out float missing)
    {
        missing = 0f;
        if (player.MovementContext == null)
            return false;

        float sprintBlend = _sprintPitch * _sprintPitch * (3f - 2f * _sprintPitch);
        if (sprintBlend <= 0f)
            return false;

        missing = Mathf.DeltaAngle(_pitchCenter, player.MovementContext.Pitch) * sprintBlend;
        return Mathf.Abs(missing) >= 0.01f;
    }

    private static void ApplySprintPitch(Player player, float missing)
    {
        if (player.MovementContext == null)
            return;

        Vector3 axis = Quaternion.Euler(0f, player.MovementContext.Rotation.x, 0f) * Vector3.right;
        Transform? neck = player.PlayerBones?.Neck;
        Transform? head = player.PlayerBones?.Head?.Original;
        float neckTurn = neck != null ? missing * 0.5f : 0f;
        if (neck != null)
            neck.rotation = Quaternion.AngleAxis(neckTurn, axis) * neck.rotation;
        if (head != null)
            head.rotation = Quaternion.AngleAxis(missing - neckTurn, axis) * head.rotation;
    }

    private static void ApplyHeadLock(Player player)
    {
        if (_headLock <= 0f)
            return;

        Transform? head = player.PlayerBones?.Head?.Original;
        if (head == null)
            return;

        float weight = _headLock * _headLock * (3f - 2f * _headLock);
        head.rotation = Quaternion.Slerp(head.rotation, CameraDirection.Unresolve(_smoothLook * AimTurn()), weight);
    }

    internal static void CalmWalkHead(Player player)
    {
        Transform? head = player.PlayerBones?.Head?.Original;
        if (head == null || player.MovementContext == null || _freeLookWeight >= 1f)
            return;

        // The turn about the vertical is read from the head's whole rotation, not from a flattened
        // view direction: flattening blows a small run tilt up into a large turn when looking far up or down.
        Quaternion local = Quaternion.Inverse(Quaternion.Euler(0f, player.MovementContext.Rotation.x, 0f)) * CameraDirection.Resolve(head.rotation);
        if (_freeLookWeight <= 0f)
        {
            if (!_hasYawCenter)
            {
                _lookCenter = local;
                _hasYawCenter = true;
            }
            else
                _lookCenter = Quaternion.Slerp(_lookCenter, local, 1f - Mathf.Exp(-Time.deltaTime / 0.5f)).normalized;
        }

        Quaternion delta = local * Quaternion.Inverse(_lookCenter);
        float yaw = Mathf.DeltaAngle(0f, 2f * Mathf.Atan2(delta.y, delta.w) * Mathf.Rad2Deg);
        float moving = Mathf.Clamp01(player.MovementContext.ActualLinearVelocity / 0.6f);
        float keep = Mathf.Lerp(1f, WalkHeadSway, moving);
        float fix = -yaw * (1f - keep) * (1f - _freeLookWeight);
        if (Mathf.Abs(fix) > 0.01f)
            head.rotation = Quaternion.AngleAxis(fix, Vector3.up) * head.rotation;
    }

    // The head model lifted about the camera's right axis, as the bones are posed for the frame: lifted only as the
    // cameras cull, with nothing in hand the face was drawn as the animation left it. The camera is placed from the
    // head as it was before (UnliftHead), which the lift turns about the axis it keeps; lifted again after.
    internal static void LiftHead(Transform head)
    {
        _lifted = null;
        if (Mathf.Abs(HeadModelPitch) < 0.01f)
            return;

        Vector3 axis = CameraDirection.Resolve(head.rotation) * Vector3.right;
        head.rotation = Quaternion.AngleAxis(-HeadModelPitch, axis) * head.rotation;
        _lifted = head;
        _liftedFrame = Time.frameCount;
    }

    // The head as the animation left it, for the camera's placing.
    internal static void UnliftHead(Transform head)
    {
        if (_lifted != head || _liftedFrame != Time.frameCount)
            return;
        _lifted = null;
        Vector3 axis = CameraDirection.Resolve(head.rotation) * Vector3.right;
        head.rotation = Quaternion.AngleAxis(HeadModelPitch, axis) * head.rotation;
    }

    private static Transform? _lifted;
    private static int _liftedFrame = -1;

    internal static void ObserveHead(Player player, Vector3 cameraPosition, Quaternion cameraRotation)
    {
        _hasObserved = false;
        Transform? neck = player.PlayerBones?.Neck;
        if (neck == null)
            return;

        if (_turnFrame != Time.frameCount && !UpdateTurn(player, cameraRotation))
            return;

        _neckPosition = neck.position;
        _headPositionNow = Quaternion.Inverse(_smoothLook) * (cameraPosition - _neckPosition);
        _hasObserved = true;
    }

    private static void UpdateBreathState(Player player)
    {
        bool holding = false;
        bool tired = false;
        try
        {
            if (player.Physical != null)
            {
                holding = player.Physical.HoldingBreath;
                tired = player.Physical.HandsStamina != null && player.Physical.HandsStamina.Exhausted;
                if (NewCameraSwitch.Enabled && player.Physical is Physical physical)
                    AimStamina.ApplyAimDrain(physical);
            }
        }
        catch
        {
        }

        float step = Time.deltaTime / StateBlendSeconds;
        _holdWeight = Mathf.MoveTowards(_holdWeight, holding ? 1f : 0f, step);
        _tiredWeight = Mathf.MoveTowards(_tiredWeight, tired ? 1f : 0f, step);
        float hold = Smooth(_holdWeight);
        float tiredness = Smooth(_tiredWeight);
        float Mix(float normal, float held, float exhausted) => Mathf.Lerp(Mathf.Lerp(normal, held, hold), exhausted, tiredness);
        Now.HeadFollow = Mix(Normal.HeadFollow, Holding.HeadFollow, Exhausted.HeadFollow);
        Now.ArmMove = Mix(Normal.ArmMove, Holding.ArmMove, Exhausted.ArmMove);
        Now.ArmTurn = Mix(Normal.ArmTurn, Holding.ArmTurn, Exhausted.ArmTurn);
        // The first-person skeleton's gun sways against the arms as the game's first-person view has it (the
        // procedural breath and hand shake, which the game itself calms while the breath is held); the arms, moved
        // by the body, are held as set, as with the third-person skeleton.
        if (NewCameraSwitch.FirstPersonSkeleton)
        {
            Now.GunMove = Now.GunTurn = Now.Breath = 1f;
            return;
        }
        Now.GunMove = Mix(Normal.GunMove, Holding.GunMove, Exhausted.GunMove);
        Now.GunTurn = Mix(Normal.GunTurn, Holding.GunTurn, Exhausted.GunTurn);
        Now.Breath = Mix(Normal.Breath, Holding.Breath, Exhausted.Breath);
    }

    private static float Smooth(float t)
    {
        return t * t * (3f - 2f * t);
    }

    private static bool UpdateTurn(Player player, Quaternion rawCamera)
    {
        Vector2 mouse;
        try
        {
            if (player.MovementContext == null)
                return false;
            mouse = player.MovementContext.Rotation;
        }
        catch
        {
            return false;
        }

        Quaternion look = Quaternion.Euler(mouse.y, mouse.x, 0f);
        bool continuous = _turnFrame == Time.frameCount - 1;
        if (_posedFrame == Time.frameCount && _previousPosedFrame == Time.frameCount - 1)
            _bodyLook = Quaternion.Euler(_previousPosedMouse.y, _previousPosedMouse.x, 0f);
        else
            _bodyLook = continuous ? _smoothLook : look;
        _turnFrame = Time.frameCount;
        _smoothLook = look;

        if (!_hasCenter || FollowDelay <= 0f)
            _lagLook = _smoothLook;
        else
            _lagLook = Quaternion.Slerp(_lagLook, _smoothLook, 1f - Mathf.Exp(-Time.deltaTime / FollowDelay)).normalized;
        float behind = Quaternion.Angle(_smoothLook, _lagLook);
        if (behind > MaxDelayDegrees)
            _lagLook = Quaternion.Slerp(_smoothLook, _lagLook, MaxDelayDegrees / behind).normalized;

        UpdateBreathState(player);

        Quaternion rawTurn = Quaternion.Inverse(_bodyLook) * rawCamera;
        _headTurnRaw = rawTurn;
        if (!_hasCenter)
        {
            _headTurnSmooth = rawTurn;
            _hasCenter = true;
        }
        else
        {
            _headTurnSmooth = Quaternion.Slerp(_headTurnSmooth, rawTurn, 1f - Mathf.Exp(-Time.deltaTime / HeadSmoothSeconds)).normalized;
        }

        return true;
    }

    private static Quaternion Lean(Quaternion headTurn)
    {
        Vector4 twist = new Vector4(0f, 0f, headTurn.z, headTurn.w);
        float length = twist.magnitude;
        return length < 0.0001f ? Quaternion.identity : new Quaternion(0f, 0f, twist.z / length, twist.w / length);
    }

    private static Quaternion AimTurn()
    {
        Quaternion sway = _headTurnSmooth * Quaternion.Inverse(Lean(_headTurnSmooth));
        return Scale(sway, _mounted ? 0f : Now.HeadFollow) * Lean(_headTurnRaw);
    }

    internal static float FreeLookYaw = 70f;
    internal static float FreeLookDownExtra;
    private static Vector2 _lookLimitSaved;
    private static Vector2 _verticalLimitSaved;
    private static bool _lookLimitSwapped;

    internal static void LookPrefix(Player __instance)
    {
        _lookLimitSwapped = false;
        if (!NewCameraSwitch.Enabled || !__instance.IsYourPlayer)
            return;

        EFTHardSettings settings = EFTHardSettings.Instance;
        if (settings == null)
            return;

        _lookLimitSaved = settings.MOUSE_LOOK_HORIZONTAL_LIMIT;
        float scale = 1f;
        if (__instance.HandsController != null && __instance.HandsController.IsAiming)
            scale = settings.MOUSE_LOOK_LIMIT_IN_AIMING_COEF;
        if (scale < 0.01f)
            scale = 1f;
        float reach = FreeLookYaw / scale;
        settings.MOUSE_LOOK_HORIZONTAL_LIMIT = new Vector2(-reach, reach);
        _verticalLimitSaved = settings.MOUSE_LOOK_VERTICAL_LIMIT;
        settings.MOUSE_LOOK_VERTICAL_LIMIT = new Vector2(_verticalLimitSaved.x, _verticalLimitSaved.y + FreeLookDownExtra);
        _lookLimitSwapped = true;
    }

    internal static void LookPostfix()
    {
        if (!_lookLimitSwapped)
            return;

        _lookLimitSwapped = false;
        EFTHardSettings settings = EFTHardSettings.Instance;
        if (settings != null)
        {
            settings.MOUSE_LOOK_HORIZONTAL_LIMIT = _lookLimitSaved;
            settings.MOUSE_LOOK_VERTICAL_LIMIT = _verticalLimitSaved;
        }
    }

    internal static void RotateHeadPostfix(PlayerBones __instance, Vector3 offset)
    {
        if (NewCameraSwitch.Enabled)
            TurnLook(__instance);

        if (!NewCameraSwitch.Enabled || _turnFrame == Time.frameCount)
            return;

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        Transform? head = __instance.Head?.Original;
        if (player == null || head == null || !ReferenceEquals(player.PlayerBones, __instance))
            return;

        _rotateHeadFrame = Time.frameCount;
        if (!UpdateTurn(player, CameraDirection.Resolve(head.rotation)))
            return;

        bool aiming = player.HandsController != null && player.HandsController.IsAiming;
        // The stock at the chest leaves the head free, as at the hip: the view goes with the head (and the balance),
        // so nothing changes as the stance is let go.
        float target = aiming && !ChestStance.Active && _smoothedHead.sqrMagnitude <= 0.01f ? 1f : 0f;
        _headLock = Mathf.MoveTowards(_headLock, target, Time.deltaTime / (target > _headLock ? HeadLockSeconds : LeaveSeconds));
    }

    private static void SteadyHead(Player player, Vector3 cameraPosition, Quaternion cameraRotation, out Vector3 position, out Quaternion rotation)
    {
        position = cameraPosition;
        rotation = cameraRotation;
        if (!_hasObserved)
            return;

        if (_smoothedHead.sqrMagnitude > 0.01f)
        {
            if (!_hasHeadMean)
            {
                _heldHeadPosition = _headPositionNow;
                _heldHeadTurn = AimTurn();
                _hasHeadMean = true;
            }
            _release = 1f;
            position = _neckPosition + _smoothLook * _heldHeadPosition;
            rotation = _smoothLook * _heldHeadTurn;
            return;
        }

        if (!_hasHeadMean)
        {
            _release = 0f;
            _hasHeadMean = true;
        }

        Vector3 steadyPosition = _headPositionNow;
        Quaternion steadyTurn = AimTurn();
        if (_release > 0f)
        {
            _release = Mathf.Max(0f, _release - Time.deltaTime / ReleaseSeconds);
            float held = _release * _release * (3f - 2f * _release);
            steadyPosition = Vector3.Lerp(steadyPosition, _heldHeadPosition, held);
            steadyTurn = Quaternion.Slerp(steadyTurn, _heldHeadTurn, held);
        }

        _heldHeadPosition = steadyPosition;
        _heldHeadTurn = steadyTurn;
        position = _neckPosition + _smoothLook * steadyPosition;
        rotation = _smoothLook * steadyTurn;
    }

    internal static void RotateHeadPrefix(PlayerBones __instance, ref Vector3 offset)
    {
        if (!NewCameraSwitch.Enabled)
            return;

        Player? player = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !ReferenceEquals(player.PlayerBones, __instance))
            return;

        if (_headSmoothFrame != Time.frameCount)
        {
            _headSmoothFrame = Time.frameCount;
            _smoothedHead = Vector3.Lerp(_smoothedHead, offset, 1f - Mathf.Exp(-Time.deltaTime / HeadSmoothSeconds));
            if (offset == Vector3.zero && _smoothedHead.sqrMagnitude < 0.0001f)
                _smoothedHead = Vector3.zero;
        }

        offset = _smoothedHead;
        if (PlantedAim(player))
            offset = Vector3.zero;
        _look = offset;
        offset = Vector3.zero;
        if (player.HeadRotation.sqrMagnitude <= 0.01f && _smoothedHead.sqrMagnitude <= 0.01f)
            return;
        if (!SprintPitchAngle(player, out float missing))
            return;

        // Free look is applied on top of this rotation. Pitch has to be underneath it,
        // otherwise starting a look drops the head back to the level sprint pose first.
        ApplySprintPitch(player, missing);
        _pitchUnderLook = true;
    }

    internal static float LookAxisForward;
    private static Vector3 _look;
    private static float _lookYawSign = 1f;

    // The game turns the free look about the neck and head bones' own axes, which lean forward
    // with the neck, and splits it so the head falls short of the full angle. The whole look is
    // turned at the neck instead: down about the body's right, then sideways about the set axis.
    private static void TurnLook(PlayerBones bones)
    {
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !ReferenceEquals(player.PlayerBones, bones))
            return;

        Vector3 look = _look;
        _look = Vector3.zero;
        ApplyLook(player, bones, look);
    }

    private static void ApplyLook(Player player, PlayerBones bones, Vector3 offset)
    {
        float yaw = offset.x;
        float down = -offset.z;
        if (Mathf.Abs(yaw) < 0.01f && Mathf.Abs(down) < 0.01f)
            return;

        Transform? pivot = bones.Neck != null ? bones.Neck : bones.Head?.Original;
        if (pivot == null || player.MovementContext == null)
            return;

        float up = Vector3.Dot(pivot.rotation * Vector3.right, Vector3.up);
        if (Mathf.Abs(up) > 0.3f)
            _lookYawSign = Mathf.Sign(up);
        pivot.rotation = LookTurn(player.MovementContext, yaw, down) * pivot.rotation;
    }

    private static Quaternion LookTurn(MovementContext movement, float yaw, float down)
    {
        Quaternion body = Quaternion.Euler(0f, movement.Rotation.x, 0f);
        Vector3 axis = body * (Quaternion.Euler(LookAxisForward, 0f, 0f) * Vector3.up);
        return Quaternion.AngleAxis(yaw * _lookYawSign, axis) * Quaternion.AngleAxis(down, body * Vector3.right);
    }

    // The mouse look the body bones (and so the head, the helmet on it and the gun) were posed with this frame.
    // It trails the newest mouse by one movement update; see _bodyLook.
    internal static Quaternion PosedLook(Player player)
    {
        return _turnFrame == Time.frameCount ? _bodyLook : player.HandsRotation;
    }

    // The free look as a world turn on top of the mouse look, the same turn ApplyLook puts on the neck.
    internal static Quaternion FreeLookTurn(Player player)
    {
        if (player.MovementContext == null || PlantedAim(player))
            return Quaternion.identity;
        float yaw = _smoothedHead.x;
        float down = -_smoothedHead.z;
        if (Mathf.Abs(yaw) < 0.01f && Mathf.Abs(down) < 0.01f)
            return Quaternion.identity;
        return LookTurn(player.MovementContext, yaw, down);
    }

    private static bool ResolveIronSight(ProceduralWeaponAnimation animation, Transform bone, out Vector3 position, out Vector3 forward)
    {
        if (CalibratedSight.TryResolve(animation, bone, out position, out forward))
            return true;
        position = bone.position;
        forward = bone.name == "aim_camera" ? -bone.up : -bone.forward;
        var scope = Scopes.Current(animation);
        Transform? weapon = animation.HandsContainer?.Weapon;
        return scope != null && weapon != null && IronSightAlignment.TryResolve(weapon, bone,
            scope.AlignIfAvailable, scope.IsOptic, out position, out forward);
    }

    private static float SightRelief(ProceduralWeaponAnimation animation, Transform bone)
    {
        if (animation.HandsContainer == null || animation.HandsContainer.Weapon == null)
            return 0f;

        WeaponPrefab.AimPlane? plane = animation._currentAimingPlane;
        if (Scopes.Current(animation) is { IsOptic: true })
            plane = animation.HandsContainer.DefaultAimPlane;
        if (plane == null)
            plane = animation.HandsContainer.DefaultAimPlane;
        if (plane == null)
            return 0f;

        float boneDepth = 0f - animation.HandsContainer.Weapon.InverseTransformPoint(bone.position).y;
        return boneDepth - plane.Depth;
    }

    // First person returns before the body shot reaction. Mounting already shrinks the
    // hand recoil; the third-person reaction is the full punch and ignores the bipod.
    internal static IEnumerable<CodeInstruction> SkipMountedBodyRecoil(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo? getter = AccessTools.PropertyGetter(typeof(Player), nameof(Player.FirstPersonPointOfView));
        MethodInfo? skip = typeof(AimArms).GetMethod(nameof(OrMounted), BindingFlags.NonPublic | BindingFlags.Static);
        int seen = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            yield return instruction;
            if (getter != null && skip != null && instruction.Calls(getter) && ++seen == 2)
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, skip);
            }
        }
    }

    private static bool OrMounted(bool firstPerson, Player player)
    {
        if (firstPerson)
            return true;

        return NewCameraSwitch.Enabled && player.IsYourPlayer && player.MovementContext != null && player.MovementContext.IsInMountedState;
    }

    internal static void Release(Player? player)
    {
        _blendAiming = false;
        _blendSightId = 0;
        _blend = 1f;
        _mountBlend = 0f;
        _offset = Vector3.zero;
        _blendFrom = Vector3.zero;
        _turn = Quaternion.identity;
        _turnFrom = Quaternion.identity;
        _viewTurnPart = null;
        _hasMean = false;
        _hasHeadMean = false;
        _hasChase = false;
        _handAction = false;
        _chaseCatching = false;
        _release = 0f;
        _smoothedHead = Vector3.zero;
        _freeLookFrom = Vector3.zero;
        _freeLookWeight = 0f;
        _freeLookReturning = false;
        _pitchUnderLook = false;
        _headLock = 0f;
        BodySkins.Restore();
        if (!_suppress)
            return;

        _suppress = false;
        if (player?.HandsController != null && player.HandsController.IsAiming)
            player.MovementContext?.PlayerAnimator?.SetAiming(true);
    }

    // Switched off: the aim stamina drain and the aim field of view worked out by the game's own rules again.
    internal static void GiveBackAim(Player player)
    {
        try
        {
            if (player.Physical is Physical physical)
                physical.Aim(physical._aimMass);
            player.ProceduralWeaponAnimation?.OnAimOrPoseChanged(true);
        }
        catch (System.Exception e)
        {
            NewCameraPlugin.Log?.LogWarning("Giving the aim back to the game failed: " + e.Message);
        }
    }

    internal static void SetAimingPrefix(PlayerAnimator __instance, ref bool isAiming)
    {
        if (!_suppress || !isAiming || !NewCameraSwitch.Enabled)
            return;

        Player? player = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer;
        if (player != null && player.IsYourPlayer && ReferenceEquals(player.MovementContext?.PlayerAnimator, __instance))
            isAiming = false;
    }
}
