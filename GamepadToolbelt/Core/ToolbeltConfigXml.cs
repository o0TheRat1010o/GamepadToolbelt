using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;

namespace GamepadToolbelt.Core;

/// <summary>The game toolbelt layout parsed from toolbelt.xml.</summary>
public readonly struct ToolbeltLayout
{
    public readonly int SlotsPerBar;
    public readonly int BarsPerPage;
    public readonly int PageCount;

    public ToolbeltLayout(int _slotsPerBar, int _barsPerPage, int _pageCount)
    {
        SlotsPerBar = _slotsPerBar;
        BarsPerPage = _barsPerPage;
        PageCount = _pageCount;
    }

    public int TotalSlots => SlotsPerBar * BarsPerPage * PageCount;

    public static ToolbeltLayout Vanilla => new ToolbeltLayout(ToolbeltConfigXml.VanillaSlots, 1, 1);

    public override string ToString() => $"{SlotsPerBar}x{BarsPerPage}x{PageCount}";
}

/// <summary>
/// The game's toolbelt.xml has an unused <c>&lt;gameconfig&gt;</c> placeholder ("currently unsupported"). A server that
/// offers the extra slot adds it to the copy it sends to clients; every machine with the mod reads it back (vanilla
/// clients ignore it and keep 10 slots).
/// </summary>
public static class ToolbeltConfigXml
{
    public const int VanillaSlots = 10;
    public const int MaxSlots = SlotMap.MaxSlots;
    public const string GameConfigElement = "gameconfig";
    public const string SlotsElement = "slots";

    public enum InjectResult
    {
        Injected,
        AlreadyPresent,
        Foreign,
        NoRoot
    }

    /// <summary>
    /// Adds <c>&lt;gameconfig&gt;&lt;slots slot_per_bar="n" bar_per_page="1" page_count="1"/&gt;&lt;/gameconfig&gt;</c>.
    /// An existing gameconfig with exactly this content counts as present (idempotent); one with other values comes from
    /// another mod and is left untouched (<see cref="InjectResult.Foreign"/>).
    /// </summary>
    public static InjectResult InjectGameConfig(XDocument _doc, int _slots)
    {
        XElement root = _doc?.Root;
        if (root == null)
        {
            return InjectResult.NoRoot;
        }
        XElement existing = root.Element(GameConfigElement);
        if (existing != null)
        {
            XElement slots = existing.Element(SlotsElement);
            bool same = slots != null
                        && (string)slots.Attribute("slot_per_bar") == _slots.ToString(CultureInfo.InvariantCulture)
                        && (string)slots.Attribute("bar_per_page") == "1"
                        && (string)slots.Attribute("page_count") == "1";
            return same ? InjectResult.AlreadyPresent : InjectResult.Foreign;
        }
        root.Add(new XElement(GameConfigElement,
            new XElement(SlotsElement,
                new XAttribute("slot_per_bar", _slots.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("bar_per_page", "1"),
                new XAttribute("page_count", "1"))));
        return InjectResult.Injected;
    }

    /// <summary>
    /// Reads <c>gameconfig/slots</c>. Without one the layout is vanilla (10x1x1). Slots per bar are clamped to 1..11;
    /// only a single bar on a single page is supported for the game toolbelt (other values are reported and reset to 1).
    /// </summary>
    public static ToolbeltLayout ParseGameConfig(XElement _root, ICollection<string> _warnings, out bool _present)
    {
        _present = false;
        XElement slots = _root?.Element(GameConfigElement)?.Element(SlotsElement);
        if (slots == null)
        {
            return ToolbeltLayout.Vanilla;
        }
        _present = true;
        int perBar = ReadInt(slots, "slot_per_bar", VanillaSlots, 1, MaxSlots, _warnings);
        int bars = ReadInt(slots, "bar_per_page", 1, 1, 1, _warnings);
        int pages = ReadInt(slots, "page_count", 1, 1, 1, _warnings);
        return new ToolbeltLayout(perBar, bars, pages);
    }

    private static int ReadInt(XElement _element, string _attribute, int _default, int _min, int _max, ICollection<string> _warnings)
    {
        string raw = (string)_element.Attribute(_attribute);
        if (raw == null)
        {
            return _default;
        }
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            _warnings?.Add($"toolbelt.xml gameconfig: {_attribute}='{raw}' is not a number, using {_default}.");
            return _default;
        }
        int clamped = Math.Max(_min, Math.Min(_max, value));
        if (clamped != value)
        {
            _warnings?.Add($"toolbelt.xml gameconfig: {_attribute}={value} is outside {_min}..{_max}, using {clamped}.");
        }
        return clamped;
    }
}

/// <summary>
/// Toolbelt index rules for the network. Vanilla peers have 10 toolbelt slots and index their arrays with a received
/// toolbelt index without a range check, so a server never sends an index beyond the vanilla range: index 10 (slot 11)
/// goes out as 9. The holding packets still carry the held item stack, so other players see the correct item; their
/// copy of a remote player's toolbelt is display only and holds that item at index 9. Item action effects (shots,
/// throws, trigger releases) name the slot of the item as well and are clamped the same way, so they find that copy.
/// Receivers clamp anything outside their own toolbelt instead of throwing.
/// </summary>
public static class HoldingIndexPolicy
{
    public const int VanillaSlots = ToolbeltConfigXml.VanillaSlots;
    public const int WireMax = VanillaSlots - 1;

    /// <summary>Index to send to other players (server only): at most 9.</summary>
    public static byte ForWire(byte _index) => _index > WireMax ? (byte)WireMax : _index;

    public static bool NeedsWireClamp(byte _index) => _index > WireMax;

    /// <summary>Slot of an item action effect to send to other players (server only): at most 9; -1 (held item) stays.</summary>
    public static int EffectSlotForWire(int _slotIdx) => _slotIdx > WireMax ? WireMax : _slotIdx;

    /// <summary>
    /// Slot of an item action effect a receiver can look up in a toolbelt of <paramref name="_length"/> slots: an index
    /// outside it becomes -1, which vanilla resolves to the item the entity holds.
    /// </summary>
    public static int EffectSlotForReceiver(int _slotIdx, int _length) => _slotIdx >= _length ? -1 : _slotIdx;

    /// <summary>Index a receiver can apply to a toolbelt of <paramref name="_length"/> slots.</summary>
    public static byte ForReceiver(byte _index, int _length)
    {
        if (_length <= 0)
        {
            return 0;
        }
        return _index >= _length ? (byte)(_length - 1) : _index;
    }

    /// <summary>
    /// A holding index received for the local player itself (the server never sends one to its owner): indices of the
    /// extra slots are ignored instead of being applied.
    /// </summary>
    public static bool LocalPlayerIgnores(byte _index) => _index >= VanillaSlots;
}
