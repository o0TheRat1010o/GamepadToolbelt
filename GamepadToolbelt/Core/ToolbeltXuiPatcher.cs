using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GamepadToolbelt.Core;

/// <summary>Pixel geometry of the HUD toolbelt for a slot count (10 slots = the vanilla values).</summary>
public readonly struct ToolbeltGeometry
{
    public const int CellSize = 75;
    public const int FramePadding = 3;
    public const int VanillaSlots = 10;

    public readonly int Slots;

    public ToolbeltGeometry(int _slots)
    {
        Slots = _slots;
    }

    /// <summary>Window width: 3 + n x 75 (753 for 10 slots).</summary>
    public int WindowWidth => FramePadding + Slots * CellSize;

    /// <summary>Window x position (bottom anchored, centred): -floor(width / 2) (-376 for 10 slots).</summary>
    public int WindowX => -(WindowWidth / 2);

    /// <summary>Width of the XP fill bar: n x 75 (750 for 10 slots).</summary>
    public int XpWidth => Slots * CellSize;

    /// <summary>Centre x of the page arrows: floor(width / 2) - 1 (375 for 10 slots).</summary>
    public int PagerX => WindowWidth / 2 - 1;

    /// <summary>X of the creative-menu trash button: n x 75 + 50 (800 for 10 slots).</summary>
    public int ClearButtonX => Slots * CellSize + 50;

    public static ToolbeltGeometry Vanilla => new ToolbeltGeometry(VanillaSlots);
}

public sealed class XuiPatchOptions
{
    public const string DefaultSlotController = "GamepadToolbelt.UI.GtbToolbeltSlot, GamepadToolbelt";
    public const string DefaultHintStateController = "GamepadToolbelt.UI.GtbHintState, GamepadToolbelt";

    public int SlotCount = ToolbeltGeometry.VanillaSlots;
    public string SlotController = DefaultSlotController;
    public string HintStateController = DefaultHintStateController;

    /// <summary>Install the hint template and point the toolbelt grid at it (geometry is patched either way).</summary>
    public bool Hints = true;

    /// <summary>
    /// Whether the template file of the same load cycle got the hint template; the window only switches to it then
    /// (an unknown template would break the toolbelt).
    /// </summary>
    public bool TemplateAvailable = true;
}

/// <summary>What a patch run changed or could not change (written to the audit log and checked by the selftest).</summary>
public sealed class XuiPatchReport
{
    public readonly List<string> Applied = new List<string>();
    public readonly List<string> Skipped = new List<string>();
    public readonly List<string> Problems = new List<string>();

    public bool TemplateInstalled;
    public bool GridUsesTemplate;
    public bool GeometryPatched;
    public bool XpBarWrapped;

    public bool Ok => Problems.Count == 0;

    public override string ToString()
    {
        string text = $"template={TemplateInstalled} grid={GridUsesTemplate} geometry={GeometryPatched} xpbar={XpBarWrapped}";
        if (Problems.Count > 0)
        {
            text += " problems: " + string.Join("; ", Problems);
        }
        return text;
    }
}

/// <summary>
/// Client-local patches of the in-game XUi (never sent to other players): a toolbelt slot template with the controller
/// hint plate, the toolbelt grid switched to it, the window geometry for the slot count, and the XP bar wrapped into a
/// rect that can slide up while hints are shown. Every step validates the vanilla structure first; anything unexpected
/// (another UI mod, a game update) is reported and that step is left out, so the toolbelt itself always keeps working.
/// The patch is idempotent.
/// </summary>
public static class ToolbeltXuiPatcher
{
    public const string TemplateName = "gtb_toolbelt_stack";
    public const string SourceTemplateName = "item_stack";
    public const string ToolbeltWindowName = "windowToolbelt";
    public const string HintRectName = "gtbHint";
    public const string FlashName = "gtbFlash";
    public const string XpRectName = "gtbXpBar";

    /// <summary>View ids of the hint plate (looked up by the slot controller).</summary>
    public static readonly string[] HintViewIds = { HintRectName, "gtbShadow", "gtbPlate", "gtbFrame", "gtbRing", "gtbRingHole", "gtbGlyph1", "gtbGlyph2", FlashName };

    // Plate layout (pixels): glyphs of 22 px (the bumper glyphs are pills, so smaller sizes are unreadable at 800p),
    // 4 px padding, 2 px gap; the plate's right-middle point sits 2 px inside the slot's top-right corner, so its centre
    // line lies on the slot's top edge. The tap timer is a ring: a radial pie with a dark hole on top of it.
    public const int GlyphSize = 22;
    public const int PlateHeight = 28;
    public const int PlatePadding = 4;
    public const int GlyphGap = 2;
    public const int PlateInset = 2;
    public const int RingSize = 30;
    public const int RingHoleSize = 24;
    public const int QuickSwapIconY = -15;

    public static int PlateWidth(int _glyphs) => _glyphs <= 1 ? PlatePadding * 2 + GlyphSize : PlatePadding * 2 + GlyphSize * 2 + GlyphGap;

    /// <summary>Centre x of a glyph relative to the plate's right edge (negative = left of it).</summary>
    public static int GlyphX(int _glyphs, int _index)
    {
        int width = PlateWidth(_glyphs);
        int left = -width + PlatePadding;
        return left + GlyphSize / 2 + _index * (GlyphSize + GlyphGap);
    }

    // ----- templates.xml ------------------------------------------------------------------------

    /// <summary>Adds (or refreshes) <see cref="TemplateName"/>: a copy of item_stack plus the hint plate.</summary>
    public static XuiPatchReport PatchTemplates(XDocument _templates, XuiPatchOptions _options)
    {
        var report = new XuiPatchReport();
        XElement root = _templates?.Root;
        if (root == null)
        {
            report.Problems.Add("templates: document has no root");
            return report;
        }
        if (!_options.Hints)
        {
            report.Skipped.Add("templates: hints disabled");
            return report;
        }
        XElement source = root.Elements(SourceTemplateName).FirstOrDefault();
        if (source == null)
        {
            report.Problems.Add("templates: item_stack template not found");
            return report;
        }
        List<XElement> sourceChildren = source.Elements().ToList();
        if (sourceChildren.Count != 1 || sourceChildren[0].Name.LocalName != "rect")
        {
            report.Problems.Add("templates: item_stack does not have exactly one rect child");
            return report;
        }
        XElement slotRect = sourceChildren[0];
        if (!slotRect.Descendants().Any(e => (string)e.Attribute("name") == "itemIcon"))
        {
            report.Problems.Add("templates: item_stack has no itemIcon (required by XUiC_ItemStack)");
            return report;
        }
        string[] clashes = HintViewIds.Where(id => slotRect.Descendants().Any(e => (string)e.Attribute("name") == id)).ToArray();
        if (clashes.Length > 0)
        {
            report.Problems.Add("templates: item_stack already uses the names " + string.Join(", ", clashes));
            return report;
        }

        foreach (XElement old in root.Elements(TemplateName).ToList())
        {
            old.Remove();
            report.Applied.Add("templates: replaced an existing " + TemplateName);
        }

        var template = new XElement(source) { Name = TemplateName };
        template.SetAttributeValue("controller", _options.SlotController);
        XElement rect = template.Elements().Single();

        XElement quickSwap = rect.Elements("sprite").FirstOrDefault(e => (string)e.Attribute("name") == "quickswapIcon");
        if (quickSwap != null && TryParsePos((string)quickSwap.Attribute("pos"), out int qx, out int qy))
        {
            // The quick-swap marker shares the top-right corner with the plate: move it below the plate's lower half.
            if (qy > QuickSwapIconY)
            {
                quickSwap.SetAttributeValue("pos", Pos(qx, QuickSwapIconY));
                report.Applied.Add($"templates: quickswapIcon {qx},{qy} -> {qx},{QuickSwapIconY}");
            }
        }
        else
        {
            report.Skipped.Add("templates: quickswapIcon not found or without position (left unchanged)");
        }

        rect.Add(new XComment(" GamepadToolbelt: selection flash and controller hint plate "));
        rect.Add(HintElements());
        source.AddAfterSelf(template);
        report.TemplateInstalled = true;
        report.Applied.Add("templates: added " + TemplateName);
        return report;
    }

    /// <summary>The flash frame and the hint plate, laid out for two glyphs (the controller re-lays one-glyph slots).</summary>
    public static IEnumerable<XElement> HintElements()
    {
        int width = PlateWidth(2);
        yield return Sprite(17, FlashName, "menu_empty3px", "255,210,120,0", "-3,3", "${3+cell_size}", "${3+cell_size}", null,
            new XAttribute("type", "sliced"), new XAttribute("fillcenter", "false"), new XAttribute("globalopacitymod", "0"));
        yield return new XElement("rect",
            new XAttribute("depth", "20"),
            new XAttribute("name", HintRectName),
            new XAttribute("pos", "${cell_size - " + PlateInset.ToString(CultureInfo.InvariantCulture) + "},0"),
            new XAttribute("width", "2"),
            new XAttribute("height", "2"),
            Sprite(20, "gtbShadow", "menu_empty", "0,0,0,120", "2,-2", W(width), W(PlateHeight), "Right", new XAttribute("type", "sliced"), new XAttribute("globalopacitymod", "0")),
            Sprite(21, "gtbPlate", "menu_empty", "14,14,14,225", "0,0", W(width), W(PlateHeight), "Right", new XAttribute("type", "sliced"), new XAttribute("globalopacitymod", "0")),
            Sprite(22, "gtbFrame", "menu_empty2px", "110,110,110,255", "0,0", W(width), W(PlateHeight), "Right",
                new XAttribute("type", "sliced"), new XAttribute("fillcenter", "false"), new XAttribute("globalopacitymod", "0")),
            Sprite(23, "gtbRing", "ui_game_filled_circle", "255,210,120,230", Pos(GlyphX(1, 0), 0), W(RingSize), W(RingSize), "Center",
                new XAttribute("type", "filled"), new XAttribute("globalopacitymod", "0")),
            Sprite(24, "gtbRingHole", "ui_game_filled_circle", "14,14,14,240", Pos(GlyphX(1, 0), 0), W(RingHoleSize), W(RingHoleSize), "Center",
                new XAttribute("globalopacitymod", "0")),
            Glyph("gtbGlyph1", GlyphX(2, 0), "XB_Button_LeftBumper"),
            Glyph("gtbGlyph2", GlyphX(2, 1), "XB_Button_Action1"));
    }

    // ----- windows.xml --------------------------------------------------------------------------

    public static XuiPatchReport PatchWindows(XDocument _windows, XuiPatchOptions _options)
    {
        var report = new XuiPatchReport();
        XElement window = _windows?.Root?.Elements("window").FirstOrDefault(e => (string)e.Attribute("name") == ToolbeltWindowName);
        if (window == null)
        {
            report.Problems.Add("windows: windowToolbelt not found");
            return report;
        }
        XElement toolbeltRect = window.Descendants("rect").FirstOrDefault(e => (string)e.Attribute("controller") == "Toolbelt");
        XElement grid = toolbeltRect?.Descendants("grid").FirstOrDefault(e => ((string)e.Attribute("name") ?? string.Empty).StartsWith("inventory", StringComparison.Ordinal));
        if (grid == null)
        {
            report.Problems.Add("windows: toolbelt grid (rect controller=Toolbelt / grid inventory*) not found");
            return report;
        }
        if (!((string)grid.Attribute("cols") ?? string.Empty).Contains("beltslots"))
        {
            report.Problems.Add("windows: toolbelt grid columns are not driven by {beltslots} (another UI mod?), left unchanged");
            return report;
        }

        PatchGrid(grid, _options, report);
        PatchGeometry(window, toolbeltRect, new ToolbeltGeometry(_options.SlotCount), report);
        WrapXpBar(window, _options, report);
        return report;
    }

    private static void PatchGrid(XElement _grid, XuiPatchOptions _options, XuiPatchReport _report)
    {
        List<XElement> cells = _grid.Elements().ToList();
        if (cells.Count != 1)
        {
            _report.Problems.Add($"windows: toolbelt grid has {cells.Count} cell templates instead of 1, left unchanged");
            return;
        }
        XElement cell = cells[0];
        if (cell.Name.LocalName == TemplateName)
        {
            _report.GridUsesTemplate = true;
            _report.Skipped.Add("windows: grid already uses " + TemplateName);
            return;
        }
        if (cell.Name.LocalName != SourceTemplateName)
        {
            _report.Problems.Add($"windows: toolbelt grid uses '{cell.Name.LocalName}' instead of item_stack (another UI mod?), hints left out");
            return;
        }
        if (!_options.Hints || !_options.TemplateAvailable)
        {
            _report.Skipped.Add("windows: hint template not available, grid keeps item_stack");
            return;
        }
        if (cell.Attribute("controller") != null)
        {
            _report.Problems.Add("windows: toolbelt item_stack has its own controller (another UI mod?), hints left out");
            return;
        }
        cell.Name = TemplateName;
        _report.GridUsesTemplate = true;
        _report.Applied.Add("windows: toolbelt grid uses " + TemplateName);
    }

    private static void PatchGeometry(XElement _window, XElement _toolbeltRect, ToolbeltGeometry _geometry, XuiPatchReport _report)
    {
        ToolbeltGeometry vanilla = ToolbeltGeometry.Vanilla;
        bool all = true;

        // Window width and centred x position.
        if ((string)_window.Attribute("width") == Num(_geometry.WindowWidth) && PosX(_window) == _geometry.WindowX)
        {
            _report.Skipped.Add("windows: window geometry already matches");
        }
        else if ((string)_window.Attribute("width") == Num(vanilla.WindowWidth) && PosX(_window) == vanilla.WindowX && TryParsePos((string)_window.Attribute("pos"), out _, out int y))
        {
            _window.SetAttributeValue("width", Num(_geometry.WindowWidth));
            _window.SetAttributeValue("pos", Pos(_geometry.WindowX, y));
            _report.Applied.Add($"windows: window width {vanilla.WindowWidth} -> {_geometry.WindowWidth}, x {vanilla.WindowX} -> {_geometry.WindowX}");
        }
        else
        {
            all = false;
            _report.Problems.Add($"windows: unexpected window geometry width={(string)_window.Attribute("width")} pos={(string)_window.Attribute("pos")}, left unchanged");
        }

        // XP fill bar width.
        XElement xpFill = _window.Descendants("sprite").FirstOrDefault(e => (string)e.Attribute("fill") == "{xp}");
        if (xpFill == null)
        {
            all = false;
            _report.Problems.Add("windows: XP fill sprite not found");
        }
        else if ((string)xpFill.Attribute("width") == Num(_geometry.XpWidth))
        {
            _report.Skipped.Add("windows: XP width already matches");
        }
        else if ((string)xpFill.Attribute("width") == Num(vanilla.XpWidth))
        {
            xpFill.SetAttributeValue("width", Num(_geometry.XpWidth));
            _report.Applied.Add($"windows: XP width {vanilla.XpWidth} -> {_geometry.XpWidth}");
        }
        else
        {
            all = false;
            _report.Problems.Add($"windows: unexpected XP width {(string)xpFill.Attribute("width")}, left unchanged");
        }

        // Page arrows: pos="{%'375, ' + (beltCount * 82 - 60)}".
        XElement pager = _toolbeltRect.Descendants("rect").FirstOrDefault(e => (string)e.Attribute("name") == "toolbeltPager");
        string pagerPos = (string)pager?.Attribute("pos");
        string vanillaLiteral = "'" + Num(vanilla.PagerX) + ", '";
        string targetLiteral = "'" + Num(_geometry.PagerX) + ", '";
        if (pagerPos == null)
        {
            all = false;
            _report.Problems.Add("windows: toolbeltPager not found");
        }
        else if (pagerPos.Contains(targetLiteral))
        {
            _report.Skipped.Add("windows: pager position already matches");
        }
        else if (pagerPos.Contains(vanillaLiteral))
        {
            pager.SetAttributeValue("pos", pagerPos.Replace(vanillaLiteral, targetLiteral));
            _report.Applied.Add($"windows: pager x {vanilla.PagerX} -> {_geometry.PagerX}");
        }
        else
        {
            all = false;
            _report.Problems.Add($"windows: unexpected pager position '{pagerPos}', left unchanged");
        }

        // Creative-menu trash button right of the slots.
        XElement clear = _toolbeltRect.Descendants("button").FirstOrDefault(e => ((string)e.Attribute("name") ?? string.Empty).StartsWith("btnClearInventory", StringComparison.Ordinal));
        if (clear == null || !TryParsePos((string)clear.Attribute("pos"), out int cx, out int cy))
        {
            all = false;
            _report.Problems.Add("windows: btnClearInventory not found");
        }
        else if (cx == _geometry.ClearButtonX)
        {
            _report.Skipped.Add("windows: clear button already matches");
        }
        else if (cx == vanilla.ClearButtonX)
        {
            clear.SetAttributeValue("pos", Pos(_geometry.ClearButtonX, cy));
            _report.Applied.Add($"windows: clear button x {vanilla.ClearButtonX} -> {_geometry.ClearButtonX}");
        }
        else
        {
            all = false;
            _report.Problems.Add($"windows: unexpected clear button position {cx},{cy}, left unchanged");
        }

        _report.GeometryPatched = all;
    }

    /// <summary>Moves the three XP bar sprites into a rect with the hint state controller (it slides the bar up).</summary>
    private static void WrapXpBar(XElement _window, XuiPatchOptions _options, XuiPatchReport _report)
    {
        XElement existing = _window.Descendants("rect").FirstOrDefault(e => (string)e.Attribute("name") == XpRectName);
        if (existing != null)
        {
            _report.XpBarWrapped = true;
            _report.Skipped.Add("windows: XP bar already wrapped");
            return;
        }
        if (!_options.Hints)
        {
            _report.Skipped.Add("windows: hints disabled, XP bar left in place");
            return;
        }
        XElement rootRect = _window.Elements("rect").FirstOrDefault();
        List<XElement> xpSprites = rootRect?.Elements("sprite").Where(e => (string)e.Attribute("visible") == "{showxp}").ToList() ?? new List<XElement>();
        if (xpSprites.Count != 3)
        {
            _report.Problems.Add($"windows: expected 3 XP bar sprites below the window rect, found {xpSprites.Count}; XP bar left in place");
            return;
        }
        // No size or depth: like the sprites' former parent, the wrapper inherits both from the window rect, so the
        // sprites keep their exact layout and draw order.
        var wrapper = new XElement("rect",
            new XAttribute("name", XpRectName),
            new XAttribute("controller", _options.HintStateController),
            new XAttribute("pos", "0,0"));
        xpSprites[0].AddBeforeSelf(wrapper);
        foreach (XElement sprite in xpSprites)
        {
            sprite.Remove();
            wrapper.Add(sprite);
        }
        _report.XpBarWrapped = true;
        _report.Applied.Add("windows: XP bar wrapped into " + XpRectName);
    }

    // ----- helpers ------------------------------------------------------------------------------

    private static XElement Sprite(int _depth, string _name, string _sprite, string _color, string _pos, string _width, string _height, string _pivot, params XAttribute[] _extra)
    {
        var element = new XElement("sprite",
            new XAttribute("depth", Num(_depth)),
            new XAttribute("name", _name),
            new XAttribute("sprite", _sprite),
            new XAttribute("color", _color),
            new XAttribute("pos", _pos),
            new XAttribute("width", _width),
            new XAttribute("height", _height));
        if (_pivot != null)
        {
            element.Add(new XAttribute("pivot", _pivot));
        }
        element.Add(_extra);
        return element;
    }

    private static XElement Glyph(string _name, int _x, string _sprite)
    {
        return new XElement("sprite",
            new XAttribute("depth", "25"),
            new XAttribute("name", _name),
            new XAttribute("atlas", "SymbolAtlas"),
            new XAttribute("sprite", _sprite),
            new XAttribute("pos", Pos(_x, 0)),
            new XAttribute("width", Num(GlyphSize)),
            new XAttribute("height", Num(GlyphSize)),
            new XAttribute("pivot", "Center"),
            new XAttribute("globalopacitymod", "0"));
    }

    private static string W(int _value) => Num(_value);

    private static string Num(int _value) => _value.ToString(CultureInfo.InvariantCulture);

    private static string Pos(int _x, int _y) => Num(_x) + "," + Num(_y);

    private static int? PosX(XElement _element) => TryParsePos((string)_element.Attribute("pos"), out int x, out _) ? x : (int?)null;

    private static readonly Regex PosPattern = new Regex(@"^\s*(-?\d+)\s*,\s*(-?\d+)\s*$", RegexOptions.Compiled);

    public static bool TryParsePos(string _pos, out int _x, out int _y)
    {
        _x = 0;
        _y = 0;
        if (_pos == null)
        {
            return false;
        }
        Match match = PosPattern.Match(_pos);
        if (!match.Success)
        {
            return false;
        }
        _x = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        _y = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }
}
