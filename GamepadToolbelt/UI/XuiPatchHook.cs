using System;
using System.Xml.Linq;
using GamepadToolbelt.Core;
using GamepadToolbelt.Engine;

namespace GamepadToolbelt.UI;

/// <summary>Runs the <see cref="ToolbeltXuiPatcher"/> on the in-game XUi files of one load cycle.</summary>
internal static class XuiPatchHook
{
    private static bool templateInstalled;

    /// <summary>toolbelt.xml always loads before the XUi files: a new cycle starts there.</summary>
    public static void BeginCycle()
    {
        templateInstalled = false;
    }

    public static void Patch(bool _templates, XDocument _document)
    {
        int slots = Math.Max(1, ToolbeltConfig.GameConfig.TotalSlots);
        var options = new XuiPatchOptions { SlotCount = slots, TemplateAvailable = templateInstalled };
        XuiPatchReport report;
        try
        {
            report = _templates ? ToolbeltXuiPatcher.PatchTemplates(_document, options) : ToolbeltXuiPatcher.PatchWindows(_document, options);
        }
        catch (Exception e)
        {
            report = new XuiPatchReport();
            report.Problems.Add(e.GetType().Name + ": " + e.Message);
            ModLog.Error($"Patching XUi {(_templates ? "templates" : "windows")} failed; the toolbelt stays vanilla{(slots > 10 ? " (but keeps " + slots + " slots)" : string.Empty)}.", e, "xui_patch");
        }
        if (_templates)
        {
            templateInstalled = report.TemplateInstalled;
        }
        foreach (string problem in report.Problems)
        {
            ModLog.Warning("XUi patch: " + problem, "xui_patch");
        }
        ToolbeltState.SetXuiReport(_templates, report, slots);
        if (!_templates)
        {
            ModLog.Info($"Toolbelt UI: {slots} slots, hints {(report.GridUsesTemplate ? "on" : "OFF")}, geometry {(report.GeometryPatched ? "ok" : "unchanged")}, XP bar {(report.XpBarWrapped ? "animated" : "fixed")}.");
        }
    }
}
