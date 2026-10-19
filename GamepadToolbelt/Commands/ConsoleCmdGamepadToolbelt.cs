using System;
using System.Collections.Generic;
using System.Linq;
using GamepadToolbelt.Core;
using GamepadToolbelt.Engine;
using GamepadToolbelt.Input;
using GamepadToolbelt.Patches;
using GamepadToolbelt.UI;

namespace GamepadToolbelt.Commands;

/// <summary>
/// Console command "gtb" (alias "gamepadtoolbelt"). It runs where it is typed - on a player's own game for the client
/// side, in the server console for the server side - because every machine has its own toolbelt state. A client runs
/// it locally even on a server without the mod (see DiagnosticsPatches.SendToServerPrefix).
/// </summary>
public sealed class ConsoleCmdGamepadToolbelt : ConsoleCmdAbstract
{
    private static readonly string[] Names = { "gamepadtoolbelt", "gtb" };

    public override bool IsExecuteOnClient => true;

    public override bool AllowedInMainMenu => true;

    public override int DefaultPermissionLevel => 1000;

    public override string[] getCommands() => (string[])Names.Clone();

    /// <summary>True when a console line calls this command (first word, any case).</summary>
    public static bool IsOwnCommandLine(string _line)
    {
        if (string.IsNullOrEmpty(_line))
        {
            return false;
        }
        string trimmed = _line.TrimStart();
        int end = trimmed.IndexOf(' ');
        string first = end < 0 ? trimmed : trimmed.Substring(0, end);
        return Names.Any(n => string.Equals(n, first, StringComparison.OrdinalIgnoreCase));
    }

    public override string getDescription() => "Gamepad Toolbelt: status, selftest, reload, loglevel, chords, screenshot";

    public override string getHelp()
    {
        return "Usage:\n" +
               "  gtb status                         - version, settings, patches, toolbelt size, controller, statistics, audit log\n" +
               "  gtb selftest                       - run all self checks (PASS/WARN/FAIL)\n" +
               "  gtb reload                         - re-read Settings.xml (ToolbeltSlots applies when a world is loaded)\n" +
               "  gtb loglevel off|summary|detailed  - change the audit log level for this session\n" +
               "  gtb chords                         - the controller layout of the current toolbelt\n" +
               "  gtb screenshot [name]              - save a screenshot of the HUD to Mods/GamepadToolbelt/Logs/screens";
    }

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        try
        {
            string sub = _params.Count == 0 ? "status" : _params[0].ToLowerInvariant();
            switch (sub)
            {
                case "status":
                    Output(Status());
                    break;
                case "selftest":
                    Output(SelfTest.Format(SelfTest.Run("command")));
                    break;
                case "reload":
                    Output(GamepadToolbeltMod.Reload());
                    break;
                case "loglevel":
                    Output(LogLevel(_params.Count > 1 ? _params[1] : null));
                    break;
                case "chords":
                    Output(Chords());
                    break;
                case "screenshot":
                {
                    string name = _params.Count > 1 ? new string(_params[1].Where(char.IsLetterOrDigit).Take(32).ToArray()) : "manual";
                    string path = SelfCheckScreenshots.Capture(name.Length == 0 ? "manual" : name);
                    Output(new[] { path == null ? "Screenshot failed (see log)." : "Screenshot: " + path });
                    break;
                }
                default:
                    Output(new[] { $"Unknown sub command '{_params[0]}'.", GetHelp() });
                    break;
            }
        }
        catch (Exception e)
        {
            ModLog.Error("Console command failed.", e, "command");
            Output(new[] { "gtb failed: " + e.GetType().Name + ": " + e.Message });
        }
    }

    // ----- sub commands -------------------------------------------------------------------------

    private static IEnumerable<string> Status()
    {
        ModSettings settings = GamepadToolbeltMod.Settings;
        EntityPlayerLocal player = GameManager.Instance?.World?.GetPrimaryPlayer();
        var table = new TextTable("Item", "Value");
        table.Add("Version", $"{GamepadToolbeltMod.ModVersion} (DLL {GamepadToolbeltMod.AssemblyVersion()}), game {Constants.cVersionInformation.LongString}");
        table.Add("Role", GamepadToolbeltMod.SessionActive ? $"{GameRole.Current}, session {AuditLog.SessionId} since {RuntimeStats.SessionStartUtc:HH:mm:ss} UTC" : "no world");
        bool dedicated = GameManager.IsDedicatedServer;
        if (!dedicated)
        {
            table.Add("Chords", !settings.Enabled ? "disabled (Enabled=false)" : PadLayer.DisabledByErrors ? "SWITCHED OFF after errors (gtb reload)" : $"enabled, tap {settings.TapMaxSeconds * 1000:0} ms, aiming {(settings.AllowWhileAiming ? "allowed" : "vanilla")}");
            table.Add("Hints", $"{settings.HintMode}, idle opacity {settings.HintIdleOpacity:0.##}, controller mode {(HintPresenter.ControllerMode ? "yes" : "no")}, visible {(HintPresenter.Visible ? "yes" : "no")}");
            table.Add("Haptics", settings.Haptics ? $"on, {settings.HapticStrength:0.##} for {settings.HapticMs} ms ({RuntimeStats.HapticPulses} pulses)" : "off");
            table.Add("Slot 11 key", KeyboardSlotKey.BoundKey?.ToString() ?? "none");
        }
        foreach (PatchGroupStatus group in PatchInstaller.Groups)
        {
            table.Add("Patches " + group.Name, group.State + (group.Installed ? $" ({group.Methods.Count} methods)" : string.Empty) + (group.Error != null ? ": " + group.Error : string.Empty));
        }
        ToolbeltConfig config = ToolbeltConfig.GameConfig;
        int? cells = player?.PlayerUI?.xui?.GetChildByType<XUiC_Toolbelt>()?.itemControllers?.Length;
        table.Add("Toolbelt", dedicated
            ? $"config {config.TotalSlots} slots"
            : $"config {config.TotalSlots} slots, inventory {player?.inventory?.Length.ToString() ?? "-"}, UI cells {cells?.ToString() ?? "-"} (XUi patched for {ToolbeltState.XuiSlotCount})");
        table.Add("Server offer", GameRole.IsServer ? $"{ToolbeltState.Offer.State}: {ToolbeltState.Offer.Slots} slots (ToolbeltSlots={settings.ToolbeltSlots})" : "client (follows the server)");
        if (!dedicated)
        {
            table.Add("Controller", PadLayer.CurrentDevice == null ? "none" : $"{PadLayer.CurrentDevice.Name} ({PadLayer.CurrentDevice.DeviceStyle}), gate {(PadLayer.LastGate.Open ? "open" : "closed: " + PadLayer.LastGate.Reason)}");
            table.Add("Selections", $"{RuntimeStats.Chords} chords, {RuntimeStats.KeyboardSelects} key, reached {RuntimeStats.Reached} (avg {RuntimeStats.LatencyMsAverage:0} ms, max {RuntimeStats.LatencyMsMax:0} ms), superseded {RuntimeStats.Superseded}, tap cancels {RuntimeStats.TapCancels}");
        }
        table.Add("Network", $"clamps {RuntimeStats.NetClamps}, guards {RuntimeStats.NetGuards}");
        table.Add("Health", $"errors {ModLog.ErrorCount}, warnings {ModLog.WarningCount}, invariant violations {RuntimeStats.InvariantViolations}, layer errors {RuntimeStats.LayerErrors}");
        table.Add("Selftest", SelfTest.Last == null ? "not run" : $"{SelfTest.Last.Overall} ({SelfTest.Last.Trigger})");
        table.Add("Audit log", AuditLog.IsOpen ? $"{AuditLog.Level}: {AuditLog.FilePath} ({AuditLog.Written} records, {AuditLog.Dropped} dropped)" : $"off ({settings.LogLevel})");
        return table.Lines();
    }

    private static IEnumerable<string> LogLevel(string _level)
    {
        if (!ModSettings.TryParseLevel(_level, out AuditLevel level))
        {
            return new[] { "Usage: gtb loglevel off|summary|detailed (current: " + GamepadToolbeltMod.Settings.LogLevel + ")" };
        }
        GamepadToolbeltMod.Settings = GamepadToolbeltMod.Settings.WithLogLevel(level);
        if (GamepadToolbeltMod.SessionActive)
        {
            AuditLog.SetLevel(level, GamepadToolbeltMod.ModPath);
            if (AuditLog.IsOpen)
            {
                JsonLineWriter record = AuditLog.Begin(AuditRecords.LogLevelChanged);
                record.Prop("level", level.ToString());
                AuditLog.Commit(record);
            }
        }
        return new[] { $"Audit level for this session: {level} (Settings.xml unchanged)." };
    }

    private static IEnumerable<string> Chords()
    {
        int slots = GameManager.Instance?.World?.GetPrimaryPlayer()?.inventory?.Length ?? ToolbeltConfig.GameConfig.TotalSlots;
        var table = new TextTable("Slot", "Controller", "Keyboard").AlignRight(0);
        foreach (KeyValuePair<int, string> pair in SlotMap.Table(SlotMap.MaxSlots))
        {
            string chord = pair.Value.Replace("+", " + ");
            if (pair.Key == SlotMap.FirstSlot || pair.Key == SlotMap.ExtraSlot)
            {
                chord += " (tap)";
            }
            string key = pair.Key <= 10 ? (pair.Key == 10 ? "0" : pair.Key.ToString()) : (KeyboardSlotKey.BoundKey?.ToString() ?? "-");
            table.Add(pair.Key, pair.Key > slots ? chord + "  [not available: " + slots + " slots]" : chord, key);
        }
        return table.Lines().Concat(new[] { $"Hold LB or RB and press A/X/Y/B as often as you like; tap = release within {GamepadToolbeltMod.Settings.TapMaxSeconds * 1000:0} ms." });
    }

    private static void Output(IEnumerable<string> _lines)
    {
        foreach (string line in _lines)
        {
            SingletonMonoBehaviour<SdtdConsole>.Instance.Output(line);
        }
    }
}
