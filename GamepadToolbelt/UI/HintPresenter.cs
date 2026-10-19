using GamepadToolbelt.Core;
using GamepadToolbelt.Input;
using GUI_2;
using Platform;
using UnityEngine;

namespace GamepadToolbelt.UI;

/// <summary>
/// Hint state shared by all toolbelt slots, computed once per frame: chord state, controller mode, overall visibility
/// (eased), the XP bar lift and the glyph style. Controller mode is the game's input style - or recent activity of the
/// chord buttons, because owned presses never reach the game and so cannot switch its input style.
/// </summary>
internal static class HintPresenter
{
    public const double PadActivityWindowSeconds = 8d;
    public const double GateCloseDelaySeconds = 0.2d;

    private static int frame = -1;
    private static double gateClosedSince = -1d;
    private static PlayerInputManager.InputStyle lastIconStyle = PlayerInputManager.InputStyle.Undefined;

    public static ChordUiState Ui;
    public static double Now;
    public static float DeltaTime;
    public static int SlotCount = ToolbeltConfigXml.VanillaSlots;
    public static double TapMaxSeconds = 0.35d;
    public static float IdleOpacity = 0.8f;
    public static bool Enabled;
    public static bool ControllerMode;
    public static bool Visible;

    /// <summary>0..1, eased: multiplies every plate's opacity (fades all hints in and out together).</summary>
    public static float GlobalAlpha;

    /// <summary>Pixels the XP bar is lifted (eased between 0 and <see cref="HintMotion.XpLift"/>).</summary>
    public static float XpOffset;

    /// <summary>Changes whenever the glyph family (Xbox / PlayStation) changes, so slots refresh their sprites.</summary>
    public static int StyleVersion;

    public static void Update()
    {
        int current = Time.frameCount;
        if (current == frame)
        {
            return;
        }
        frame = current;
        ModSettings settings = GamepadToolbeltMod.Settings;
        Now = Time.unscaledTime;
        DeltaTime = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.1f);
        Ui = PadLayer.Engine.Ui;
        SlotCount = PadLayer.LastSlotCount;
        TapMaxSeconds = settings.TapMaxSeconds;
        IdleOpacity = settings.HintIdleOpacity;
        Enabled = settings.Enabled && settings.HintMode != HintMode.Off && !PadLayer.DisabledByErrors;

        PlayerInputManager input = PlatformManager.NativePlatform?.Input;
        bool styleIsController = input != null && input.CurrentInputStyle != PlayerInputManager.InputStyle.Keyboard
                                 && input.CurrentInputStyle != PlayerInputManager.InputStyle.Undefined;
        bool recentPad = Ui.LastPadActivity > 0d && Now - Ui.LastPadActivity < PadActivityWindowSeconds;
        ControllerMode = styleIsController || recentPad || Ui.AnyBumperHeld;

        bool gateOpen = PadLayer.LastGate.Open;
        if (gateOpen)
        {
            gateClosedSince = -1d;
        }
        else if (gateClosedSince < 0d)
        {
            gateClosedSince = Now;
        }
        bool gateVisible = gateOpen || Now - gateClosedSince < GateCloseDelaySeconds;

        Visible = Enabled && ControllerMode && gateVisible && (settings.HintMode == HintMode.Always || Ui.AnyBumperHeld);
        GlobalAlpha = HintMotion.Approach(GlobalAlpha, Visible ? 1f : 0f, HintMotion.EaseSpeed * 0.6f, DeltaTime);
        XpOffset = HintMotion.Approach(XpOffset, Visible ? HintMotion.XpLift : 0f, HintMotion.XpEaseSpeed, DeltaTime);

        PlayerInputManager.InputStyle iconStyle = PlayerInputManager.InputStyleFromSelectedIconStyle();
        if (iconStyle != lastIconStyle)
        {
            lastIconStyle = iconStyle;
            StyleVersion++;
        }
    }

    /// <summary>Sprite name of a button in the current glyph family (Xbox or PlayStation, as the game shows them).</summary>
    public static string SpriteFor(PadButton _button)
    {
        switch (_button)
        {
            case PadButton.LeftBumper: return UIUtils.GetSpriteName(UIUtils.ButtonIcon.LeftBumper);
            case PadButton.RightBumper: return UIUtils.GetSpriteName(UIUtils.ButtonIcon.RightBumper);
            case PadButton.A: return UIUtils.GetSpriteName(UIUtils.ButtonIcon.FaceButtonSouth);
            case PadButton.B: return UIUtils.GetSpriteName(UIUtils.ButtonIcon.FaceButtonEast);
            case PadButton.X: return UIUtils.GetSpriteName(UIUtils.ButtonIcon.FaceButtonWest);
            default: return UIUtils.GetSpriteName(UIUtils.ButtonIcon.FaceButtonNorth);
        }
    }

    public static void Reset()
    {
        GlobalAlpha = 0f;
        XpOffset = 0f;
        gateClosedSince = -1d;
        frame = -1;
    }
}
