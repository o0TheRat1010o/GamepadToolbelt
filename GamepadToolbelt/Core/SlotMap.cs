using System;
using System.Collections.Generic;

namespace GamepadToolbelt.Core;

/// <summary>How a slot's hint looks in the current chord state.</summary>
public enum SlotHintState
{
    /// <summary>No bumper held: every hint at idle opacity.</summary>
    Idle,

    /// <summary>Reachable with the next button press (face button or the second bumper).</summary>
    Reachable,

    /// <summary>Selected by releasing the held bumper in time (slot 1 or 11); shows the tap timer.</summary>
    TapPending,

    /// <summary>Not reachable from the current hold.</summary>
    Dimmed
}

/// <summary>The one or two buttons of a slot's chord, in display order (bumper first).</summary>
public readonly struct SlotChord
{
    public readonly PadButton First;
    public readonly PadButton? Second;

    public SlotChord(PadButton _first, PadButton? _second)
    {
        First = _first;
        Second = _second;
    }

    public int GlyphCount => Second.HasValue ? 2 : 1;

    public override string ToString() => Second.HasValue ? SlotMap.ShortName(First) + "+" + SlotMap.ShortName(Second.Value) : SlotMap.ShortName(First);
}

/// <summary>
/// The fixed controller layout (slots are 1-based as printed on the hints):
/// <code>
/// LB tap = 1 · LB + A/X/Y/B = 2/3/4/5 · LB + RB = 6 · RB + A/X/Y/B = 7/8/9/10 · RB tap = 11
/// </code>
/// </summary>
public static class SlotMap
{
    public const int FirstSlot = 1;
    public const int BothBumpersSlot = 6;
    public const int ExtraSlot = 11;
    public const int MaxSlots = 11;

    /// <summary>Face buttons in layout order (A, X, Y, B = offsets 0..3).</summary>
    public static readonly PadButton[] FaceOrder = { PadButton.A, PadButton.X, PadButton.Y, PadButton.B };

    public static bool IsBumper(PadButton _button) => _button == PadButton.LeftBumper || _button == PadButton.RightBumper;

    public static bool IsFace(PadButton _button) => !IsBumper(_button);

    public static PadButton Bumper(PadSide _side) => _side == PadSide.Right ? PadButton.RightBumper : PadButton.LeftBumper;

    public static PadSide SideOf(PadButton _bumper)
    {
        switch (_bumper)
        {
            case PadButton.LeftBumper: return PadSide.Left;
            case PadButton.RightBumper: return PadSide.Right;
            default: return PadSide.None;
        }
    }

    public static PadSide Opposite(PadSide _side) => _side == PadSide.Left ? PadSide.Right : _side == PadSide.Right ? PadSide.Left : PadSide.None;

    /// <summary>A=0, X=1, Y=2, B=3; -1 for bumpers.</summary>
    public static int FaceOffset(PadButton _face)
    {
        switch (_face)
        {
            case PadButton.A: return 0;
            case PadButton.X: return 1;
            case PadButton.Y: return 2;
            case PadButton.B: return 3;
            default: return -1;
        }
    }

    /// <summary>Slot of a bumper tap (1 or 11), 0 for no side.</summary>
    public static int TapSlot(PadSide _side) => _side == PadSide.Left ? FirstSlot : _side == PadSide.Right ? ExtraSlot : 0;

    /// <summary>Slot of a face chord on a held bumper (2-5 on LB, 7-10 on RB), 0 when invalid.</summary>
    public static int FaceSlot(PadSide _side, PadButton _face)
    {
        int offset = FaceOffset(_face);
        if (offset < 0)
        {
            return 0;
        }
        return _side == PadSide.Left ? 2 + offset : _side == PadSide.Right ? 7 + offset : 0;
    }

    /// <summary>The chord that selects a slot; null outside 1..11.</summary>
    public static SlotChord? ChordFor(int _slot)
    {
        if (_slot == FirstSlot)
        {
            return new SlotChord(PadButton.LeftBumper, null);
        }
        if (_slot >= 2 && _slot <= 5)
        {
            return new SlotChord(PadButton.LeftBumper, FaceOrder[_slot - 2]);
        }
        if (_slot == BothBumpersSlot)
        {
            return new SlotChord(PadButton.LeftBumper, PadButton.RightBumper);
        }
        if (_slot >= 7 && _slot <= 10)
        {
            return new SlotChord(PadButton.RightBumper, FaceOrder[_slot - 7]);
        }
        if (_slot == ExtraSlot)
        {
            return new SlotChord(PadButton.RightBumper, null);
        }
        return null;
    }

    /// <summary>"LB+A" style text of a slot's chord ("-" when the slot has none).</summary>
    public static string Describe(int _slot) => ChordFor(_slot)?.ToString() ?? "-";

    public static string ShortName(PadButton _button)
    {
        switch (_button)
        {
            case PadButton.LeftBumper: return "LB";
            case PadButton.RightBumper: return "RB";
            case PadButton.A: return "A";
            case PadButton.B: return "B";
            case PadButton.X: return "X";
            case PadButton.Y: return "Y";
            default: return "?";
        }
    }

    public static string SideName(PadSide _side) => _side == PadSide.Left ? "LB" : _side == PadSide.Right ? "RB" : "-";

    /// <summary>Hint state of a slot for the given chord state (slots outside the toolbelt are dimmed).</summary>
    public static SlotHintState HintState(int _slot, in ChordUiState _ui, int _slotCount)
    {
        if (!_ui.AnyBumperHeld)
        {
            return SlotHintState.Idle;
        }
        if (_slot < FirstSlot || _slot > _slotCount)
        {
            return SlotHintState.Dimmed;
        }
        PadSide baseSide = _ui.BaseSide;
        if (_slot == BothBumpersSlot)
        {
            // Reachable by adding the other bumper; while both are held it stays highlighted as the active chord.
            return SlotHintState.Reachable;
        }
        if (_slot == FirstSlot)
        {
            return _ui.LeftTapPending ? SlotHintState.TapPending : SlotHintState.Dimmed;
        }
        if (_slot == ExtraSlot)
        {
            return _ui.RightTapPending ? SlotHintState.TapPending : SlotHintState.Dimmed;
        }
        if (_slot >= 2 && _slot <= 5)
        {
            return baseSide == PadSide.Left ? SlotHintState.Reachable : SlotHintState.Dimmed;
        }
        if (_slot >= 7 && _slot <= 10)
        {
            return baseSide == PadSide.Right ? SlotHintState.Reachable : SlotHintState.Dimmed;
        }
        return SlotHintState.Dimmed;
    }

    /// <summary>For a face chord: the face button of the slot while a bumper is held (null for other slots).</summary>
    public static PadButton? FaceButtonOf(int _slot)
    {
        if (_slot >= 2 && _slot <= 5)
        {
            return FaceOrder[_slot - 2];
        }
        if (_slot >= 7 && _slot <= 10)
        {
            return FaceOrder[_slot - 7];
        }
        return null;
    }

    /// <summary>All slots (1..n) with their chord text, for the "gtb chords" table.</summary>
    public static IEnumerable<KeyValuePair<int, string>> Table(int _slotCount)
    {
        for (int slot = FirstSlot; slot <= Math.Min(_slotCount, MaxSlots); slot++)
        {
            yield return new KeyValuePair<int, string>(slot, Describe(slot));
        }
    }
}
