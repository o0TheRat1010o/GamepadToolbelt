using System.Text;

namespace GamepadToolbelt.Core;

/// <summary>The six physical controller buttons the chord layer reads (InControl: bumpers and Action1-4).</summary>
public enum PadButton
{
    LeftBumper = 0,
    RightBumper = 1,

    /// <summary>South face button (Xbox A, PlayStation Cross, InControl Action1).</summary>
    A = 2,

    /// <summary>East face button (Xbox B, PlayStation Circle, InControl Action2).</summary>
    B = 3,

    /// <summary>West face button (Xbox X, PlayStation Square, InControl Action3).</summary>
    X = 4,

    /// <summary>North face button (Xbox Y, PlayStation Triangle, InControl Action4).</summary>
    Y = 5
}

/// <summary>Which bumper a chord is based on.</summary>
public enum PadSide
{
    None = 0,
    Left = 1,
    Right = 2
}

/// <summary>How a slot was selected.</summary>
public enum ChordGesture
{
    /// <summary>A bumper tapped and released without a chord (slot 1 or 11).</summary>
    Tap,

    /// <summary>A face button pressed while a bumper is held (slots 2-5 and 7-10).</summary>
    Face,

    /// <summary>The second bumper pressed while the first is held (slot 6).</summary>
    BothBumpers,

    /// <summary>The keyboard key for slot 11.</summary>
    Keyboard
}

/// <summary>Physical pressed state of the six buttons in one input tick.</summary>
public struct PadSnapshot
{
    public const int ButtonCount = 6;

    private byte bits;

    public bool this[PadButton _button]
    {
        get => (bits & (1 << (int)_button)) != 0;
        set
        {
            if (value)
            {
                bits = (byte)(bits | (1 << (int)_button));
            }
            else
            {
                bits = (byte)(bits & ~(1 << (int)_button));
            }
        }
    }

    public bool Any => bits != 0;

    public byte Bits => bits;

    public static PadSnapshot Of(params PadButton[] _held)
    {
        var snapshot = new PadSnapshot();
        foreach (PadButton button in _held)
        {
            snapshot[button] = true;
        }
        return snapshot;
    }

    public static PadSnapshot FromBits(byte _bits) => new PadSnapshot { bits = (byte)(_bits & 0x3F) };

    public bool Equals(PadSnapshot _other) => bits == _other.bits;

    /// <summary>"LB+A", "RB", "-" (nothing held).</summary>
    public override string ToString()
    {
        if (bits == 0)
        {
            return "-";
        }
        var sb = new StringBuilder();
        for (int i = 0; i < ButtonCount; i++)
        {
            if ((bits & (1 << i)) == 0)
            {
                continue;
            }
            if (sb.Length > 0)
            {
                sb.Append('+');
            }
            sb.Append(SlotMap.ShortName((PadButton)i));
        }
        return sb.ToString();
    }
}

/// <summary>Everything the chord engine needs for one input tick.</summary>
public struct PadInput
{
    /// <summary>Identity of the device the buttons were read from; 0 = no usable controller.</summary>
    public long DeviceKey;

    public PadSnapshot Pad;

    /// <summary>True while the toolbelt is active (on foot, no menu, no radial, ...).</summary>
    public bool GateOpen;

    /// <summary>Unscaled time in seconds.</summary>
    public double Time;

    /// <summary>Slots of the toolbelt (10 vanilla, 11 with the extra slot).</summary>
    public int SlotCount;

    public double TapMaxSeconds;
}
