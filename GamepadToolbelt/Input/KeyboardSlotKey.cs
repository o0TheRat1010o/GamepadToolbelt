using System;
using GamepadToolbelt.Engine;
using GamepadToolbelt.Patches;
using InControl;

namespace GamepadToolbelt.Input;

/// <summary>
/// A keyboard key for slot 11 (vanilla only has Key1-Key0 for slots 1-10). It lives in an action set of its own, so it
/// is never saved into the game's control bindings and uninstalling the mod leaves nothing behind. It does not count
/// with Ctrl, Alt or Cmd. Shift is allowed - sprinting is Shift, and vanilla's number keys work while sprinting too -
/// except in god or fly mode, where vanilla uses Shift+Minus/Equals for the fly speed.
/// </summary>
internal static class KeyboardSlotKey
{
    private sealed class KeyActions : PlayerActionSet
    {
        public readonly PlayerAction Slot11;

        public KeyActions()
        {
            Slot11 = CreatePlayerAction("GamepadToolbeltSlot11");
        }
    }

    private static KeyActions actions;

    public static Key? BoundKey { get; private set; }

    /// <summary>Binds the configured key (or none). Returns an error text for an unknown key name.</summary>
    public static string Configure(string _keyName, bool _enabled)
    {
        if (actions == null)
        {
            actions = new KeyActions();
        }
        actions.Slot11.ClearBindings();
        BoundKey = null;
        if (!_enabled)
        {
            return null;
        }
        if (!Enum.TryParse(_keyName, true, out Key key) || key == Key.None || IsModifier(key))
        {
            return $"KeyboardSlot11Key '{_keyName}' is not a usable InControl key name; slot 11 has no key.";
        }
        actions.Slot11.AddBinding(new KeyBindingSource(key));
        BoundKey = key;
        return null;
    }

    /// <summary>True in the frame the key was pressed for <paramref name="_player"/>, without a blocking modifier key.</summary>
    public static bool WasPressed(EntityPlayerLocal _player)
    {
        if (actions == null || !BoundKey.HasValue || !actions.Slot11.WasPressed)
        {
            return false;
        }
        return !ModifierBlocks(_player);
    }

    /// <summary>
    /// Ctrl, Alt or Cmd held, or Shift while vanilla's fly speed keys are live (god or fly mode). Read like the key
    /// binding itself (InControl's keyboard provider) and like vanilla (Unity input).
    /// </summary>
    public static bool ModifierBlocks(EntityPlayerLocal _player)
    {
        IKeyboardProvider keyboard = InputManager.KeyboardProvider;
        bool Held(Key _left, Key _right) => keyboard != null && (keyboard.GetKeyIsPressed(_left) || keyboard.GetKeyIsPressed(_right));
        if (InputUtils.ControlKeyPressed || InputUtils.AltKeyPressed || Held(Key.LeftControl, Key.RightControl) || Held(Key.LeftAlt, Key.RightAlt)
            || Held(Key.LeftCommand, Key.RightCommand))
        {
            return true;
        }
        bool shift = InputUtils.ShiftKeyPressed || Held(Key.LeftShift, Key.RightShift);
        return shift && FlySpeedKeysLive(_player);
    }

    /// <summary>Vanilla changes the god/fly mode speed with Shift+Minus and Shift+Equals; it only matters in these modes.</summary>
    public static bool FlySpeedKeysLive(EntityPlayerLocal _player)
    {
        return _player == null || _player.IsGodMode.Value || _player.IsFlyMode.Value;
    }

    public static void Destroy()
    {
        try
        {
            actions?.Destroy();
        }
        catch (Exception e)
        {
            ModLog.Warning("Removing the slot 11 key failed: " + e.Message, "keyboard");
        }
        actions = null;
        BoundKey = null;
    }

    private static bool IsModifier(Key _key)
    {
        switch (_key)
        {
            case Key.Shift:
            case Key.LeftShift:
            case Key.RightShift:
            case Key.Control:
            case Key.LeftControl:
            case Key.RightControl:
            case Key.Alt:
            case Key.LeftAlt:
            case Key.RightAlt:
            case Key.Command:
            case Key.LeftCommand:
            case Key.RightCommand:
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// A short, light pulse on the controller that made the chord. The game's TriggerEffectManager rewrites the controller
/// vibration every frame (it stops it whenever no rumbling sound plays) and in keyboard mode nothing would ever stop
/// it, so the pulse is managed here: re-applied right after the game's update (postfix, group "Haptics") until it ends,
/// then stopped explicitly - in every input style. The input tick is a watchdog in case that update stops running.
/// It respects the game's vibration options and never interrupts a vibration the game itself plays (damage).
/// </summary>
internal static class Haptics
{
    /// <summary>Grace period after the planned end before the watchdog stops a pulse the game update did not end.</summary>
    public const float WatchdogSeconds = 0.25f;

    private static InputDevice device;
    private static float strength;
    private static float endTime;

    /// <summary>A pulse is running (for diagnostics and the harness).</summary>
    public static bool Active => device != null;

    public static bool Pulse(InputDevice _device, Core.ModSettings _settings)
    {
        if (!_settings.Haptics || _device == null || !PatchInstaller.IsInstalled(PatchInstaller.HapticsGroup))
        {
            return false;
        }
        try
        {
            if (GamePrefs.GetInt(EnumGamePrefs.OptionsControllerVibrationStrength) <= 0 || GameManager.Instance?.triggerEffectManager == null)
            {
                return false;
            }
            EntityPlayerLocal player = GameManager.Instance.World?.GetPrimaryPlayer();
            if (player == null || player.MoveController == null || !player.MoveController.GetControllerVibration())
            {
                return false;
            }
            if (player.vibrationTimeout != float.MaxValue && player.vibrationTimeout > UnityEngine.Time.time)
            {
                return false;
            }
            if (device != null && !ReferenceEquals(device, _device))
            {
                StopNow();
            }
            device = _device;
            strength = _settings.HapticStrength;
            endTime = UnityEngine.Time.unscaledTime + _settings.HapticMs / 1000f;
            Apply();
            RuntimeStats.HapticPulses++;
            return true;
        }
        catch (Exception e)
        {
            ModLog.Warning("Haptic pulse failed: " + e.Message, "haptics");
            StopNow();
            return false;
        }
    }

    /// <summary>Postfix of TriggerEffectManager.Update: every frame, after the game set (or stopped) the vibration.</summary>
    public static void AfterTriggerEffectsUpdate()
    {
        if (device == null)
        {
            return;
        }
        try
        {
            if (UnityEngine.Time.unscaledTime >= endTime || !device.IsAttached)
            {
                StopNow();
                return;
            }
            Apply();
        }
        catch (Exception e)
        {
            ModLog.Warning("Haptic pulse update failed: " + e.Message, "haptics");
            StopNow();
        }
    }

    /// <summary>Input tick: stops a pulse that outlived its end (the game update did not run).</summary>
    public static void Watchdog()
    {
        if (device != null && UnityEngine.Time.unscaledTime >= endTime + WatchdogSeconds)
        {
            StopNow();
        }
    }

    /// <summary>Session end, layer reset: no pulse may keep running.</summary>
    public static void Stop()
    {
        if (device != null)
        {
            StopNow();
        }
    }

    private static void Apply()
    {
        device.Vibrate(strength);
        GameManager.Instance?.triggerEffectManager?.SetGamepadVibration(strength);
    }

    private static void StopNow()
    {
        InputDevice stopped = device;
        device = null;
        try
        {
            stopped?.StopVibration();
            GameManager.Instance?.triggerEffectManager?.StopGamepadVibration();
        }
        catch (Exception e)
        {
            ModLog.Warning("Stopping the haptic pulse failed: " + e.Message, "haptics");
        }
    }
}
