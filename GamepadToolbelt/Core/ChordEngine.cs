using System;
using System.Collections.Generic;

namespace GamepadToolbelt.Core;

public enum ChordEventKind
{
    /// <summary>A chord selected a slot (the newest request wins).</summary>
    Select,

    /// <summary>A bumper was released without a chord, but too late or after the toolbelt became inactive.</summary>
    TapCancel,

    /// <summary>A chord points at a slot the toolbelt does not have (e.g. RB tap with 10 slots).</summary>
    Unavailable,

    /// <summary>The layer took a button away from the game (it stays owned until it is physically released).</summary>
    Claim,

    /// <summary>An owned button was released and belongs to the game again.</summary>
    Release,

    /// <summary>The controller changed; every owned button was given back.</summary>
    DeviceReset
}

/// <summary>One thing that happened in a tick, for the selection, the haptics and the audit log.</summary>
public readonly struct ChordEvent
{
    public readonly ChordEventKind Kind;
    public readonly int Slot;
    public readonly ChordGesture Gesture;
    public readonly PadSide Side;
    public readonly PadButton Button;
    public readonly double HeldSeconds;
    public readonly double Time;
    public readonly string Reason;

    public ChordEvent(ChordEventKind _kind, int _slot, ChordGesture _gesture, PadSide _side, PadButton _button, double _heldSeconds, double _time, string _reason)
    {
        Kind = _kind;
        Slot = _slot;
        Gesture = _gesture;
        Side = _side;
        Button = _button;
        HeldSeconds = _heldSeconds;
        Time = _time;
        Reason = _reason;
    }

    public override string ToString()
    {
        switch (Kind)
        {
            case ChordEventKind.Select: return $"Select {Slot} ({Gesture} {SlotMap.SideName(Side)}{(Gesture == ChordGesture.Face ? "+" + SlotMap.ShortName(Button) : string.Empty)})";
            case ChordEventKind.TapCancel: return $"TapCancel {SlotMap.SideName(Side)} ({Reason}, {HeldSeconds * 1000:0} ms)";
            case ChordEventKind.Unavailable: return $"Unavailable {Slot}";
            case ChordEventKind.Claim: return $"Claim {SlotMap.ShortName(Button)}";
            case ChordEventKind.Release: return $"Release {SlotMap.ShortName(Button)}";
            default: return $"DeviceReset ({Reason})";
        }
    }
}

/// <summary>What the toolbelt hints need to know about the current hold.</summary>
public struct ChordUiState
{
    /// <summary>LB is owned by the layer and physically held.</summary>
    public bool LeftHeld;

    /// <summary>RB is owned by the layer and physically held.</summary>
    public bool RightHeld;

    /// <summary>The bumper face chords refer to: the first pressed of the held bumpers.</summary>
    public PadSide BaseSide;

    /// <summary>Releasing LB now would still select slot 1 (no chord yet, toolbelt stayed active).</summary>
    public bool LeftTapPending;

    /// <summary>Releasing RB now would still select slot 11.</summary>
    public bool RightTapPending;

    public double LeftPressTime;
    public double RightPressTime;

    /// <summary>Incremented on every selection (drives the pulse animation).</summary>
    public int SelectSerial;

    public int LastSelectedSlot;
    public double LastSelectTime;

    /// <summary>Last time a chord button changed state on the controller (controller-mode detection).</summary>
    public double LastPadActivity;

    public bool AnyBumperHeld => LeftHeld || RightHeld;

    /// <summary>0..1 of the tap window that has passed for the given bumper; 1 = a release no longer selects.</summary>
    public double TapProgress(PadSide _side, double _now, double _tapMaxSeconds)
    {
        bool pending = _side == PadSide.Left ? LeftTapPending : _side == PadSide.Right && RightTapPending;
        if (!pending || _tapMaxSeconds <= 0)
        {
            return 1d;
        }
        double start = _side == PadSide.Left ? LeftPressTime : RightPressTime;
        return Math.Max(0d, Math.Min(1d, (_now - start) / _tapMaxSeconds));
    }
}

/// <summary>
/// Deterministic chord state machine of the controller layer, ticked once per input update with the physical button
/// states. It decides which buttons are taken away from the game ("owned") and which slot a chord selects:
/// <list type="bullet">
/// <item>A bumper pressed while the toolbelt is active is owned until it is physically released, so the game never sees
/// it (no vanilla toolbelt step or radial). Pressing the other bumper while the first is held selects slot 6.</item>
/// <item>A face button pressed while an owned bumper is held is owned as well and selects a slot of the base side, the
/// first pressed of the held bumpers (RB held, LB added = 6, then X = 8).</item>
/// <item>Releasing a bumper that formed no chord within <see cref="PadInput.TapMaxSeconds"/> selects slot 1 or 11;
/// releasing it later, or after the toolbelt became inactive during the hold, cancels.</item>
/// <item>Order within one tick: bumper presses, face presses, releases. A button released in the same tick still counts
/// as held for the chords of that tick, so pressing LB and A together, or A while letting go of LB, is a chord.</item>
/// <item>Buttons already held when the hold starts, and anything pressed while the toolbelt is inactive, stay vanilla.
/// A controller change gives every owned button back.</item>
/// </list>
/// </summary>
public sealed class ChordEngine
{
    private struct ButtonState
    {
        public bool Owned;
        public double PressTime;
        public long PressSequence;
    }

    /// <summary>Tolerance of the tap window comparison (frame times come from float clocks).</summary>
    public const double TimeEpsilon = 1e-6;

    private static readonly PadSide[] Sides = { PadSide.Left, PadSide.Right };

    private readonly ButtonState[] buttons = new ButtonState[PadSnapshot.ButtonCount];
    private readonly bool[] comboUsed = new bool[3];      // indexed by PadSide
    private readonly bool[] tapValid = new bool[3];       // toolbelt stayed active since the bumper was pressed
    private readonly List<ChordEvent> events = new List<ChordEvent>();
    private PadSnapshot previous;
    private long deviceKey;
    private long sequence;
    private ChordUiState ui;

    public IReadOnlyList<ChordEvent> Events => events;

    public ChordUiState Ui => ui;

    /// <summary>The device whose buttons are owned (0 = none).</summary>
    public long OwnedDeviceKey => AnyOwned ? deviceKey : 0;

    public PadSnapshot Previous => previous;

    public bool AnyOwned
    {
        get
        {
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i].Owned)
                {
                    return true;
                }
            }
            return false;
        }
    }

    public bool IsOwned(PadButton _button) => buttons[(int)_button].Owned;

    /// <summary>Owned buttons as a bit mask (bit = <see cref="PadButton"/> value).</summary>
    public byte OwnedBits
    {
        get
        {
            int bits = 0;
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i].Owned)
                {
                    bits |= 1 << i;
                }
            }
            return (byte)bits;
        }
    }

    public void Tick(in PadInput _input)
    {
        events.Clear();
        PadSnapshot current = _input.DeviceKey == 0 ? default : _input.Pad;
        double now = _input.Time;

        if (_input.DeviceKey != deviceKey)
        {
            // New (or no) controller: give everything back and take the held buttons as the new baseline, so a button
            // that is held while switching devices produces no press edge.
            ReleaseAll(now, _input.DeviceKey == 0 ? "no_device" : deviceKey == 0 ? "device_connected" : "device_changed", _emitReset: deviceKey != 0 || AnyOwned);
            deviceKey = _input.DeviceKey;
            previous = current;
            if (current.Any)
            {
                ui.LastPadActivity = now;
            }
            RefreshUi(now, _input.TapMaxSeconds);
            return;
        }

        if (!current.Equals(previous))
        {
            ui.LastPadActivity = now;
        }

        if (!_input.GateOpen)
        {
            // Once the toolbelt becomes inactive during a hold, releasing the bumper never taps.
            tapValid[(int)PadSide.Left] = false;
            tapValid[(int)PadSide.Right] = false;
        }

        // 1. Bumper presses.
        foreach (PadSide side in Sides)
        {
            PadButton bumper = SlotMap.Bumper(side);
            if (!Pressed(current, bumper) || !_input.GateOpen)
            {
                continue;
            }
            Claim(bumper, now);
            comboUsed[(int)side] = false;
            tapValid[(int)side] = true;
            PadButton other = SlotMap.Bumper(SlotMap.Opposite(side));
            if (buttons[(int)other].Owned)
            {
                comboUsed[(int)PadSide.Left] = true;
                comboUsed[(int)PadSide.Right] = true;
                Select(SlotMap.BothBumpersSlot, ChordGesture.BothBumpers, BaseSide(), bumper, now, _input.SlotCount);
            }
        }

        // 2. Face presses on an owned bumper.
        foreach (PadButton face in SlotMap.FaceOrder)
        {
            if (!Pressed(current, face) || !_input.GateOpen)
            {
                continue;
            }
            PadSide baseSide = BaseSide();
            if (baseSide == PadSide.None)
            {
                continue;
            }
            Claim(face, now);
            comboUsed[(int)PadSide.Left] |= buttons[(int)PadButton.LeftBumper].Owned;
            comboUsed[(int)PadSide.Right] |= buttons[(int)PadButton.RightBumper].Owned;
            Select(SlotMap.FaceSlot(baseSide, face), ChordGesture.Face, baseSide, face, now, _input.SlotCount);
        }

        // 3. Releases: face buttons first, then the bumpers (a bumper tap is decided on its release).
        foreach (PadButton face in SlotMap.FaceOrder)
        {
            if (!current[face] && buttons[(int)face].Owned)
            {
                Release(face, now);
            }
        }
        foreach (PadSide side in Sides)
        {
            PadButton bumper = SlotMap.Bumper(side);
            if (current[bumper] || !buttons[(int)bumper].Owned)
            {
                continue;
            }
            double held = now - buttons[(int)bumper].PressTime;
            if (!comboUsed[(int)side])
            {
                if (tapValid[(int)side] && _input.GateOpen && held <= _input.TapMaxSeconds + TimeEpsilon)
                {
                    Select(SlotMap.TapSlot(side), ChordGesture.Tap, side, bumper, now, _input.SlotCount, held);
                }
                else
                {
                    events.Add(new ChordEvent(ChordEventKind.TapCancel, SlotMap.TapSlot(side), ChordGesture.Tap, side, bumper, held, now,
                        tapValid[(int)side] && _input.GateOpen ? "too_long" : "gate_closed"));
                }
            }
            comboUsed[(int)side] = false;
            tapValid[(int)side] = false;
            Release(bumper, now);
        }

        previous = current;
        RefreshUi(now, _input.TapMaxSeconds);
    }

    /// <summary>Gives every owned button back (world unload, layer disabled).</summary>
    public void Reset(double _now, string _reason)
    {
        events.Clear();
        ReleaseAll(_now, _reason, _emitReset: AnyOwned);
        RefreshUi(_now, 0d);
    }

    // ----- internals --------------------------------------------------------------------------

    private bool Pressed(PadSnapshot _current, PadButton _button) => _current[_button] && !previous[_button];

    /// <summary>
    /// First pressed of the owned bumpers. Owned means held since the press; a bumper released in this tick is given back
    /// only in the release phase, so it still counts here.
    /// </summary>
    private PadSide BaseSide()
    {
        ButtonState left = buttons[(int)PadButton.LeftBumper];
        ButtonState right = buttons[(int)PadButton.RightBumper];
        if (left.Owned && right.Owned)
        {
            return left.PressSequence <= right.PressSequence ? PadSide.Left : PadSide.Right;
        }
        return left.Owned ? PadSide.Left : right.Owned ? PadSide.Right : PadSide.None;
    }

    private void Claim(PadButton _button, double _now)
    {
        buttons[(int)_button] = new ButtonState { Owned = true, PressTime = _now, PressSequence = ++sequence };
        events.Add(new ChordEvent(ChordEventKind.Claim, 0, ChordGesture.Tap, SlotMap.SideOf(_button), _button, 0d, _now, null));
    }

    private void Release(PadButton _button, double _now)
    {
        double held = _now - buttons[(int)_button].PressTime;
        buttons[(int)_button].Owned = false;
        events.Add(new ChordEvent(ChordEventKind.Release, 0, ChordGesture.Tap, SlotMap.SideOf(_button), _button, held, _now, null));
    }

    private void ReleaseAll(double _now, string _reason, bool _emitReset)
    {
        for (int i = 0; i < buttons.Length; i++)
        {
            if (buttons[i].Owned)
            {
                Release((PadButton)i, _now);
            }
        }
        Array.Clear(comboUsed, 0, comboUsed.Length);
        Array.Clear(tapValid, 0, tapValid.Length);
        if (_emitReset)
        {
            events.Add(new ChordEvent(ChordEventKind.DeviceReset, 0, ChordGesture.Tap, PadSide.None, PadButton.LeftBumper, 0d, _now, _reason));
        }
    }

    private void Select(int _slot, ChordGesture _gesture, PadSide _side, PadButton _button, double _now, int _slotCount, double _held = 0d)
    {
        if (_slot < SlotMap.FirstSlot || _slot > _slotCount)
        {
            events.Add(new ChordEvent(ChordEventKind.Unavailable, _slot, _gesture, _side, _button, _held, _now, "slot_count_" + _slotCount));
            return;
        }
        events.Add(new ChordEvent(ChordEventKind.Select, _slot, _gesture, _side, _button, _held, _now, null));
        ui.SelectSerial++;
        ui.LastSelectedSlot = _slot;
        ui.LastSelectTime = _now;
    }

    private void RefreshUi(double _now, double _tapMaxSeconds)
    {
        ButtonState left = buttons[(int)PadButton.LeftBumper];
        ButtonState right = buttons[(int)PadButton.RightBumper];
        ui.LeftHeld = left.Owned;
        ui.RightHeld = right.Owned;
        ui.BaseSide = BaseSide();
        ui.LeftPressTime = left.PressTime;
        ui.RightPressTime = right.PressTime;
        ui.LeftTapPending = left.Owned && !comboUsed[(int)PadSide.Left] && tapValid[(int)PadSide.Left] && (_now - left.PressTime) <= _tapMaxSeconds + TimeEpsilon;
        ui.RightTapPending = right.Owned && !comboUsed[(int)PadSide.Right] && tapValid[(int)PadSide.Right] && (_now - right.PressTime) <= _tapMaxSeconds + TimeEpsilon;
    }
}
