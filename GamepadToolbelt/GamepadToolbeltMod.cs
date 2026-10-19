using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GamepadToolbelt.Core;
using GamepadToolbelt.Engine;
using GamepadToolbelt.Net;
using GamepadToolbelt.Input;
using GamepadToolbelt.Patches;
using GamepadToolbelt.UI;
using UnityEngine;

namespace GamepadToolbelt;

/// <summary>
/// Mod entry point. Loads the settings, installs the patch groups and drives the session lifecycle on every machine
/// (singleplayer, host, client, dedicated server): at game start the audit log is opened, "session_start" written and
/// the selftest run; once the local player and the toolbelt UI exist, the selftest runs again with the player checks;
/// at world shutdown "session_end" is written.
/// </summary>
public sealed class GamepadToolbeltMod : IModApi
{
    public const string SettingsFileName = "Settings.xml";
    public const float StatsIntervalSeconds = 600f;

    internal static Mod ModInstance { get; private set; }
    internal static string ModPath { get; private set; } = string.Empty;
    internal static string ModVersion { get; private set; } = "?";

    /// <summary>Current settings (replaced by "gtb reload" and "gtb loglevel").</summary>
    internal static ModSettings Settings { get; set; } = ModSettings.Default;

    internal static readonly List<string> SettingsWarnings = new List<string>();
    internal static bool SessionActive { get; private set; }

    private static bool playerChecked;
    private static float nextStats;

    public void InitMod(Mod _modInstance)
    {
        ModInstance = _modInstance;
        ModPath = NormalizePath(_modInstance?.Path);
        ModVersion = _modInstance?.VersionString ?? AssemblyVersion();
        ModLog.Info($"Loading v{ModVersion} (game {Constants.cVersionInformation.LongString}).");

        // XUi resolves controllers with Type.GetType("Namespace.Type, GamepadToolbelt"); mod DLLs are not in the default
        // load context, so the name is resolved to this assembly here.
        AppDomain.CurrentDomain.AssemblyResolve += ResolveOwnAssembly;

        Settings = LoadSettings(SettingsWarnings);
        foreach (string warning in SettingsWarnings)
        {
            ModLog.Warning(warning, "settings");
        }

        PatchInstaller.InstallAll();
        if (!GameManager.IsDedicatedServer)
        {
            ApplyKeyboardKey();
        }

        ModEvents.GameStartDone.RegisterHandler(OnGameStartDone);
        ModEvents.GameUpdate.RegisterHandler(OnGameUpdate);
        ModEvents.WorldShuttingDown.RegisterHandler(OnWorldShuttingDown);
        ModEvents.GameShutdown.RegisterHandler(OnGameShutdown);

        string groups = string.Join(", ", PatchInstaller.Groups.Select(g => g.Name + "=" + g.State));
        ModLog.Info($"Loaded ({groups}); 11th slot {(PatchInstaller.ExtraSlotAllowed ? "available" : "NOT available (interlock)")}. Settings: {SettingsPath}");
    }

    // ----- settings ---------------------------------------------------------------------------

    internal static string SettingsPath => Path.Combine(ModPath, SettingsFileName);

    internal static ModSettings LoadSettings(ICollection<string> _warnings)
    {
        _warnings.Clear();
        string path = SettingsPath;
        try
        {
            if (!File.Exists(path))
            {
                _warnings.Add($"{SettingsFileName} not found at {path}; using defaults.");
                return ModSettings.Default;
            }
            return ModSettings.Parse(File.ReadAllText(path), _warnings);
        }
        catch (Exception e)
        {
            _warnings.Add($"{SettingsFileName} could not be read ({e.GetType().Name}: {e.Message}); using defaults.");
            return ModSettings.Default;
        }
    }

    /// <summary>"gtb reload": re-reads Settings.xml and applies everything that can change live.</summary>
    internal static IEnumerable<string> Reload()
    {
        var lines = new List<string>();
        var warnings = new List<string>();
        ModSettings previous = Settings;
        ModSettings next = LoadSettings(warnings);
        Settings = next;
        SettingsWarnings.Clear();
        SettingsWarnings.AddRange(warnings);
        foreach (string warning in warnings)
        {
            ModLog.Warning(warning, "settings");
            lines.Add("Warning: " + warning);
        }
        if (!GameManager.IsDedicatedServer)
        {
            ApplyKeyboardKey();
            PadLayer.ResetErrors();
            if (!next.Enabled)
            {
                PadLayer.Reset("disabled");
            }
        }
        if (SessionActive)
        {
            AuditLog.SetLevel(next.LogLevel, ModPath);
            if (AuditLog.IsOpen)
            {
                JsonLineWriter record = AuditLog.Begin(AuditRecords.Reload);
                WriteSettings(record);
                record.PropStrings("settings_warnings", SettingsWarnings);
                AuditLog.Commit(record);
            }
        }
        if (previous.ToolbeltSlots != next.ToolbeltSlots)
        {
            lines.Add($"Note: ToolbeltSlots={next.ToolbeltSlots} applies when the next world is hosted.");
        }
        string summary = "Settings reloaded: " + string.Join(", ", next.ToPairs().Select(p => p.Key + "=" + p.Value));
        ModLog.Info(summary + (warnings.Count > 0 ? $" ({warnings.Count} warning(s))" : string.Empty));
        lines.Add(summary);
        return lines;
    }

    private static void ApplyKeyboardKey()
    {
        string error = KeyboardSlotKey.Configure(Settings.KeyboardSlot11Key, Settings.HasKeyboardSlot11Key);
        if (error != null)
        {
            SettingsWarnings.Add(error);
            ModLog.Warning(error, "settings");
        }
    }

    // ----- session lifecycle ------------------------------------------------------------------

    private static void OnGameStartDone(ref ModEvents.SGameStartDoneData _data)
    {
        try
        {
            StartSession();
        }
        catch (Exception e)
        {
            ModLog.Error("Starting the session failed.", e, "session_start");
        }
    }

    private static void StartSession()
    {
        if (SessionActive)
        {
            EndSession("restart");
        }
        RuntimeStats.Reset();
        ModLog.ResetCounters();
        Invariants.Reset();
        NetPatches.ResetSession();
        SelfCheckScreenshots.ResetSession();

        string sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
        AuditLog.Open(ModPath, Settings.LogLevel, sessionId);
        SessionActive = true;
        playerChecked = GameManager.IsDedicatedServer;
        nextStats = Time.realtimeSinceStartup + StatsIntervalSeconds;

        WriteSessionStart();
        SelfTestResult selftest = SelfTest.Run("auto");
        string toolbelt = ToolbeltState.ConfigsPending ? "toolbelt from the server pending" : $"toolbelt {ToolbeltConfig.GameConfig.TotalSlots} slots";
        ModLog.Info($"ready: v{ModVersion}, {GameRole.Current}, {toolbelt}, " +
                    $"offer {(GameRole.IsServer ? ToolbeltState.Offer.State : "-")}, selftest {selftest.Overall}, audit {(AuditLog.IsOpen ? AuditLog.FilePath : "off")}");
    }

    internal static void EndSession(string _reason)
    {
        if (!SessionActive)
        {
            return;
        }
        try
        {
            if (!GameManager.IsDedicatedServer)
            {
                PadLayer.Reset(_reason);
                Haptics.Stop();
            }
            if (AuditLog.IsOpen)
            {
                JsonLineWriter record = AuditLog.Begin(AuditRecords.SessionEnd);
                record.Prop("reason", _reason).Prop("role", GameRole.Current);
                RuntimeStats.Write(record);
                AuditLog.Commit(record);
            }
        }
        catch (Exception e)
        {
            ModLog.Error("Writing session_end failed.", e, "session_end");
        }
        finally
        {
            AuditLog.Close();
            SessionActive = false;
        }
    }

    private static void OnGameUpdate(ref ModEvents.SGameUpdateData _data)
    {
        if (!SessionActive)
        {
            return;
        }
        try
        {
            if (!playerChecked && PlayerReady())
            {
                playerChecked = true;
                WritePlayerReady();
                SelfTest.Run("player_ready");
            }
            if (!GameManager.IsDedicatedServer)
            {
                SelfCheckScreenshots.Update();
            }
            if (Time.realtimeSinceStartup >= nextStats)
            {
                nextStats = Time.realtimeSinceStartup + StatsIntervalSeconds;
                WriteStats();
            }
        }
        catch (Exception e)
        {
            ModLog.Error("Session update failed.", e, "session_update");
        }
    }

    private static bool PlayerReady()
    {
        EntityPlayerLocal player = GameManager.Instance?.World?.GetPrimaryPlayer();
        if (player == null || !player.Spawned || player.inventory == null)
        {
            return false;
        }
        XUiC_Toolbelt toolbelt = player.PlayerUI?.xui?.GetChildByType<XUiC_Toolbelt>();
        return toolbelt?.itemControllers != null && toolbelt.itemControllers.Length > 0;
    }

    private static void OnWorldShuttingDown(ref ModEvents.SWorldShuttingDownData _data)
    {
        EndSession("world_shutdown");
        // Nothing of this world's toolbelt may leak into the next game (a client joining a server without the mod
        // must start at 10 slots). Only the UI and the loaders read the config, never the saves that follow.
        ToolbeltState.ResetForWorld();
        if (PatchInstaller.IsInstalled(PatchInstaller.ToolbeltGroup))
        {
            ToolbeltConfigPatches.ResetToVanilla();
        }
        HintPresenter.Reset();
    }

    private static void OnGameShutdown(ref ModEvents.SGameShutdownData _data)
    {
        EndSession("game_shutdown");
        AuditLog.Close();
    }

    internal static void WriteStats()
    {
        if (!AuditLog.Summary)
        {
            return;
        }
        JsonLineWriter record = AuditLog.Begin(AuditRecords.Stats);
        RuntimeStats.Write(record);
        AuditLog.Commit(record);
    }

    // ----- records ------------------------------------------------------------------------------

    private static void WriteSessionStart()
    {
        if (!AuditLog.IsOpen)
        {
            return;
        }
        World world = GameManager.Instance?.World;
        JsonLineWriter record = AuditLog.Begin(AuditRecords.SessionStart);
        record.BeginObject("mod")
            .Prop("name", ModInstance?.Name ?? "GamepadToolbelt")
            .Prop("version", ModVersion)
            .Prop("assembly", AssemblyVersion())
            .EndObject();
        record.BeginObject("game")
            .Prop("version", Constants.cVersionInformation.LongString)
            .Prop("role", GameRole.Current)
            .Prop("server", GameRole.IsServer)
            .Prop("dedicated", GameManager.IsDedicatedServer)
            .Prop("world", GamePrefs.GetString(EnumGamePrefs.GameWorld))
            .Prop("game_name", GamePrefs.GetString(EnumGamePrefs.GameName))
            .Prop("players_online", world?.Players?.list?.Count ?? 0)
            .EndObject();
        WriteSettings(record);
        record.PropStrings("settings_warnings", SettingsWarnings);
        record.BeginArray("patches");
        foreach (PatchGroupStatus group in PatchInstaller.Groups)
        {
            record.BeginObject().Prop("group", group.Name).Prop("state", group.State).PropStrings("targets", group.Targets);
            if (group.Error != null)
            {
                record.Prop("error", group.Error);
            }
            record.EndObject();
        }
        record.EndArray();
        record.Prop("extra_slot_allowed", PatchInstaller.ExtraSlotAllowed);
        ToolbeltState.WriteOffer(record);
        ToolbeltState.WriteLayout(record);
        ToolbeltState.WriteXui(record);
        // A client starts its session on connect, before the server's configs arrive: its toolbelt values are the
        // defaults until the "config" records (layout, player_ready) follow.
        record.BeginObject("toolbelt").Prop("config_slots", ToolbeltConfig.GameConfig.TotalSlots).Prop("configs_pending", ToolbeltState.ConfigsPending).EndObject();
        record.BeginObject("log").Prop("level", AuditLog.Level.ToString()).Prop("file", AuditLog.FilePath).EndObject();
        AuditLog.Commit(record, _flushSoon: true);
    }

    /// <summary>The toolbelt as the local player got it: config, inventory, UI cells, hint controllers, controller.</summary>
    private static void WritePlayerReady()
    {
        if (!AuditLog.Summary)
        {
            return;
        }
        EntityPlayerLocal player = GameManager.Instance.World.GetPrimaryPlayer();
        XUiC_Toolbelt toolbelt = player.PlayerUI?.xui?.GetChildByType<XUiC_Toolbelt>();
        JsonLineWriter record = AuditLog.Begin(AuditRecords.Config);
        record.Prop("event", "player_ready")
            .Prop("config_slots", ToolbeltConfig.GameConfig.TotalSlots)
            .Prop("inventory_slots", player.inventory.Length)
            .Prop("ui_cells", toolbelt?.itemControllers?.Length ?? 0)
            .Prop("hint_controllers", toolbelt?.itemControllers?.OfType<XUiC_GtbToolbeltSlot>().Count() ?? 0)
            .Prop("hint_views_ok", toolbelt?.itemControllers?.OfType<XUiC_GtbToolbeltSlot>().Count(s => s.HintViewsResolved) ?? 0)
            .Prop("xp_controllers", XUiC_GtbHintState.LiveInstances)
            .Prop("holding", player.inventory.holdingItemIdx + 1)
            .Prop("device", PadLayer.CurrentDevice?.Name)
            .Prop("device_style", PadLayer.CurrentDevice?.DeviceStyle.ToString())
            .Prop("icon_style", Platform.PlayerInputManager.InputStyleFromSelectedIconStyle().ToString())
            .Prop("keyboard_key", KeyboardSlotKey.BoundKey?.ToString());
        ToolbeltState.WriteLayout(record);
        AuditLog.Commit(record, _flushSoon: true);
    }

    private static void WriteSettings(JsonLineWriter _record)
    {
        _record.BeginObject("settings");
        foreach (KeyValuePair<string, string> pair in Settings.ToPairs())
        {
            _record.Prop(pair.Key, pair.Value);
        }
        _record.EndObject();
    }

    // ----- helpers ------------------------------------------------------------------------------

    private static Assembly ResolveOwnAssembly(object _sender, ResolveEventArgs _args)
    {
        Assembly own = typeof(GamepadToolbeltMod).Assembly;
        try
        {
            string name = new AssemblyName(_args.Name).Name;
            return string.Equals(name, own.GetName().Name, StringComparison.OrdinalIgnoreCase) ? own : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The loader reports paths like ".../7DaysToDie_Data/../Mods\Name"; resolve them for readable logs.</summary>
    private static string NormalizePath(string _path)
    {
        if (string.IsNullOrEmpty(_path))
        {
            return string.Empty;
        }
        try
        {
            return Path.GetFullPath(_path);
        }
        catch (Exception)
        {
            return _path;
        }
    }

    internal static string AssemblyVersion()
    {
        Assembly assembly = typeof(GamepadToolbeltMod).Assembly;
        var informational = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false).OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault();
        return informational?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "?";
    }
}
