using System;
using System.Collections.Generic;
using System.Xml.Linq;
using GamepadToolbelt.Core;
using GamepadToolbelt.Engine;
using GamepadToolbelt.UI;

namespace GamepadToolbelt.Patches;

/// <summary>
/// Group "Toolbelt": every machine with the mod takes the toolbelt size from toolbelt.xml's gameconfig. The server's
/// toolbelt.xml (with or without our gameconfig) reaches clients through the vanilla config sync, so a client follows
/// whatever the server offers and keeps 10 slots on a vanilla server.
/// </summary>
internal static class ToolbeltConfigPatches
{
    /// <summary>Before each toolbelt.xml load: back to the vanilla layout (no value may survive from an earlier game).</summary>
    public static void LoadPrefix()
    {
        ResetToVanilla();
        XuiPatchHook.BeginCycle();
    }

    /// <summary>
    /// A client starts to wait for the server's configs: forget the previous world's toolbelt right away. If the
    /// received toolbelt.xml never loads (a failing client stage skips the load), the client keeps 10 slots instead of
    /// a stale 11 - it must never select index 10 on a server that does not know it.
    /// </summary>
    public static void WaitForConfigsFromServerPrefix()
    {
        ResetToVanilla();
        ToolbeltState.ResetConfig();
    }

    /// <summary>The vanilla toolbelt: 10 slots, one bar, one page.</summary>
    public static void ResetToVanilla()
    {
        ToolbeltConfig config = ToolbeltConfig.GameConfig;
        config.SlotsPerBar = ToolbeltConfigXml.VanillaSlots;
        config.BarsPerPage = 1;
        config.PageCount = 1;
    }

    /// <summary>ToolbeltFromXml.Load parses the editor config; right after it the game config is read.</summary>
    public static void ParseConfigPostfix(XElement root, string configId)
    {
        if (!string.Equals(configId, "editorconfig", StringComparison.Ordinal))
        {
            return;
        }
        try
        {
            var warnings = new List<string>();
            ToolbeltLayout layout = ToolbeltConfigXml.ParseGameConfig(root, warnings, out bool present);
            ToolbeltConfig config = ToolbeltConfig.GameConfig;
            config.SlotsPerBar = layout.SlotsPerBar;
            config.BarsPerPage = layout.BarsPerPage;
            config.PageCount = layout.PageCount;
            foreach (string warning in warnings)
            {
                ModLog.Warning(warning, "toolbelt_config");
            }
            ToolbeltState.SetLayout(present, layout, warnings);
            ModLog.Info($"Toolbelt: {layout.TotalSlots} slots ({(present ? "gameconfig of the " + (GameRole.IsServer ? "local" : "server's") + " toolbelt.xml" : "vanilla, no gameconfig")}).");
        }
        catch (Exception e)
        {
            ResetToVanilla();
            ModLog.Error("Reading the toolbelt gameconfig failed; the toolbelt keeps 10 slots.", e, "toolbelt_config");
        }
    }
}
