using System;
using GamepadToolbelt.Core;
using GamepadToolbelt.Engine;
using InControl;
using Platform;
using UnityEngine;

namespace GamepadToolbelt.Input;

/// <summary>
/// The controller layer. Once per InControl tick (devices committed, action sets not yet updated) it reads the six
/// physical buttons of the player's controller, evaluates the toolbelt gate, ticks the <see cref="ChordEngine"/> and
/// publishes which controls are owned. The binding postfixes then report owned controls as released to every
/// PlayerAction, so the game never sees them - in any action set, with any user binding - while keyboard bindings of
/// the same actions keep working. Selections are queued for <see cref="SelectionApplier"/>.
/// <para>A reset (world end, settings, error) gives ownership back at once, but a button that is still held stays
/// masked until it is physically released ("residual" mask): unmasking it while held would hand the game a fresh press
/// (vanilla would step the toolbelt, open the radial or jump).</para>
/// </summary>
internal static class PadLayer
{
    public const int MaxErrors = 20;

    public static readonly ChordEngine Engine = new ChordEngine();

    /// <summary>Device whose owned controls are masked (null = nothing masked).</summary>
    public static InputDevice MaskDevice;

    private static bool maskLeftBumper;
    private static bool maskRightBumper;
    private static bool maskA;
    private static bool maskB;
    private static bool maskX;
    private static bool maskY;

    /// <summary>Buttons given back by a reset while held: masked on <see cref="residualDevice"/> until released.</summary>
    private static byte residualBits;
    private static InputDevice residualDevice;

    private static ulong lastTick = ulong.MaxValue;
    private static InputDevice maskSource;
    private static InputDevice keyedDevice;
    private static long deviceKey;
    private static long deviceCounter;
    private static bool lastGateOpen;
    private static string lastGateReason = "none";
    private static int errors;

    public static bool DisabledByErrors { get; private set; }

    /// <summary>InControl ticks seen since the game started (never reset, unlike the session statistics).</summary>
    public static long TotalTicks { get; private set; }
    public static InputDevice CurrentDevice { get; private set; }
    public static GateResult LastGate { get; private set; }
    public static double LastTickTime { get; private set; }
    public static int LastSlotCount { get; private set; } = ToolbeltConfigXml.VanillaSlots;

    /// <summary>Called by the prefix of InControl's InputManager.UpdatePlayerActionSets.</summary>
    public static void OnInputTick()
    {
        ulong tick = InputManager.CurrentTick;
        if (tick == lastTick)
        {
            RuntimeStats.DuplicateTicks++;
            return;
        }
        lastTick = tick;
        TotalTicks++;
        RuntimeStats.InputTicks++;
        Haptics.Watchdog();
        if (DisabledByErrors)
        {
            // The layer is off, but buttons it gave back while held stay masked until they are released.
            if (residualBits != 0)
            {
                UpdateResidual(ResolveDevice());
                ApplyMask(maskSource);
            }
            return;
        }
        try
        {
            TickCore();
        }
        catch (Exception e)
        {
            OnError(e);
        }
    }

    /// <summary>Gives every owned button back (world unload, settings changed); held ones stay masked until released.</summary>
    public static void Reset(string _reason)
    {
        KeepHeldMasked();
        Engine.Reset(Time.unscaledTime, _reason);
        ApplyMask(maskSource);
        DispatchReset();
        SelectionApplier.Reset(_reason);
        Haptics.Stop();
    }

    /// <summary>Re-enables the layer after it was switched off by repeated errors ("gtb reload").</summary>
    public static void ResetErrors()
    {
        errors = 0;
        DisabledByErrors = false;
    }

    public static bool IsMasked(InputControlType _control)
    {
        switch (_control)
        {
            case InputControlType.LeftBumper: return maskLeftBumper;
            case InputControlType.RightBumper: return maskRightBumper;
            case InputControlType.Action1: return maskA;
            case InputControlType.Action2: return maskB;
            case InputControlType.Action3: return maskX;
            case InputControlType.Action4: return maskY;
            default: return false;
        }
    }

    public static InputControlType ControlOf(PadButton _button)
    {
        switch (_button)
        {
            case PadButton.LeftBumper: return InputControlType.LeftBumper;
            case PadButton.RightBumper: return InputControlType.RightBumper;
            case PadButton.A: return InputControlType.Action1;
            case PadButton.B: return InputControlType.Action2;
            case PadButton.X: return InputControlType.Action3;
            default: return InputControlType.Action4;
        }
    }

    /// <summary>The controller the player's actions read from, or null when there is no usable controller.</summary>
    public static InputDevice ResolveDevice()
    {
        PlayerActionsLocal actions = PlatformManager.NativePlatform?.Input?.PrimaryPlayer;
        InputDevice device = actions?.Device ?? InputManager.ActiveDevice;
        if (device == null || device == InputDevice.Null || !device.IsKnown)
        {
            return null;
        }
        return device;
    }

    public static PadSnapshot Read(InputDevice _device)
    {
        var pad = new PadSnapshot();
        pad[PadButton.LeftBumper] = _device.LeftBumper.IsPressed;
        pad[PadButton.RightBumper] = _device.RightBumper.IsPressed;
        pad[PadButton.A] = _device.Action1.IsPressed;
        pad[PadButton.B] = _device.Action2.IsPressed;
        pad[PadButton.X] = _device.Action3.IsPressed;
        pad[PadButton.Y] = _device.Action4.IsPressed;
        return pad;
    }

    /// <summary>Buttons that are masked although the engine does not own them (released after a reset).</summary>
    public static byte ResidualBits => residualBits;

    /// <summary>
    /// True when one of the action's controller bindings on a control the layer does not mask is active: the action
    /// may then be pressed legitimately while a bumper is owned (a user binding on the D-pad, say). The invariant checks
    /// use it; all bindings of an action read the same device as the mask.
    /// </summary>
    public static bool ActiveThroughUnmaskedControl(PlayerAction _action)
    {
        InputDevice device = MaskDevice;
        if (_action == null || device == null)
        {
            return false;
        }
        foreach (BindingSource binding in _action.UnfilteredBindings)
        {
            if (binding is DeviceBindingSource deviceBinding && !IsMasked(deviceBinding.Control) && deviceBinding.GetState(device))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Selftest hook: masks LB of a probe device; <see cref="RestoreMask"/> puts the live mask back.</summary>
    internal static void SetMaskForProbe(InputDevice _device, bool _leftBumper)
    {
        MaskDevice = _device;
        maskLeftBumper = _leftBumper;
        maskRightBumper = maskA = maskB = maskX = maskY = false;
    }

    internal static void RestoreMask() => ApplyMask(maskSource);

    // ----- internals --------------------------------------------------------------------------

    private static void TickCore()
    {
        ModSettings settings = GamepadToolbeltMod.Settings;
        double now = Time.unscaledTime;
        InputDevice device = ResolveDevice();
        long key = KeyOf(device);
        PadSnapshot pad = device != null ? Read(device) : default;
        GateResult gate = ToolbeltGate.Evaluate(settings);
        int slotCount = gate.Player?.inventory?.Length ?? ToolbeltConfig.GameConfig.TotalSlots;
        LastSlotCount = slotCount;

        Engine.Tick(new PadInput
        {
            DeviceKey = key,
            Pad = pad,
            GateOpen = gate.Open,
            Time = now,
            SlotCount = slotCount,
            TapMaxSeconds = settings.TapMaxSeconds
        });
        UpdateResidual(device);
        ApplyMask(device);
        LastGate = gate;
        LastTickTime = now;
        TrackGate(gate);
        if (Engine.Events.Count > 0)
        {
            Dispatch(device, pad, slotCount, settings);
        }
    }

    private static long KeyOf(InputDevice _device)
    {
        if (_device == null)
        {
            if (CurrentDevice != null)
            {
                CurrentDevice = null;
                keyedDevice = null;
                WriteDevice(null, "lost");
            }
            return 0;
        }
        if (!ReferenceEquals(_device, keyedDevice))
        {
            keyedDevice = _device;
            deviceKey = ++deviceCounter;
            CurrentDevice = _device;
            WriteDevice(_device, "active");
        }
        return deviceKey;
    }

    /// <summary>Owned buttons plus the residual ones of the same device become the mask.</summary>
    private static void ApplyMask(InputDevice _device)
    {
        maskSource = _device;
        int bits = Engine.OwnedBits;
        if (residualBits != 0 && _device != null && ReferenceEquals(_device, residualDevice))
        {
            bits |= residualBits;
        }
        if (bits == 0 || _device == null)
        {
            ClearMask();
            return;
        }
        maskLeftBumper = (bits & (1 << (int)PadButton.LeftBumper)) != 0;
        maskRightBumper = (bits & (1 << (int)PadButton.RightBumper)) != 0;
        maskA = (bits & (1 << (int)PadButton.A)) != 0;
        maskB = (bits & (1 << (int)PadButton.B)) != 0;
        maskX = (bits & (1 << (int)PadButton.X)) != 0;
        maskY = (bits & (1 << (int)PadButton.Y)) != 0;
        MaskDevice = _device;
    }

    private static void ClearMask()
    {
        MaskDevice = null;
        maskLeftBumper = maskRightBumper = maskA = maskB = maskX = maskY = false;
    }

    /// <summary>Before ownership is given back: the masked buttons that are still held stay masked until released.</summary>
    private static void KeepHeldMasked()
    {
        InputDevice device = MaskDevice;
        if (device == null)
        {
            return;
        }
        byte masked = 0;
        for (int i = 0; i < PadSnapshot.ButtonCount; i++)
        {
            if (IsMasked(ControlOf((PadButton)i)))
            {
                masked |= (byte)(1 << i);
            }
        }
        if (!ReferenceEquals(device, residualDevice))
        {
            residualBits = 0;
        }
        residualDevice = device;
        residualBits |= (byte)(masked & Read(device).Bits);
    }

    /// <summary>Drops the residual buttons that were released (or all of them when the controller changed).</summary>
    private static void UpdateResidual(InputDevice _device)
    {
        if (residualBits == 0)
        {
            return;
        }
        if (_device == null || !ReferenceEquals(_device, residualDevice) || !_device.IsAttached)
        {
            residualBits = 0;
            residualDevice = null;
            return;
        }
        residualBits &= Read(_device).Bits;
        if (residualBits == 0)
        {
            residualDevice = null;
        }
    }

    private static void TrackGate(GateResult _gate)
    {
        if (_gate.Open == lastGateOpen && (_gate.Open || _gate.Reason == lastGateReason))
        {
            return;
        }
        bool opened = _gate.Open && !lastGateOpen;
        if (!_gate.Open && lastGateOpen)
        {
            RuntimeStats.CountGateClosed(_gate.Reason);
        }
        lastGateOpen = _gate.Open;
        lastGateReason = _gate.Reason;
        if (AuditLog.Detailed)
        {
            JsonLineWriter record = AuditLog.Begin(AuditRecords.Gate);
            record.Prop("open", _gate.Open).Prop("reason", _gate.Reason).Prop("opened", opened).Prop("owned", SlotOwnedText());
            AuditLog.Commit(record);
        }
    }

    private static void Dispatch(InputDevice _device, PadSnapshot _pad, int _slotCount, ModSettings _settings)
    {
        foreach (ChordEvent e in Engine.Events)
        {
            switch (e.Kind)
            {
                case ChordEventKind.Select:
                {
                    RuntimeStats.CountChord(e.Slot, false);
                    long id = SelectionApplier.Request(e.Slot, e.Gesture, e.Side, e.Button, e.Time);
                    bool pulsed = Haptics.Pulse(_device, _settings);
                    WriteChord(e, id, _pad, _slotCount, _settings, pulsed);
                    break;
                }
                case ChordEventKind.TapCancel:
                    RuntimeStats.TapCancels++;
                    WriteSimple(AuditRecords.TapCancel, e, _settings);
                    break;
                case ChordEventKind.Unavailable:
                    RuntimeStats.Unavailable++;
                    WriteSimple(AuditRecords.Unavailable, e, _settings);
                    break;
                case ChordEventKind.Claim:
                case ChordEventKind.Release:
                    if (AuditLog.Detailed)
                    {
                        JsonLineWriter record = AuditLog.Begin(AuditRecords.Mask);
                        record.Prop("action", e.Kind == ChordEventKind.Claim ? "claim" : "release")
                            .Prop("button", SlotMap.ShortName(e.Button))
                            .Prop("held_ms", e.Kind == ChordEventKind.Release ? AuditRecords.Ms(e.HeldSeconds) : 0d)
                            .Prop("owned", SlotOwnedText());
                        AuditLog.Commit(record);
                    }
                    break;
                case ChordEventKind.DeviceReset:
                    RuntimeStats.DeviceResets++;
                    if (AuditLog.Summary)
                    {
                        JsonLineWriter record = AuditLog.Begin(AuditRecords.Device);
                        record.Prop("event", "reset").Prop("reason", e.Reason);
                        AuditLog.Commit(record);
                    }
                    break;
            }
        }
    }

    private static void WriteChord(ChordEvent _event, long _requestId, PadSnapshot _pad, int _slotCount, ModSettings _settings, bool _pulsed)
    {
        if (!AuditLog.Summary)
        {
            return;
        }
        JsonLineWriter record = AuditLog.Begin(AuditRecords.Chord);
        record.Prop("id", _requestId)
            .Prop("slot", _event.Slot)
            .Prop("gesture", AuditRecords.GestureCode(_event.Gesture))
            .Prop("side", SlotMap.SideName(_event.Side))
            .Prop("button", SlotMap.ShortName(_event.Button))
            .Prop("chord", SlotMap.Describe(_event.Slot))
            .Prop("held_ms", AuditRecords.Ms(_event.HeldSeconds))
            .Prop("tap_max_ms", AuditRecords.Ms(_settings.TapMaxSeconds))
            .Prop("pad", _pad.ToString())
            .Prop("owned", SlotOwnedText())
            .Prop("slots", _slotCount)
            .Prop("gate", LastGate.Open)
            .Prop("haptic", _pulsed);
        AuditLog.Commit(record);
    }

    private static void WriteSimple(string _type, ChordEvent _event, ModSettings _settings)
    {
        if (!AuditLog.Summary)
        {
            return;
        }
        JsonLineWriter record = AuditLog.Begin(_type);
        record.Prop("slot", _event.Slot)
            .Prop("side", SlotMap.SideName(_event.Side))
            .Prop("button", SlotMap.ShortName(_event.Button))
            .Prop("held_ms", AuditRecords.Ms(_event.HeldSeconds))
            .Prop("tap_max_ms", AuditRecords.Ms(_settings.TapMaxSeconds))
            .Prop("reason", _event.Reason)
            .Prop("gate", LastGate.Open)
            .Prop("gate_reason", LastGate.Reason);
        AuditLog.Commit(record);
    }

    private static void WriteDevice(InputDevice _device, string _event)
    {
        if (!AuditLog.Summary)
        {
            return;
        }
        JsonLineWriter record = AuditLog.Begin(AuditRecords.Device);
        record.Prop("event", _event);
        if (_device != null)
        {
            record.Prop("name", _device.Name).Prop("class", _device.DeviceClass.ToString()).Prop("style", _device.DeviceStyle.ToString());
        }
        AuditLog.Commit(record);
    }

    private static string SlotOwnedText() => PadSnapshot.FromBits(Engine.OwnedBits).ToString();

    /// <summary>The releases and the reset of an engine reset go to the audit log like those of a tick (claims stay balanced).</summary>
    private static void DispatchReset()
    {
        try
        {
            if (Engine.Events.Count > 0)
            {
                Dispatch(maskSource, default, LastSlotCount, GamepadToolbeltMod.Settings);
            }
        }
        catch (Exception e)
        {
            ModLog.Warning("Logging the controller layer reset failed: " + e.Message, "input_reset");
        }
    }

    private static void OnError(Exception _exception)
    {
        RuntimeStats.LayerErrors++;
        errors++;
        try
        {
            KeepHeldMasked();
            Engine.Reset(Time.unscaledTime, "error");
            ApplyMask(maskSource);
        }
        catch
        {
            // Reading the device failed as well: give everything back rather than keep a mask we cannot release.
            residualBits = 0;
            residualDevice = null;
            ClearMask();
        }
        DispatchReset();
        Haptics.Stop();
        if (errors <= 3)
        {
            ModLog.Error("Controller layer tick failed; owned buttons were given back.", _exception, "input_tick");
        }
        if (errors >= MaxErrors && !DisabledByErrors)
        {
            DisabledByErrors = true;
            ModLog.Error($"Controller layer failed {errors} times and is switched off for this session (vanilla toolbelt). Use 'gtb reload' to retry.", null, "input_disabled");
        }
    }
}
