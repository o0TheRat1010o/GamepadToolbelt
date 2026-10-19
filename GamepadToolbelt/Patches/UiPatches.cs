using System;
using GamepadToolbelt.Core;
using GamepadToolbelt.Engine;
using GamepadToolbelt.Input;
using GamepadToolbelt.UI;
using InControl;

namespace GamepadToolbelt.Patches;

/// <summary>
/// Group "UI": the client stage of XmlPatcher.ApplyConditionalXmlBlocks runs after the server cached its configs for
/// the clients (host) and for every config a client received (client). Patching the in-game XUi there keeps it local:
/// players without the mod get the unchanged UI from a modded host, players with the mod get the hints everywhere.
/// </summary>
internal static class UiPatches
{
    public const string TemplatesXml = "XUi_InGame/templates";
    public const string WindowsXml = "XUi_InGame/windows";

    public static void ApplyConditionalXmlBlocksPrefix(string _xmlName, XmlFile _xmlFile, XmlPatcher.EEvaluator _evaluator)
    {
        if (_evaluator != XmlPatcher.EEvaluator.Client || GameManager.IsDedicatedServer || _xmlFile?.XmlDoc == null || _xmlName == null)
        {
            return;
        }
        bool templates = string.Equals(_xmlName, TemplatesXml, StringComparison.OrdinalIgnoreCase);
        if (!templates && !string.Equals(_xmlName, WindowsXml, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        XuiPatchHook.Patch(templates, _xmlFile.XmlDoc);
    }
}

/// <summary>
/// Group "Diagnostics": runtime checks that the layer really keeps vanilla's toolbelt radial away, and the "gtb" console
/// command for clients.
/// </summary>
internal static class DiagnosticsPatches
{
    /// <summary>"gtb" commands a client ran locally instead of sending them to the server (for the harness).</summary>
    public static int LocalConsoleCommands;

    public static void SetupToolbeltMenuPrefix(int _direction)
    {
        try
        {
            if (_direction == 0)
            {
                return;   // quick-swap radial (D-pad), unrelated to the bumpers
            }
            PadButton bumper = _direction > 0 ? PadButton.RightBumper : PadButton.LeftBumper;
            if (!PadLayer.Engine.IsOwned(bumper))
            {
                return;
            }
            PlayerActionsLocal actions = GameManager.Instance?.World?.GetPrimaryPlayer()?.playerInput;
            PlayerAction action = _direction > 0 ? actions?.InventorySlotRight : actions?.InventorySlotLeft;
            if (action != null && (action.LastInputType != BindingSourceType.DeviceBindingSource || PadLayer.ActiveThroughUnmaskedControl(action)))
            {
                return;   // opened by the mouse wheel or by another controller binding of the action (D-pad, say)
            }
            Invariants.Report("vanilla_radial", $"the vanilla toolbelt radial opened ({(_direction > 0 ? "RB" : "LB")}) while the bumper is owned",
                r => r.Prop("direction", _direction));
        }
        catch (Exception e)
        {
            ModLog.Warning("Radial check failed: " + e.Message, "diagnostics");
        }
    }

    /// <summary>
    /// Prefix of ConnectionManager.SendToServer. A client's console sends every command to the server, and a server
    /// without the mod answers "Unknown command". "gtb" only reports and changes this machine, so a client runs it
    /// right here - the console's echo and history stay vanilla, only the routing changes. Never throws: any problem
    /// sends the package as usual.
    /// </summary>
    public static bool SendToServerPrefix(NetPackage _package)
    {
        if (!(_package is NetPackageConsoleCmdServer console))
        {
            return true;
        }
        try
        {
            string command = console.cmd;
            if (!Commands.ConsoleCmdGamepadToolbelt.IsOwnCommandLine(command))
            {
                return true;
            }
            LocalConsoleCommands++;
            GUIWindowConsole.AddLines(SingletonMonoBehaviour<SdtdConsole>.Instance.ExecuteSync(command, null));
            return false;
        }
        catch (Exception e)
        {
            ModLog.Warning("Running gtb locally failed, sending it to the server: " + e.Message, "console");
            return true;
        }
    }
}
