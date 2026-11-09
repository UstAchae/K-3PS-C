using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Settings;
using UnityEngine;

namespace EFTNewCamera;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("com.borkel.nvgmasks", BepInDependency.DependencyFlags.SoftDependency)]   // BRNVG makes the eyepiece picture
[BepInDependency("com.pein.camerarecoilmod", BepInDependency.DependencyFlags.SoftDependency)]   // SPTRecoilReworkLegacy, switched by RecoilMode
public sealed class NewCameraPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "justachae.thirdpersoncamera";
    public const string PluginName = "Justachae's Third-Person Skeleton and Camera";
    public const string PluginVersion = "1.7.0";

    internal static BepInEx.Logging.ManualLogSource? Log;
    private const string SkeletonThird = "第三人称骨架", SkeletonFirst = "第一人称骨架";
    private const string FisheyeLight = "省性能", FisheyeSharp = "高清";
    private static GUIStyle? _warning;

    // The fisheye's clarity in F12: "high" in red, with what it costs said in place, not left to the tooltip.
    private static void DrawFisheyeClarity(ConfigEntryBase entry)
    {
        bool sharp = (string)entry.BoxedValue == FisheyeSharp;
        Color color = GUI.color;
        GUILayout.BeginVertical();
        GUILayout.BeginHorizontal();
        if (GUILayout.Toggle(!sharp, FisheyeLight, GUI.skin.button, GUILayout.ExpandWidth(true)) && sharp)
            entry.BoxedValue = FisheyeLight;
        GUI.color = new Color(1f, 0.35f, 0.3f);
        if (GUILayout.Toggle(sharp, FisheyeSharp + "（极吃性能）", GUI.skin.button, GUILayout.ExpandWidth(true)) && !sharp)
            entry.BoxedValue = FisheyeSharp;
        GUILayout.EndHorizontal();
        _warning ??= new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, wordWrap = true };
        GUI.color = sharp ? new Color(1f, 0.35f, 0.3f) : new Color(1f, 0.8f, 0.3f);
        GUILayout.Label(sharp
            ? "【警告】高清正在开启：按更高的分辨率渲染，帧数会大幅下降，显存占用大增，没有上限保护。清晰度补偿大于 1 是真正的超采样，开销按平方增长。显存不够时游戏会卡住好几秒甚至更久。卡顿就换回省性能，或调低下面的清晰度补偿。"
            : "高清画面中心更清楚，但极吃性能和显存，配置不够不要开。", _warning);
        GUI.color = color;
        GUILayout.EndVertical();
    }

    private void Awake()
    {
        Log = Logger;
        // No section: the F12 window shows the master switch right under the plugin's name, without a heading.
        ConfigEntry<bool> enabled = SettingsLayout.Bind(Config, "", "总开关", true,
            new ConfigDescription("关掉后整个 mod 不起作用，一切按游戏原样。", null, new ConfigurationManagerAttributes { Order = 8 }));
        MoveFrom(enabled, "新镜头", "总开关");
        ConfigEntry<string> skeleton = SettingsLayout.Bind(Config, "", "骨架", SkeletonThird,
            new ConfigDescription("第三人称骨架：用第三人称的身体动作（呼吸摆动、跑步转向时头部翻滚、换枪和翻背包的动作）。第一人称骨架：身体动作用游戏原版的第一人称（没有呼吸摆动和跑步转向时的头部翻滚，换枪、翻背包没有身体动作），晃得少，适合觉得第三人称骨架晕的玩家。除了身体动作，两种骨架的其他功能完全一样（镜头挂在头部、人物显示、瞄准等），只是第一人称骨架下瞄准时枪本身按原版第一人称的摆动晃（呼吸、手抖，屏息时由原版自己减弱），“瞄准摆动”那几组里的“摆动”只管手臂跟着身体动作的晃动，“呼吸”不起作用。局内可以随时切换。",
                new AcceptableValueList<string>(SkeletonThird, SkeletonFirst), new ConfigurationManagerAttributes { Order = 7 }));
        AimPatches.Install();
        DovetailSlots.Install();
        Fisheye.Install();
        BodyVisibility.Install();
        VisorFilter.Install();
        FirstPersonAudio.Install();
        AimFieldOfView.Install();
        NightVisionEyepieces.Install();
        InteractionRay.Install();
        CameraBeforePostProcessing.Install();
        HideoutEscape.Install();
        HideoutAreaView.Install();
        GrassCamera.Install();
        MagazineCount.Install();
        HudHiding.Install();
        FirstPersonGates.Install();
        NewCameraSwitch.Enabled = enabled.Value;
        NewCameraSwitch.FirstPersonSkeleton = skeleton.Value == SkeletonFirst;
        enabled.SettingChanged += (_, _) =>
        {
            NewCameraSwitch.Enabled = enabled.Value;
            RecoilMode.Refresh();
            HudHiding.Apply();
        };
        skeleton.SettingChanged += (_, _) => NewCameraSwitch.FirstPersonSkeleton = skeleton.Value == SkeletonFirst;
        BindFisheye();
        foreach (HudHiding.Panel panel in HudHiding.Panels)
        {
            ConfigEntry<bool> hidden = SettingsLayout.Bind(Config, "隐藏界面", panel.Key, false, panel.Description);
            hidden.SettingChanged += (_, _) =>
            {
                panel.Hidden = hidden.Value;
                HudHiding.Apply();
            };
            panel.Hidden = hidden.Value;
        }
        HudHiding.Apply();
        BindDirection();
        BindPosition();
        BindAimDistance();
        BindSway();
        BindBody();
        BindVestibular();
        BindNightVision();
        OpticElevation.Initialize();
        SettingsLayout.Arrange(Config);
    }

    private void BindFisheye()
    {
        ConfigEntry<bool> fisheye = SettingsLayout.Bind(Config, "鱼眼", "启用", false,
            new ConfigDescription("画面像透过执法记录仪、运动相机那样的广角镜头看：视野更宽，直线往外弯，四角有镜头的暗角。作用在整个游戏画面上，瞄准时的瞄具、头戴夜视仪和热成像的目镜也一起变形。画面中心是放大过的，由下面的“清晰度方式”决定怎么补。",
                null, new ConfigurationManagerAttributes { Order = 7 }));
        ConfigEntry<float> field = SettingsLayout.Bind(Config, "鱼眼", "视野", 120f,
            new ConfigDescription("鱼眼开着时，屏幕左边缘到右边缘之间的角度（度）。游戏设置里的视野是上下方向的，75 在 16:9 屏幕上大约相当于这里的 107。",
                new AcceptableValueRange<float>(100f, 140f), new ConfigurationManagerAttributes { Order = 6 }));
        ConfigEntry<float> curve = SettingsLayout.Bind(Config, "鱼眼", "弯曲", 0.4f,
            new ConfigDescription("0：不弯，是同样视野的普通广角画面，中心不放大，边缘的东西被拉得很大。1：完全的鱼眼，画面中间往外鼓，边缘压得扁。默认 0.4 接近执法记录仪、运动相机的感觉。",
                new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 5 }));
        ConfigEntry<float> vignette = SettingsLayout.Bind(Config, "鱼眼", "暗角", 0.6f,
            new ConfigDescription("镜头四角变暗：越往边缘越暗，开大后四角露出镜筒的黑色圆边。0 是没有暗角。",
                new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 4 }));
        ConfigEntry<string> clarity = SettingsLayout.Bind(Config, "鱼眼", "清晰度方式", FisheyeLight,
            new ConfigDescription("鱼眼把画面中心放大了，中心会变软。省性能：不提高渲染分辨率，中心用更锐利的插值放大，边缘多点取平均，几乎没有额外开销，这是一般游戏的做法。高清：按更高的分辨率渲染，真正把中心的细节补回来（程度由“清晰度补偿”决定）。高清极吃性能和显存，没有上限保护。",
                new AcceptableValueList<string>(FisheyeLight, FisheyeSharp), new ConfigurationManagerAttributes { Order = 3, CustomDrawer = DrawFisheyeClarity }));
        ConfigEntry<float> detail = SettingsLayout.Bind(Config, "鱼眼", "清晰度补偿", 1f,
            new ConfigDescription("只在清晰度方式为“高清”时起作用。0：不补，没有额外开销。1：把鱼眼对中心的放大补回来，中心和不开鱼眼时一样清楚。大于 1：真正的超采样，中心每个屏幕像素渲染多个像素后再平均，能减轻草地、远处细线的闪烁；2 表示每边 2 倍。代价按平方增长：默认视野和弯曲下，1 约 1.8 倍像素，2 约 7.3 倍；视野和弯曲都开满时，2 约 31 倍。不设上限，帧数和显存都可能撑不住，显存不够时游戏会卡住好几秒。",
                new AcceptableValueRange<float>(0f, 2f), new ConfigurationManagerAttributes { Order = 2 }));
        void Refresh()
        {
            Fisheye.Enabled = fisheye.Value;
            Fisheye.Field = field.Value;
            Fisheye.Curve = curve.Value;
            Fisheye.Vignette = vignette.Value;
            Fisheye.Detail = detail.Value;
            Fisheye.HighResolution = clarity.Value == FisheyeSharp;
        }

        fisheye.SettingChanged += (_, _) => Refresh();
        field.SettingChanged += (_, _) => Refresh();
        curve.SettingChanged += (_, _) => Refresh();
        vignette.SettingChanged += (_, _) => Refresh();
        detail.SettingChanged += (_, _) => Refresh();
        clarity.SettingChanged += (_, _) => Refresh();
        Refresh();
    }

    private void BindNightVision()
    {
        ConfigEntry<KeyboardShortcut> raiseMount = SettingsLayout.Bind(Config, "头戴式成像装备", "挂架升高", new KeyboardShortcut(KeyCode.Equals, KeyCode.LeftAlt),
            new ConfigDescription("按住时把支架往上调（TATM 的滑块、G24 的托架）。只在夜视仪开着、并且处于腰射（没有瞄准）时有效。每个支架（每一件物品）各自记住自己调好的高度、仰俯和前后，存在配置文件夹里的 justachae.thirdpersoncamera.mounts.txt。", null, new ConfigurationManagerAttributes { Order = -1 }));
        ConfigEntry<KeyboardShortcut> lowerMount = SettingsLayout.Bind(Config, "头戴式成像装备", "挂架降低", new KeyboardShortcut(KeyCode.Minus, KeyCode.LeftAlt),
            new ConfigDescription("按住时把支架往下调。只在夜视仪开着、并且处于腰射（没有瞄准）时有效。", null, new ConfigurationManagerAttributes { Order = -2 }));
        ConfigEntry<KeyboardShortcut> tiltUp = SettingsLayout.Bind(Config, "头戴式成像装备", "挂架仰起", new KeyboardShortcut(KeyCode.Equals, KeyCode.LeftShift),
            new ConfigDescription("按住时把支架往上仰（目镜抬高）。只在夜视仪开着、并且处于腰射（没有瞄准）时有效。", null, new ConfigurationManagerAttributes { Order = -4 }));
        ConfigEntry<KeyboardShortcut> tiltDown = SettingsLayout.Bind(Config, "头戴式成像装备", "挂架俯下", new KeyboardShortcut(KeyCode.Minus, KeyCode.LeftShift),
            new ConfigDescription("按住时把支架往下俯（目镜降低）。只在夜视仪开着、并且处于腰射（没有瞄准）时有效。", null, new ConfigurationManagerAttributes { Order = -5 }));
        ConfigEntry<KeyboardShortcut> slideForward = SettingsLayout.Bind(Config, "头戴式成像装备", "挂架往前", new KeyboardShortcut(KeyCode.Equals, KeyCode.LeftControl),
            new ConfigDescription("按住时把夜视仪沿支架往前推（离眼睛远；G24 一格一格地走）。只在夜视仪开着、并且处于腰射（没有瞄准）时有效。", null, new ConfigurationManagerAttributes { Order = -7 }));
        ConfigEntry<KeyboardShortcut> slideBack = SettingsLayout.Bind(Config, "头戴式成像装备", "挂架往后", new KeyboardShortcut(KeyCode.Minus, KeyCode.LeftControl),
            new ConfigDescription("按住时把夜视仪沿支架往后拉（离眼睛近）。只在夜视仪开着、并且处于腰射（没有瞄准）时有效。", null, new ConfigurationManagerAttributes { Order = -8 }));
        ConfigEntry<KeyboardShortcut> headwearUp = SettingsLayout.Bind(Config, "头戴式成像装备", "头部装备仰起", HeadwearTilt.Up,
            new ConfigDescription("按住时把头盔前沿往上抬（只转头盔和挂在上面的夜视仪，头和镜头不动）。每顶头盔各自记住自己的角度，像夜视仪支架一样。只在腰射（没有瞄准）时有效，瞄准时同样的键调瞄准距离。", null, new ConfigurationManagerAttributes { Order = -10 }));
        ConfigEntry<KeyboardShortcut> headwearDown = SettingsLayout.Bind(Config, "头戴式成像装备", "头部装备俯下", HeadwearTilt.Down,
            new ConfigDescription("按住时把头盔前沿往回放（最低回到原位）。", null, new ConfigurationManagerAttributes { Order = -11 }));
        // Saved as "BRNVG", or by its full name with or without "[未安装]": the option as it is named now.
        SettingsLayout.MapSaved(Config, "头戴式成像装备", "夜视画面来源", saved =>
            OtherMods.IsChoice(saved, OtherMods.BrnvgGuid, OtherMods.BrnvgName, "BRNVG") ? NightVisionEyepieces.PictureBrnvg : null);
        ConfigEntry<string> picture = SettingsLayout.Bind(Config, "头戴式成像装备", "夜视画面来源", NightVisionEyepieces.PictureGame,
            new ConfigDescription("头戴夜视仪和热成像只在自己的目镜里成像，目镜以外是不带夜视的原样画面，设备模型照常画出。支持 GPNVG-18、PNV-10T、PNV-57E、Armasight N-15、AN/PVS-14（单筒）、模组物品 PVS-31A 和 T-7 热成像；其他型号仍是游戏原样的全屏滤镜。这一项选目镜里的夜视画面用谁做。自动：装了 Borkel's Realistic NVGs（BRNVG）就用它，没装就用游戏原版的夜视滤镜。Borkel's Realistic NVGs：用它的画面，它的 F12 设置（增益、颜色、噪点、泛光、近处虚焦等）都会作用到目镜里；没装时这一项后面写着 [未安装]，选了也按原版。原版：用游戏自己的夜视滤镜（绿色、噪点），装了 BRNVG 也不用它的画面。热成像设备不受这个选项影响。",
                new AcceptableValueList<string>(NightVisionEyepieces.PictureAuto, NightVisionEyepieces.PictureBrnvg, NightVisionEyepieces.PictureGame),
                new ConfigurationManagerAttributes { Order = 3 }));
        ConfigEntry<bool> glow = SettingsLayout.Bind(Config, "头戴式成像装备", "目镜光晕", false,
            new ConfigDescription("夜视开着时，镜片的光往四周晕开，照在镜片玻璃边缘和眼罩内侧的那一圈光。关掉后只剩镜片里的画面。热成像也一样。",
                null, new ConfigurationManagerAttributes { Order = 0 }));
        ConfigEntry<bool> reflections = SettingsLayout.Bind(Config, "头戴式成像装备", "镜片反光", false,
            new ConfigDescription("夜视仪翻下时，目镜镜片表面映出的周围环境和高光，叠在镜片里的夜视画面上。关掉后镜片里只剩夜视画面，设备上的玻璃部件也不画。",
                null, new ConfigurationManagerAttributes { Order = 0 }));
        ConfigEntry<float> monocularSeeThrough = SettingsLayout.Bind(Config, "头戴式成像装备", "PVS-14 左眼透明度", 0.5f,
            new ConfigDescription("AN/PVS-14 单筒夜视仪只挡在一只眼前（插在双燕尾转接件的哪个槽位，就在哪只眼前），另一只眼看过去时机身往画面边缘逐渐变透明。0：不变透明（两只眼都看到完整的机身）；0.5：默认；1：变透明得最快。",
                new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 0 }));
        ConfigEntry<float> nearBlur = SettingsLayout.Bind(Config, "头戴式成像装备", "近处虚化距离", 1f,
            new ConfigDescription("夜视仪的物镜对焦在远处，近处的手、枪和机瞄是虚的，越近越虚，远处是清楚的。原版夜视和 BRNVG 的画面都会这样，热成像不受影响。距离从夜视仪的物镜算起：在这个距离上虚化是“近处虚化上限”的一半，更近的越来越接近上限，更远的逐渐变清楚。拉长这个距离，枪的前端和更远一点的东西也会更虚。",
                new AcceptableValueRange<float>(0.2f, 3f), new ConfigurationManagerAttributes { Order = 0 }));
        ConfigEntry<float> nearBlurLimit = SettingsLayout.Bind(Config, "头戴式成像装备", "近处虚化上限", 0.75f,
            new ConfigDescription("近处虚化最多糊开多大（模糊圆斑的半径，单位为度）。越近越接近这个值，但不是一刀切：整把枪从后到前仍是越近越虚。0.5 到 1 比较合适；越大，离眼睛近的部分糊得越厉害。",
                new AcceptableValueRange<float>(0.2f, 3f), new ConfigurationManagerAttributes { Order = 0 }));
        ConfigEntry<float> eyeNear = SettingsLayout.Bind(Config, "头戴式成像装备", "双眼近裁剪面（米）", 0.00001f,
            new ConfigDescription("夜视仪翻下时镜片里成像的近裁剪面：离眼睛比这更近的不算。太大时眼睛贴近目镜，那一侧的镜片就不成像；可以调到最小。眼睛画夜视仪外壳时最小按 0.5 毫米算（更近时灯光和阴影不稳，外壳会闪）。和「镜头位置」里主镜头的近裁剪面互不影响。",
                new AcceptableValueRange<float>(0.00001f, 0.02f), new ConfigurationManagerAttributes { Order = 0 }));
        eyeNear.SettingChanged += (_, _) => NightVisionEyepieces.EyeNearPlane = eyeNear.Value;
        NightVisionEyepieces.EyeNearPlane = eyeNear.Value;
        ConfigEntry<float> gameLensBrightness = SettingsLayout.Bind(Config, "头戴式成像装备", "原版夜视目镜亮度", 1f,
            new ConfigDescription("目镜里用游戏原版夜视滤镜成像时（“夜视画面来源”是原版，或没装 Borkel's Realistic NVGs），画面亮度乘以这个数，只作用于目镜里。原版夜视打开时会把整个场景的环境光换成更亮的一套，本模组让目镜以外保持肉眼看到的样子，目镜里也就少了这份提亮；这一项用放大亮度来补，但它只能把有光的地方放大，原本全黑的地方还是黑的，太大时亮处会过曝。1 是不放大。热成像和 Borkel's Realistic NVGs 的画面不受影响。",
                new AcceptableValueRange<float>(1f, 4f), new ConfigurationManagerAttributes { Order = 2 }));
        gameLensBrightness.SettingChanged += (_, _) => NightVisionEyepieces.GameLensBrightness = gameLensBrightness.Value;
        NightVisionEyepieces.GameLensBrightness = gameLensBrightness.Value;
        ConfigEntry<float> tritium = SettingsLayout.Bind(Config, "头戴式成像装备", "氚光亮度", 0.6f,
            new ConfigDescription("透过夜视仪看氚光机瞄（以及游戏里做了氚光的其他瞄具）：每个氚光点被物镜散成一个亮度均匀、边缘清楚的圆盘，离眼睛越近圆盘越大、越暗。这一项调这些圆盘有多亮，0 是不画。",
                new AcceptableValueRange<float>(0f, 4f), new ConfigurationManagerAttributes { Order = 0 }));
        ConfigEntry<float> eyeShift = SettingsLayout.Bind(Config, "头戴式成像装备", "双眼平移比例", 0.8f,
            new ConfigDescription("两个目镜往中间合多少：1 是各自正好对准画面中间，0 是不合。画面里的景物始终完整，这里只改变目镜的样子。",
                new AcceptableValueRange<float>(0f, 1.2f), new ConfigurationManagerAttributes { Order = 1 }));
        void Refresh()
        {
            NightVisionEyepieces.Picture = picture.Value;
            NightVisionEyepieces.TritiumBrightness = tritium.Value;
            NightVisionEyepieces.NearBlurDistance = nearBlur.Value;
            NightVisionEyepieces.NearBlurLimit = nearBlurLimit.Value;
            HeadwearTilt.Up = headwearUp.Value;
            HeadwearTilt.Down = headwearDown.Value;
            NightVisionEyepieces.EyepieceGlow = glow.Value;
            NightVisionEyepieces.LensReflections = reflections.Value;
            NightVisionEyepieces.MonocularSeeThrough = monocularSeeThrough.Value;
            NightVisionEyepieces.EyeShift = eyeShift.Value;
            NightVisionEyepieces.RaiseMount = raiseMount.Value;
            NightVisionEyepieces.LowerMount = lowerMount.Value;
            NightVisionEyepieces.TiltUp = tiltUp.Value;
            NightVisionEyepieces.TiltDown = tiltDown.Value;
            NightVisionEyepieces.SlideForward = slideForward.Value;
            NightVisionEyepieces.SlideBack = slideBack.Value;
        }

        eyeShift.SettingChanged += (_, _) => Refresh();
        picture.SettingChanged += (_, _) => Refresh();
        nearBlur.SettingChanged += (_, _) => Refresh();
        tritium.SettingChanged += (_, _) => Refresh();
        nearBlurLimit.SettingChanged += (_, _) => Refresh();
        headwearUp.SettingChanged += (_, _) => Refresh();
        headwearDown.SettingChanged += (_, _) => Refresh();
        glow.SettingChanged += (_, _) => Refresh();
        reflections.SettingChanged += (_, _) => Refresh();
        monocularSeeThrough.SettingChanged += (_, _) => Refresh();
        raiseMount.SettingChanged += (_, _) => Refresh();
        lowerMount.SettingChanged += (_, _) => Refresh();
        tiltUp.SettingChanged += (_, _) => Refresh();
        tiltDown.SettingChanged += (_, _) => Refresh();
        slideForward.SettingChanged += (_, _) => Refresh();
        slideBack.SettingChanged += (_, _) => Refresh();
        Refresh();
    }

    private void BindBody()
    {
        ConfigEntry<float> walkHead = SettingsLayout.Bind(Config, "人物", "走路摇头", 0.2f,
            new ConfigDescription("走路和跑步时，把头的左右转动收到平均朝向上。镜头仍按原来的固定位置挂在头上，和头一起转。0 是不摇头，1 是游戏原样。站着不动和中键扭头不受影响。",
                new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 1 }));
        MoveFrom(walkHead, "新镜头", "走路摇头");
        walkHead.SettingChanged += (_, _) => AimArms.WalkHeadSway = walkHead.Value;
        AimArms.WalkHeadSway = walkHead.Value;
        ConfigEntry<bool> showHead = SettingsLayout.Bind(Config, "人物", "显示头部", false,
            "画出自己的头。关掉时头只投阴影，画面里看不见。");
        ConfigEntry<bool> showHeadwear = SettingsLayout.Bind(Config, "人物", "显示头部装备", true,
            "画出头盔和帽子。关掉后头部装备不画，但头盔上挂的夜视仪、热成像和它们的支架照常画出（用来对付全封闭头盔挡住视线）。");
        ConfigEntry<bool> showFaceCover = SettingsLayout.Bind(Config, "人物", "显示面部装备", false,
            "画出面罩和头套。关掉后整个面部装备都不画。");
        ConfigEntry<bool> showEyewear = SettingsLayout.Bind(Config, "人物", "显示眼部装备", false,
            "画出眼镜和护目镜。关掉后整个眼部装备都不画。");
        ConfigEntry<bool> showVisor = SettingsLayout.Bind(Config, "人物", "显示面罩滤镜", false,
            "画出面罩玻璃上的滤镜。关掉后玻璃还在，滤镜画面没有。");
        ConfigEntry<float> headPitch = SettingsLayout.Bind(Config, "人物", "头部仰俯", 17f,
            new ConfigDescription("只转头部模型，镜头保持原位。正值把脸往上抬，单位为度。",
                new AcceptableValueRange<float>(-45f, 45f)));
        ConfigEntry<float> freeLookYaw = SettingsLayout.Bind(Config, "人物", "中键扭头角度", 80f,
            new ConfigDescription("按住鼠标中键自由看时，头最多往左或往右转多少度。",
                new AcceptableValueRange<float>(30f, 150f)));
        ConfigEntry<float> freeLookDown = SettingsLayout.Bind(Config, "人物", "中键低头额外角度", 20f,
            new ConfigDescription("按住鼠标中键自由看时，在原版能低头的角度上再多低多少度。0 是原版的范围。",
                new AcceptableValueRange<float>(0f, 60f)));
        ConfigEntry<float> axisForward = SettingsLayout.Bind(Config, "人物", "中键扭头轴前后倾斜", 5f,
            new ConfigDescription("中键左右扭头时绕的那根轴，0 是竖直。正值让轴的上端往前倒，负值往后倒，单位为度。",
                new AcceptableValueRange<float>(-60f, 60f)));
        void Refresh()
        {
            AimArms.FreeLookYaw = freeLookYaw.Value;
            AimArms.FreeLookDownExtra = freeLookDown.Value;
            AimArms.LookAxisForward = axisForward.Value;
            BodyVisibility.ShowHead = showHead.Value;
            BodyVisibility.ShowHeadwear = showHeadwear.Value;
            BodyVisibility.ShowFaceCover = showFaceCover.Value;
            BodyVisibility.ShowEyewear = showEyewear.Value;
            VisorFilter.Show = showVisor.Value;
            AimArms.HeadModelPitch = headPitch.Value;
        }

        showHead.SettingChanged += (_, _) => Refresh();
        showHeadwear.SettingChanged += (_, _) => Refresh();
        showFaceCover.SettingChanged += (_, _) => Refresh();
        showEyewear.SettingChanged += (_, _) => Refresh();
        showVisor.SettingChanged += (_, _) => Refresh();
        headPitch.SettingChanged += (_, _) => Refresh();
        freeLookYaw.SettingChanged += (_, _) => Refresh();
        freeLookDown.SettingChanged += (_, _) => Refresh();
        axisForward.SettingChanged += (_, _) => Refresh();
        Refresh();
    }

    // A setting that moved to another section takes over the value saved under its old place, once.
    private void MoveFrom(ConfigEntryBase entry, string oldSection, string oldKey)
    {
        PropertyInfo? property = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic);
        if (property?.GetValue(Config) is not Dictionary<ConfigDefinition, string> orphans)
            return;

        ConfigDefinition old = new ConfigDefinition(oldSection, oldKey);
        if (!orphans.TryGetValue(old, out string value))
            return;
        orphans.Remove(old);
        entry.SetSerializedValue(value);
        Config.Save();
    }

    private void BindVestibular()
    {
        ConfigEntry<bool> balance = SettingsLayout.Bind(Config, "前庭平衡", "总开关", true,
            new ConfigDescription("像内耳前庭带着眼球那样，镜头位置不动，只把镜头转回玩家想看的方向，抵消走路、跑步时头的晃动。注视点取视线上实际看到的东西的距离；切枪、换弹、打药、翻背包、搜刮、开门等动作时，动画让头转去看手或包的部分会保留。开枪时后坐力给镜头的转动不受影响。开镜（胸口抵肩除外）、架枪、固定武器、翻越、攀爬时不起作用；中键自由看保留。关闭后镜头完全按头部原样晃动。",
                null, new ConfigurationManagerAttributes { Order = 3 }));
        ConfigEntry<float> strength = SettingsLayout.Bind(Config, "前庭平衡", "强度", 0.7f,
            new ConfigDescription("左右和上下平衡多少。0 是不平衡（原样），1 是完全平衡。",
                new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 2 }));
        ConfigEntry<float> torsion = SettingsLayout.Bind(Config, "前庭平衡", "扭转补偿", 0.25f,
            new ConfigDescription("头绕视线歪斜（翻滚）时，镜头反向扭回多少。人眼只能反向扭回头部歪斜的一小部分（约一到三成），所以默认 0.25。0 是完全不管翻滚，1 是歪斜全部扭回。探头的歪斜也按这个比例减小。最终效果再乘上强度。",
                new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 1 }));
        void Refresh()
        {
            VestibularView.Enabled = balance.Value;
            VestibularView.Strength = strength.Value;
            VestibularView.Torsion = torsion.Value;
        }

        balance.SettingChanged += (_, _) => Refresh();
        strength.SettingChanged += (_, _) => Refresh();
        torsion.SettingChanged += (_, _) => Refresh();
        Refresh();
    }

    private void BindDirection()
    {
        ConfigEntry<float> pitch = BindDegrees("镜头朝向", "俯仰", -90f, "头骨坐标到镜头坐标的俯仰对齐，单位为度。");
        ConfigEntry<float> yaw = BindDegrees("镜头朝向", "偏航", -20f, "头骨坐标到镜头坐标的偏航对齐，单位为度。");
        ConfigEntry<float> roll = BindDegrees("镜头朝向", "翻滚", 90f, "头骨坐标到镜头坐标的翻滚对齐，单位为度。");
        void Refresh()
        {
            CameraDirection.Pitch = pitch.Value;
            CameraDirection.Yaw = yaw.Value;
            CameraDirection.Roll = roll.Value;
        }

        pitch.SettingChanged += (_, _) => Refresh();
        yaw.SettingChanged += (_, _) => Refresh();
        roll.SettingChanged += (_, _) => Refresh();
        Refresh();
    }

    private void BindPosition()
    {
        ConfigEntry<float> horizontal = BindMeters("镜头位置", "水平", 0f, "沿镜头右方向移动，单位为米。");
        ConfigEntry<float> vertical = BindMeters("镜头位置", "垂直", 0.05f, "沿镜头上方向移动，单位为米。");
        ConfigEntry<float> forward = BindMeters("镜头位置", "前后", 0.115f, "沿镜头前方向移动，单位为米。");
        ConfigEntry<bool> aimEye = SettingsLayout.Bind(Config, "镜头位置", "瞄准时移到瞄准眼", true,
            "开镜时，镜头的水平位置平滑移到瞄准用的那只眼：右肩时到右眼，换到左肩时到左眼，枪跟着一起过去；胸口抵肩时镜头不动，枪的姿势仍按移到那只眼时摆；退出瞄准时回到上面设的水平位置。");
        ConfigEntry<float> near = SettingsLayout.Bind(Config, "镜头位置", "近裁剪面（米）", 0.01f,
            new ConfigDescription("离镜头比这个距离还近的东西不画。游戏在地图里是 0.03 米（藏身处 0.01 米），镜头挂在头上、贴着头盔帽檐，帽檐靠近镜头的一截会被切掉，从切口透出后面的景物。调小可以把更近的东西也画出来。不会比游戏原来的距离更远。",
                new AcceptableValueRange<float>(0.005f, 0.03f)));
        near.SettingChanged += (_, _) => CameraNear.Near = near.Value;
        CameraNear.Near = near.Value;
        ConfigEntry<float> eye = SettingsLayout.Bind(Config, "镜头位置", "瞄准眼离中线（米）", 0.03f,
            new ConfigDescription("开镜时镜头离头部中线多远：右眼是正值这么远，左眼是负值这么远。", new AcceptableValueRange<float>(0f, 0.06f)));
        void Refresh()
        {
            CameraPosition.Across = horizontal.Value;
            CameraPosition.Horizontal = horizontal.Value;
            CameraPosition.AimEyeEnabled = aimEye.Value;
            CameraPosition.AimEye = eye.Value;
            CameraPosition.Vertical = vertical.Value;
            CameraPosition.Forward = forward.Value;
        }

        horizontal.SettingChanged += (_, _) => Refresh();
        vertical.SettingChanged += (_, _) => Refresh();
        forward.SettingChanged += (_, _) => Refresh();
        aimEye.SettingChanged += (_, _) => Refresh();
        eye.SettingChanged += (_, _) => Refresh();
        Refresh();
    }

    private void BindAimDistance()
    {
        RecoilMode.Find();
        ConfigEntry<bool> useRework = SettingsLayout.Bind(Config, "瞄准", "使用 Recoil Rework", false,
            new ConfigDescription("关：原版后坐力，游戏自己的后坐力按原版第一人称的整套做法放到第三人称的枪和镜头上：枪往后顶、枪口上翻（腰射也翻，子弹跟着枪口），瞄准时镜头追着瞄具、跟着枪口转过去，再加上镜头自己的那一下，力度由下面的“后坐力力度”决定。开：别人的后坐力 mod SPTRecoilReworkLegacy，和不装本 mod、只装它时一样：枪和镜头按它调过的数值走，镜头不跟着枪口转（它把这一下关掉），它把视角往上拉、它给镜头的抖动都照常有，“后坐力力度”不起作用。没装它时这一项名字后面写着 [未安装]，不能打开；关着时它的改动全部撤下，打开时再装回去。它的服务端部分改了一些枪的后坐数值，那部分在这里关不掉，关着时也带着。局内可以随时切换，切换后换一次枪才完全生效。",
                null, new ConfigurationManagerAttributes
                {
                    // Shown by the mod's full name; not installed, marked so and not changeable.
                    DispName = OtherMods.Label(OtherMods.ReworkGuid, OtherMods.ReworkName),
                    ReadOnly = !RecoilMode.ReworkInstalled,
                }));
        useRework.SettingChanged += (_, _) => RecoilMode.Set(useRework.Value);
        RecoilMode.Set(useRework.Value);
        _aimDistance = SettingsLayout.Bind(Config, "瞄准", "前后距离", -0.05f,
            new ConfigDescription("瞄准后沿镜头前方向移动枪，单位为米。负值让枪离镜头更近。",
                new AcceptableValueRange<float>(AimDistanceMin, AimDistanceMax)));
        _aimDistanceFarther = SettingsLayout.Bind(Config, "瞄准", "前后距离调远快捷键", new KeyboardShortcut(KeyCode.Equals),
            "瞄准时按住，瞄准的前后距离慢慢变大，枪离镜头更远。不瞄准时这个键不起作用（腰射时同样的键调夜视仪挂架）。");
        _aimDistanceCloser = SettingsLayout.Bind(Config, "瞄准", "前后距离调近快捷键", new KeyboardShortcut(KeyCode.Minus),
            "瞄准时按住，瞄准的前后距离慢慢变小，枪离镜头更近。不瞄准时这个键不起作用（腰射时同样的键调夜视仪挂架）。");
        ConfigEntry<float> recoilStrength = SettingsLayout.Bind(Config, "瞄准", "后坐力力度", 1f,
            new ConfigDescription("原版后坐力的力度：枪往后顶、枪口上翻、镜头跟着枪口转、镜头自己的那一下，一起按这个数放大或缩小。1 是原版第一人称，0 是都没有。子弹跟着枪口走。打开“使用 Recoil Rework”时不起作用（那时按它自己的设置）。",
                new AcceptableValueRange<float>(0f, 2f)));
        recoilStrength.SettingChanged += (_, _) => AimArms.RecoilStrength = recoilStrength.Value;
        AimArms.RecoilStrength = recoilStrength.Value;
        _aimDistance.SettingChanged += (_, _) => AimArms.ForwardDistance = _aimDistance.Value;
        AimArms.ForwardDistance = _aimDistance.Value;
    }

    private const float AimDistanceMin = -0.10f;
    private const float AimDistanceMax = 0f;
    private const float AimDistanceSpeed = 0.05f;
    private ConfigEntry<float>? _aimDistance;
    private ConfigEntry<KeyboardShortcut>? _aimDistanceFarther;
    private ConfigEntry<KeyboardShortcut>? _aimDistanceCloser;

    private void AdjustAimDistance()
    {
        if (_aimDistance == null || _aimDistanceFarther == null || _aimDistanceCloser == null || !NewCameraSwitch.Enabled)
            return;
        // Only while aiming: the same keys set the helmet mount at the hip.
        // Not in the stance at the chest (its place is set in the settings).
        bool aiming = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer?.ProceduralWeaponAnimation?.IsAiming == true && !ChestStance.Active;
        float step = 0f;
        if (aiming && _aimDistanceFarther.Value.IsPressed())
            step += AimDistanceSpeed * Time.deltaTime;
        if (aiming && _aimDistanceCloser.Value.IsPressed())
            step -= AimDistanceSpeed * Time.deltaTime;

        // Setting the config value saves the whole file, so it is written once when the key is let go.
        if (step != 0f)
        {
            AimArms.ForwardDistance = Mathf.Clamp(AimArms.ForwardDistance + step, AimDistanceMin, AimDistanceMax);
            _aimDistanceHeld = true;
        }
        else if (_aimDistanceHeld)
        {
            _aimDistanceHeld = false;
            _aimDistance.Value = AimArms.ForwardDistance;
        }
    }

    private bool _aimDistanceHeld;

    // The follow delay and the view's share of the recoil are the code's own (AimArms), with no settings.
    private void BindSway()
    {
        BindSwaySet("瞄准摆动-平时", "平时（不屏息、手臂有体力）", AimArms.Normal, 0.05f, 0.1f, 1f);
        BindSwaySet("瞄准摆动-屏息", "按住左 Alt 屏息时", AimArms.Holding, 0f, 0f, 0f);
        BindSwaySet("瞄准摆动-没体力", "手臂没体力时（优先于屏息）", AimArms.Exhausted, 1f, 1f, 1f);
        BindChestStance();
    }

    private void BindChestStance()
    {
        const string section = "胸口抵肩";
        ConfigEntry<bool> enabled = SettingsLayout.Bind(Config, section, "启用", true,
            "按住瞄准键不放时，不把瞄具对到眼前，而是枪托抵在胸口正中、枪放低并向内倾斜，枪管和视线平行，靠激光指示器瞄准（戴夜视仪时的常用姿势）。点一下瞄准键仍是正常开镜（松开时开镜）；按住超过下面的时间就直接进入胸口抵肩，松开就退出瞄准。摆动、呼吸、后坐力和开镜时一样，走路速度不按开镜减慢，鼠标灵敏度和红点、全息开镜时一样，前庭平衡照常起作用。游戏里瞄准键设成长按瞄准时两者对换：按住是正常开镜（松开退出），点一下进入胸口抵肩并保持，再点一下退出，保持中按住则转为正常开镜，松开回到胸口抵肩。设成双击瞄准时：双击是正常开镜，按住进入胸口抵肩，松开退出（和点击瞄准时一样）。");
        ConfigEntry<KeyCode> key = SettingsLayout.Bind(Config, section, "瞄准键", KeyCode.Mouse1,
            "游戏里绑定的瞄准键（默认鼠标右键 Mouse1），用来判断是否还按着。改了游戏里的瞄准键时这里也要改。");
        ConfigEntry<float> hold = SettingsLayout.Bind(Config, section, "按住多久算按住（秒）", 0.15f,
            new ConfigDescription("瞄准键按下后，这个时间内松开算点一下（松开时正常开镜），超过这个时间还按着算按住（直接进入胸口抵肩；游戏里设成长按瞄准时反过来）。越短按住时等得越少，但慢一点的点击可能被当成按住。", new AcceptableValueRange<float>(0.1f, 1f)));
        ConfigEntry<float> drop = SettingsLayout.Bind(Config, section, "比开镜时低（米）", 0.05f,
            new ConfigDescription("胸口抵肩时，枪比正常开镜时的位置低多少（负数是更高）。", new AcceptableValueRange<float>(-0.1f, 0.3f)));
        ConfigEntry<float> side = SettingsLayout.Bind(Config, section, "比开镜时偏右（米）", -0.05f,
            new ConfigDescription("胸口抵肩时，枪比正常开镜时的位置往右多少（负数是往左）。", new AcceptableValueRange<float>(-0.15f, 0.15f)));
        ConfigEntry<float> ahead = SettingsLayout.Bind(Config, section, "比开镜时靠前（米）", 0f,
            new ConfigDescription("胸口抵肩时，枪比正常开镜时的位置往前多少（负数是往后）。", new AcceptableValueRange<float>(-0.15f, 0.15f)));
        ConfigEntry<float> cant = SettingsLayout.Bind(Config, section, "倾斜（度）", 20f,
            new ConfigDescription("胸口抵肩时，枪绕着瞄准线转多少度，正数是枪的上面往左倒（向内），负数往右。", new AcceptableValueRange<float>(-60f, 60f)));
        // Two decimals: the slider's value rounded to hundredths of a second.
        float rounded = Mathf.Round(hold.Value * 100f) / 100f;
        if (rounded != hold.Value)
            hold.Value = rounded;
        hold.SettingChanged += (_, _) =>
        {
            float hundredths = Mathf.Round(hold.Value * 100f) / 100f;
            if (hundredths != hold.Value)
                hold.Value = hundredths;
        };
        void Refresh()
        {
            ChestStance.Enabled = enabled.Value;
            ChestStance.Key = key.Value;
            ChestStance.HoldSeconds = hold.Value;
            ChestStance.Drop = drop.Value;
            ChestStance.Ahead = ahead.Value;
            ChestStance.Side = side.Value;
            ChestStance.Cant = cant.Value;
        }
        enabled.SettingChanged += (_, _) => Refresh();
        key.SettingChanged += (_, _) => Refresh();
        hold.SettingChanged += (_, _) => Refresh();
        drop.SettingChanged += (_, _) => Refresh();
        ahead.SettingChanged += (_, _) => Refresh();
        side.SettingChanged += (_, _) => Refresh();
        cant.SettingChanged += (_, _) => Refresh();
        Refresh();
    }

    private void BindSwaySet(string section, string when, AimArms.SwayAmounts target, float sway, float headFollowDefault, float breathDefault)
    {
        ConfigEntry<float> swayAmount = BindAmount(section, "摆动", sway,
            when + "：枪和手臂快的晃动（转视角时的甩动、走路、手上动作等）带来的平移和转动。0 是瞄具钉在眼前不动，1 是游戏原样。");
        ConfigEntry<float> headFollow = BindAmount(section, "跟随头部", headFollowDefault,
            when + "：瞄准方向跟着头的晃动（呼吸、走路颠簸、姿势带来的偏角）走多少，镜头和枪一起跟。0 是只认鼠标方向（探头的歪斜照样保留），1 是完全跟着头。");
        ConfigEntry<float> breath = BindAmount(section, "呼吸", breathDefault,
            when + "：手臂和枪跟着慢晃（多半是呼吸）转多少。摆动滑条只管快的晃动，这个管更慢的那部分。0 是不跟着转，1 是游戏原样。");
        void Refresh()
        {
            target.GunMove = swayAmount.Value;
            target.GunTurn = swayAmount.Value;
            target.ArmMove = swayAmount.Value;
            target.ArmTurn = swayAmount.Value;
            target.HeadFollow = headFollow.Value;
            target.Breath = breath.Value;
        }

        swayAmount.SettingChanged += (_, _) => Refresh();
        headFollow.SettingChanged += (_, _) => Refresh();
        breath.SettingChanged += (_, _) => Refresh();
        Refresh();
    }

    private ConfigEntry<float> BindAmount(string section, string key, float defaultValue, string description)
    {
        return SettingsLayout.Bind(Config, section, key, defaultValue,
            new ConfigDescription(description, new AcceptableValueRange<float>(0f, 1f)));
    }

    private ConfigEntry<float> BindDegrees(string section, string key, float defaultValue, string description)
    {
        return SettingsLayout.Bind(Config, section, key, defaultValue,
            new ConfigDescription(description, new AcceptableValueRange<float>(-180f, 360f)));
    }

    private ConfigEntry<float> BindMeters(string section, string key, float defaultValue, string description)
    {
        return SettingsLayout.Bind(Config, section, key, defaultValue,
            new ConfigDescription(description, new AcceptableValueRange<float>(-0.05f, 1f)));
    }

    // The camera callbacks are each one delegate among the game's: one that throws stops the rest for that camera (the
    // game lights lights there, per camera), so each of ours is kept from throwing; a failure is logged once.
    internal static void GuardedPreCull(Camera camera) => Guarded(NewCameraSwitch.OnCameraPreCull, camera, "pre-cull (camera, before post-processing)");

    private static readonly Camera.CameraCallback PreCull = camera =>
    {
        Guarded(Fisheye.Reassert, camera, "pre-cull (fisheye)");
        Guarded(NewCameraSwitch.OnCameraPreCull, camera, "pre-cull (camera)");
        Guarded(NightVisionEyepieces.OnCameraPreCull, camera, "pre-cull (eyepieces)");
        Guarded(NightVisionEyepieces.OnOpticPreCullIr, camera, "pre-cull (optic IR)");
    };
    private static readonly Camera.CameraCallback PreRender = camera =>
    {
        Guarded(MagazineCount.OnCameraPreRender, camera, "pre-render (magazine count)");
        Guarded(NightVisionEyepieces.NoteRendered, camera, "pre-render (objective)");
        Guarded(NightVisionEyepieces.OnCameraPreRender, camera, "pre-render (eyepieces)");
        Guarded(NightVisionEyepieces.OnCameraPreRenderIr, camera, "pre-render (IR)");
    };
    private static readonly Camera.CameraCallback PostRender = camera =>
    {
        Guarded(NightVisionEyepieces.OnCameraPostRender, camera, "post-render (eyepieces)");
        Guarded(NightVisionEyepieces.OnCameraPostRenderIr, camera, "post-render (IR)");
        Guarded(OpticElevation.Restore, camera, "post-render (optic)");
        Guarded(NightVisionEyepieces.OnOpticPostRenderIr, camera, "post-render (optic IR)");
    };
    private static readonly System.Collections.Generic.HashSet<string> Reported = new System.Collections.Generic.HashSet<string>();

    private static void Guarded(Camera.CameraCallback callback, Camera camera, string name)
    {
        try
        {
            callback(camera);
        }
        catch (System.Exception e)
        {
            if (Reported.Add(name + e.GetType().Name + e.TargetSite))
                Log?.LogError($"Camera {name} failed (reported once): {e}");
        }
    }

    private void OnEnable()
    {
        StartCoroutine(EndOfFrame());
        Camera.onPreCull += PreCull;
        Camera.onPreRender += PreRender;
        Camera.onPostRender += PostRender;
    }

    // The fisheye's field handed back to the game once the frame is drawn, before its next frame's updates read it.
    private static System.Collections.IEnumerator EndOfFrame()
    {
        WaitForEndOfFrame end = new WaitForEndOfFrame();
        while (true)
        {
            yield return end;
            Fisheye.GiveBack();
        }
    }

    private void OnDisable()
    {
        Fisheye.GiveBack();
        Camera.onPreCull -= PreCull;
        Camera.onPreRender -= PreRender;
        Camera.onPostRender -= PostRender;
        OpticElevation.Shutdown();
    }

    private void Update()
    {
        OpticElevation.UpdateHeld();
        AdjustAimDistance();
        DovetailSlots.Update();
    }

    private void LateUpdate()
    {
        NewCameraSwitch.LateUpdate();
        HeadwearTilt.UpdateShown();
        NightVisionEyepieces.UpdatePreviewMounts();
        Fisheye.LateUpdate();
    }
}

internal static class NewCameraSwitch
{
    internal static bool Enabled;
    internal static bool FirstPersonSkeleton;
    private static bool _applied;
    private static int _poseFrame = -1;
    private static Vector3 _posePosition;

    // The camera is where this mod put it this frame, at the player's eyes, and has not been moved off since (a free
    // camera places itself after this mod does).
    internal static bool AtEyes(Camera camera) => _poseFrame == Time.frameCount && (camera.transform.position - _posePosition).sqrMagnitude < 1e-6f;
    private static Quaternion _poseRotation;

    // Where this mod has the camera for this player (last placed: this frame, or the one before if not yet this one).
    internal static bool CameraOf(Player player, out Vector3 position, out Quaternion rotation)
    {
        position = _posePosition;
        rotation = _poseRotation;
        return _applied && ReferenceEquals(player, _appliedTo) && _poseFrame >= Time.frameCount - 1;
    }

    // Where the camera was put from the head this frame (along the world's axes), as it was worked out, not as the
    // difference of the two places in the world: far out in a map those are rounded to tens of micrometres.
    internal static Transform? PoseHead;
    internal static Vector3 PoseFromHead;

    internal static void LateUpdate()
    {
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        bool mine = player != null && player.IsYourPlayer;
        // The hideout overview (before walking in) and the gym use the game's free camera.
        bool wanted = Enabled && mine && player!.PointOfView != EPointOfView.FreeCamera;
        // Let go of whenever this mod stops applying: switched off, the free camera, or the player gone or another
        // one (a raid over), whether or not there is a player to hand things back to.
        if (_applied && (!wanted || !ReferenceEquals(player, _appliedTo)))
        {
            ReleaseAll(ReferenceEquals(player, _appliedTo) ? player : null);
            _applied = false;
            _appliedTo = null;
        }
        if (!wanted)
            return;

        if (player!.PointOfView != EPointOfView.ThirdPerson)
            player.PointOfView = EPointOfView.ThirdPerson;
        if (!_applied)
        {
            AimFieldOfView.Reset();
            VestibularView.Reset();
        }
        ChooseSkeleton(player);
        CameraNear.Apply();
        ChestStance.Update(player);
        CameraPosition.UpdateAimEye(player);
        AimArms.Apply(player);
        HideoutCinemachine.Update(player);
        HideoutGear.Update(player);
        NightVisionEyepieces.Update(player);
        BodyVisibility.Apply(player);
        _applied = true;
        _appliedTo = player;
    }

    // The body's animation, all else as the third-person view has it: the first-person skeleton is the game's
    // first-person animator (no breathing sway, no roll of the head turning at a run, no body moves for changing the
    // weapon or opening the backpack). The game sets it as it sets the point of view: the first-person one is set
    // every frame, the third-person one again once chosen back.
    private static void ChooseSkeleton(Player player)
    {
        bool first = FirstPersonSkeleton;
        if (!first && !_firstSkeleton && ReferenceEquals(_skeletonOf, player))
            return;
        player.MovementContext?.PlayerAnimatorPointOfView(first ? EPointOfView.FirstPerson : EPointOfView.ThirdPerson);
        _firstSkeleton = first;
        _skeletonOf = player;
    }

    private static bool _firstSkeleton;
    private static Player? _skeletonOf;
    private static Player? _appliedTo;

    // Everything this mod changed handed back, as the game had it; player: the one it was applied to, when still there.
    private static void ReleaseAll(Player? player)
    {
        CameraPosition.ResetAimEye();
        CameraNear.Release();
        NightVisionEyepieces.Release();
        CollimatorHandoff.Release();
        OpticPicture.Release(player);
        RangefinderSight.ClearReading();
        AimArms.Release(player);
        BodyVisibility.Restore();
        HeadwearTilt.Release();
        HideoutGear.Release(player);
        HideoutCinemachine.Release();
        _skeletonOf = null;
        if (player == null || Enabled)
            return;
        // Switched off: the game's own view back at once, not at the next aim or pose change.
        if (player.PointOfView != EPointOfView.FirstPerson)
            player.PointOfView = EPointOfView.FirstPerson;
        AimArms.GiveBackAim(player);
    }

    private static int _mainPlacedFrame = -1;

    // Where the camera was last placed, if that was just now.
    internal static bool LastPose(out Vector3 position, out Quaternion rotation)
    {
        position = _posePosition;
        rotation = _poseRotation;
        return _poseFrame >= 0 && Time.frameCount - _poseFrame <= 2;
    }

    internal static void OnCameraPreCull(Camera camera)
    {
        if (!Enabled || camera == null)
            return;

        bool main = camera.CompareTag("MainCamera");
        bool optic = OpticPicture.IsCamera(camera);
        if (!main && !optic)
            return;

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (main && HideoutWorkout.Place(player, camera))
            return;
        if (player != null && player.IsYourPlayer && player.PointOfView == EPointOfView.FreeCamera)
            return;

        if (player == null || !player.IsYourPlayer)
        {
            LaserReplay.Replay(camera);
            return;
        }

        // Once a frame for the FPS camera, at whichever comes first: its post-processing's own OnPreCull, or the
        // cameras' pre-cull callback (CameraBeforePostProcessing). The second only finds it placed.
        if (main && _mainPlacedFrame == Time.frameCount)
        {
            if (_poseFrame == Time.frameCount)
                camera.transform.SetPositionAndRotation(_posePosition, _poseRotation);
            return;
        }
        if (main)
            _mainPlacedFrame = Time.frameCount;

        if (main)
            OpticPicture.Apply(player);

        if (_poseFrame != Time.frameCount)
        {
            Transform? head = player.PlayerBones?.Head?.Original;
            if (head != null)
            {
                AimArms.UnliftHead(head);
                AimArms.FollowMountedGun(player);
                _poseRotation = CameraDirection.Resolve(head.rotation);
                // A magnified sight through the goggle: the camera (the objective) ahead of the eyes, the gun with it.
                CameraPosition.ScopeAhead = 0f;
                CameraPosition.ScopeAhead = NightVisionEyepieces.ScopePull(player, CameraPosition.Resolve(head.position, _poseRotation), _poseRotation);
                _posePosition = CameraPosition.Resolve(head.position, _poseRotation);
                PoseFromHead = CameraPosition.Offset(_poseRotation);
                AimArms.ObserveHead(player, _posePosition, _poseRotation);
                AimArms.AlignToCamera(player, _posePosition, _poseRotation);
                if (AimArms.TrackMountedSight(player))
                {
                    _poseRotation = CameraDirection.Resolve(head.rotation);
                    _posePosition = CameraPosition.Resolve(head.position, _poseRotation);
                    PoseFromHead = CameraPosition.Offset(_poseRotation);
                }
                _poseRotation = VestibularView.Stabilize(player, _posePosition, _poseRotation);
                // The recoil on the view, in the order the game's first-person camera takes it (LerpCamera): its turn
                // after the gun's flip (aimed), the camera recoil, then what Recoil Rework adds there when it is working.
                _poseRotation = AimArms.FollowGun(player, _poseRotation);
                _poseRotation = AimArms.CameraRecoil(player, _poseRotation);
                if (RecoilMode.ReworkCameraShake(player, out Vector3 shakeMove, out Vector3 shakeTurn))
                {
                    _posePosition += _poseRotation * shakeMove;
                    PoseFromHead += _poseRotation * shakeMove;
                    _poseRotation *= Quaternion.Euler(shakeTurn);
                }
                PoseHead = head;
                AimArms.LiftHead(head);
                _poseFrame = Time.frameCount;
            }
        }

        if (main && _poseFrame == Time.frameCount)
        {
            camera.transform.SetPositionAndRotation(_posePosition, _poseRotation);
            NightVisionEyepieces.RedrawAmbient(camera);
        }
        if (optic && _poseFrame == Time.frameCount)
        {
            AimArms.CarryOptic(camera);
            RangefinderSight.DrawReading(player, camera);
        }

        LaserReplay.Replay(camera);
        if (optic)
            OpticElevation.Apply(camera);
        if (main)
        {
            CollimatorHandoff.Update(camera);
        }
    }
}
