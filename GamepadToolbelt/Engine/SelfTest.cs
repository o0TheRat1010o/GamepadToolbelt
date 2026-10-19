using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using GamepadToolbelt.Core;
using GamepadToolbelt.Input;
using GamepadToolbelt.Net;
using GamepadToolbelt.Patches;
using GamepadToolbelt.UI;
using InControl;

namespace GamepadToolbelt.Engine;

internal sealed class SelfTestCheck
{
    public string Name;
    public string Status;
    public string Detail;
}

internal sealed class SelfTestResult
{
    public string Trigger;
    public readonly List<SelfTestCheck> Checks = new List<SelfTestCheck>();

    public string Overall => Checks.Any(c => c.Status == SelfTest.Fail) ? SelfTest.Fail : Checks.Any(c => c.Status == SelfTest.Warn) ? SelfTest.Warn : SelfTest.Pass;

    public void Add(string _name, string _status, string _detail) => Checks.Add(new SelfTestCheck { Name = _name, Status = _status, Detail = _detail });
}

/// <summary>
/// Automatic self checks (at every game start, once the local player exists, and with "gtb selftest"):
/// patches applied, the toolbelt size agreeing between config, inventory and UI, the hint views, the binding mask, the
/// network clamp (server) and the death backpack capacity. The result goes to the audit log and the game log.
/// </summary>
internal static class SelfTest
{
    public const string Pass = "PASS";
    public const string Warn = "WARN";
    public const string Fail = "FAIL";

    public static SelfTestResult Last { get; private set; }

    public static SelfTestResult Run(string _trigger)
    {
        var result = new SelfTestResult { Trigger = _trigger };
        Guard(result, "patches", CheckPatches);
        Guard(result, "settings", r => r.Add("settings", GamepadToolbeltMod.SettingsWarnings.Count == 0 ? Pass : Warn,
            GamepadToolbeltMod.SettingsWarnings.Count == 0 ? "no warnings" : string.Join(" | ", GamepadToolbeltMod.SettingsWarnings)));
        Guard(result, "offer", CheckOffer);
        Guard(result, "layout", CheckLayout);
        Guard(result, "player", CheckPlayer);
        Guard(result, "controller_type", CheckControllerType);
        Guard(result, "mapping", CheckMapping);
        Guard(result, "mask", CheckMask);
        Guard(result, "input_tick", CheckInputTick);
        Guard(result, "wire_clamp", CheckWireClamp);
        Guard(result, "keyboard", CheckKeyboard);
        Last = result;
        Write(result);
        return result;
    }

    public static IEnumerable<string> Format(SelfTestResult _result)
    {
        yield return $"Selftest ({_result.Trigger}): {_result.Overall}";
        foreach (SelfTestCheck check in _result.Checks)
        {
            yield return $"  {check.Status,-4}  {check.Name,-16} {check.Detail}";
        }
    }

    // ----- checks -------------------------------------------------------------------------------

    private static void CheckPatches(SelfTestResult _result)
    {
        foreach (PatchGroupStatus group in PatchInstaller.Groups)
        {
            string name = "patches." + group.Name;
            if (group.State == "skipped")
            {
                _result.Add(name, Pass, "skipped: " + group.Error);
            }
            else if (!group.Installed)
            {
                _result.Add(name, PatchInstaller.IsOptional(group.Name) ? Warn : Fail, group.State + ": " + group.Error);
            }
            else if (!PatchInstaller.VerifyApplied(group, out string problem))
            {
                _result.Add(name, Fail, problem);
            }
            else
            {
                _result.Add(name, Pass, $"{group.Methods.Count} methods");
            }
        }
    }

    private static void CheckOffer(SelfTestResult _result)
    {
        if (!GameRole.IsServer)
        {
            _result.Add("offer", Pass, "client: follows the server's toolbelt.xml");
            return;
        }
        ToolbeltState.OfferInfo offer = ToolbeltState.Offer;
        switch (offer.State)
        {
            case "offered":
            case "vanilla_setting":
                _result.Add("offer", Pass, $"{offer.State}: {offer.Slots} slots ({offer.Detail})");
                break;
            case "none":
                _result.Add("offer", Warn, "toolbelt.xml was not cached by this server yet");
                break;
            default:
                _result.Add("offer", Warn, $"{offer.State}: {offer.Slots} slots ({offer.Detail})");
                break;
        }
        CheckCachedToolbelt(_result, offer);
    }

    /// <summary>What clients really receive: the toolbelt.xml the server cached for them.</summary>
    private static void CheckCachedToolbelt(SelfTestResult _result, ToolbeltState.OfferInfo _offer)
    {
        WorldStaticData.XmlLoadInfo info = WorldStaticData.getLoadInfoForName("toolbelt", out _);
        if (info?.CompressedXmlData == null)
        {
            _result.Add("offer.cache", Warn, "no cached toolbelt.xml for clients yet");
            return;
        }
        XDocument cached;
        using (var input = new MemoryStream(info.CompressedXmlData))
        using (var inflate = new Noemax.GZip.DeflateInputStream(input))
        {
            cached = XDocument.Load(inflate);
        }
        ToolbeltLayout layout = ToolbeltConfigXml.ParseGameConfig(cached.Root, null, out bool present);
        int expected = _offer.State == "offered" || _offer.State == "foreign" ? _offer.Slots : ToolbeltConfigXml.VanillaSlots;
        bool ok = layout.TotalSlots == expected && (present || expected == ToolbeltConfigXml.VanillaSlots);
        _result.Add("offer.cache", ok ? Pass : Fail, $"clients receive {(present ? "gameconfig " + layout : "no gameconfig (10 slots)")}, expected {expected}");
    }

    private static void CheckLayout(SelfTestResult _result)
    {
        ToolbeltConfig config = ToolbeltConfig.GameConfig;
        int expected = ToolbeltState.Layout.Layout.TotalSlots;
        string detail = $"game config {config.SlotsPerBar}x{config.BarsPerPage}x{config.PageCount}, toolbelt.xml {(ToolbeltState.Layout.Present ? "gameconfig " + ToolbeltState.Layout.Layout : "vanilla")}"
                        + (ToolbeltState.ConfigsPending ? " (server configs not received yet: checked again when the player has spawned)" : string.Empty);
        _result.Add("layout", config.TotalSlots == expected && config.BarsPerPage == 1 && config.PageCount == 1 ? Pass : Fail, detail);
        if (GameRole.IsServer && ToolbeltState.Offer.State == "offered" && config.TotalSlots != ToolbeltState.Offer.Slots)
        {
            _result.Add("layout.offer", Fail, $"offered {ToolbeltState.Offer.Slots} slots but the server uses {config.TotalSlots}");
        }
    }

    private static void CheckPlayer(SelfTestResult _result)
    {
        if (GameManager.IsDedicatedServer)
        {
            _result.Add("player", Pass, "dedicated server: no local player");
            return;
        }
        EntityPlayerLocal player = GameManager.Instance?.World?.GetPrimaryPlayer();
        if (player == null || player.inventory == null)
        {
            _result.Add("player", Warn, "no local player yet (checked again when the player has spawned)");
            return;
        }
        int inventory = player.inventory.Length;
        int config = ToolbeltConfig.GameConfig.TotalSlots;
        _result.Add("inventory", inventory == config ? Pass : Fail, $"toolbelt inventory {inventory} slots, config {config}");

        XUiC_Toolbelt toolbelt = player.PlayerUI?.xui?.GetChildByType<XUiC_Toolbelt>();
        if (toolbelt?.itemControllers == null)
        {
            _result.Add("xui.cells", Warn, "toolbelt UI not built yet");
        }
        else
        {
            int cells = toolbelt.itemControllers.Length;
            _result.Add("xui.cells", cells == inventory ? Pass : Fail, $"{cells} UI cells for {inventory} slots");
            XuiPatchReport windows = ToolbeltState.WindowsReport;
            if (windows != null && windows.GridUsesTemplate)
            {
                XUiC_GtbToolbeltSlot[] slots = toolbelt.itemControllers.OfType<XUiC_GtbToolbeltSlot>().ToArray();
                int resolved = slots.Count(s => s.HintViewsResolved);
                _result.Add("xui.hints", slots.Length == cells && resolved == cells ? Pass : Fail,
                    $"{slots.Length}/{cells} hint controllers, {resolved} with all views");
            }
            else
            {
                _result.Add("xui.hints", Warn, "hints not installed: " + (windows == null ? "XUi patch did not run" : string.Join("; ", windows.Problems.Concat(windows.Skipped))));
            }
            if (windows != null && windows.XpBarWrapped)
            {
                _result.Add("xui.xpbar", XUiC_GtbHintState.LiveInstances > 0 ? Pass : Fail, $"{XUiC_GtbHintState.LiveInstances} XP bar controller(s)");
            }
            if (windows != null && inventory > ToolbeltConfigXml.VanillaSlots && !windows.GeometryPatched)
            {
                _result.Add("xui.geometry", Warn, "toolbelt window not widened: " + string.Join("; ", windows.Problems));
            }
        }

        LootContainer backpack = LootContainer.GetLootContainer("playerBackpack", false);
        if (backpack == null)
        {
            _result.Add("backpack", Warn, "loot container playerBackpack not found");
        }
        else
        {
            int capacity = backpack.size.x * backpack.size.y;
            int needed = player.MaxBagSize + (player.equipment?.GetSlotCount() ?? 13) + inventory;
            _result.Add("backpack", needed <= capacity ? Pass : Warn,
                $"death backpack {capacity} slots for bag {player.MaxBagSize} + equipment {player.equipment?.GetSlotCount() ?? 13} + toolbelt {inventory} = {needed}");
        }
    }

    private static void CheckControllerType(SelfTestResult _result)
    {
        if (GameManager.IsDedicatedServer)
        {
            return;
        }
        Type slot = Type.GetType("GamepadToolbelt.UI.XUiC_GtbToolbeltSlot, GamepadToolbelt");
        Type state = Type.GetType("GamepadToolbelt.UI.XUiC_GtbHintState, GamepadToolbelt");
        _result.Add("controller_type", slot != null && state != null ? Pass : Fail,
            slot != null && state != null ? "XUi resolves the hint controllers" : "Type.GetType cannot resolve the mod's XUi controllers (assembly resolve)");
    }

    private static void CheckMapping(SelfTestResult _result)
    {
        int slots = Math.Max(ToolbeltConfig.GameConfig.TotalSlots, ToolbeltConfigXml.VanillaSlots);
        var wrong = new List<string>();
        for (int slot = SlotMap.FirstSlot; slot <= Math.Min(slots, SlotMap.MaxSlots); slot++)
        {
            SlotChord chord = SlotMap.ChordFor(slot).Value;
            var engine = new ChordEngine();
            var input = new PadInput { DeviceKey = 1, GateOpen = true, Time = 1d, SlotCount = slots, TapMaxSeconds = 0.35d };
            int selected = 0;
            void Step(PadButton? _press, PadButton? _release)
            {
                if (_press.HasValue)
                {
                    input.Pad[_press.Value] = true;
                }
                if (_release.HasValue)
                {
                    input.Pad[_release.Value] = false;
                }
                engine.Tick(input);
                input.Time += 0.016d;
                foreach (ChordEvent e in engine.Events.Where(e => e.Kind == ChordEventKind.Select))
                {
                    selected = e.Slot;
                }
            }
            Step(null, null);
            Step(chord.First, null);
            if (chord.Second.HasValue)
            {
                Step(chord.Second.Value, null);
                Step(null, chord.Second.Value);
            }
            Step(null, chord.First);
            if (selected != slot)
            {
                wrong.Add($"{chord} -> {selected} (expected {slot})");
            }
        }
        _result.Add("mapping", wrong.Count == 0 ? Pass : Fail, wrong.Count == 0 ? $"slots 1-{Math.Min(slots, SlotMap.MaxSlots)}: " + string.Join(" ", SlotMap.Table(slots).Select(p => p.Key + "=" + p.Value)) : string.Join("; ", wrong));
    }

    private sealed class ProbeDevice : InputDevice
    {
        public ProbeDevice() : base("GamepadToolbelt selftest probe")
        {
            AddControl(InputControlType.LeftBumper, "Left Bumper");
        }

        public void Hold() => GetControl(InputControlType.LeftBumper).CommitWithState(true, InputManager.CurrentTick + 1000UL, 0.016f);
    }

    private static void CheckMask(SelfTestResult _result)
    {
        if (!PatchInstaller.IsInstalled(PatchInstaller.InputGroup))
        {
            return;
        }
        var probe = new ProbeDevice();
        probe.Hold();
        var binding = new DeviceBindingSource(InputControlType.LeftBumper);
        float before = binding.GetValue(probe);
        float masked;
        float other;
        try
        {
            PadLayer.SetMaskForProbe(probe, true);
            masked = binding.GetValue(probe);
            other = new DeviceBindingSource(InputControlType.Action1).GetValue(probe);
        }
        finally
        {
            PadLayer.RestoreMask();
        }
        bool ok = before > 0.5f && masked == 0f && other == 0f;
        _result.Add("mask", ok ? Pass : Fail, $"probe LB value {before:0.##}, masked {masked:0.##} (binding postfix {(ok ? "active" : "NOT active")})");
    }

    private static void CheckInputTick(SelfTestResult _result)
    {
        if (!PatchInstaller.IsInstalled(PatchInstaller.InputGroup))
        {
            return;
        }
        double age = UnityEngine.Time.unscaledTime - PadLayer.LastTickTime;
        bool live = PadLayer.TotalTicks > 0 && age < 2d;
        _result.Add("input_tick", live ? Pass : Warn, live ? $"{PadLayer.TotalTicks} ticks, last {age * 1000:0} ms ago, device {(PadLayer.CurrentDevice?.Name ?? "none")}"
            : PadLayer.TotalTicks == 0 ? "no InControl tick seen yet" : $"last InControl tick {age:0.0} s ago");
        if (PadLayer.DisabledByErrors)
        {
            _result.Add("input_errors", Fail, "controller layer switched off after repeated errors");
        }
    }

    /// <summary>
    /// What a server sends to other players about a player on slot 11 (index 10), one probe package per clamp. The probes
    /// are neither counted nor logged as clamps. The item action effect runs through the patched Setup itself; holding
    /// item and player stats need an entity for Setup, so their probes run the postfix directly. Effect and holding item
    /// are also written and read back like on the wire.
    /// </summary>
    private static void CheckWireClamp(SelfTestResult _result)
    {
        if (!GameRole.IsServer || !PatchInstaller.IsInstalled(PatchInstaller.NetGroup))
        {
            return;
        }
        NetPatches.Probing = true;
        try
        {
            Probe(_result, "wire_clamp.effects", () =>
            {
                NetPackageItemActionEffects package = new NetPackageItemActionEffects()
                    .Setup(-1, 10, 0, ItemActionFiringState.Off, UnityEngine.Vector3.zero, UnityEngine.Vector3.zero, 0);
                NetPackageItemActionEffects copy = RoundTrip(package, new NetPackageItemActionEffects());
                return (package.slotIdx == HoldingIndexPolicy.WireMax && copy.slotIdx == HoldingIndexPolicy.WireMax,
                    $"item action effect of slot index 10: Setup gives {package.slotIdx}, the wire {copy.slotIdx}");
            });
            Probe(_result, "wire_clamp.holding", () =>
            {
                var package = new NetPackageHoldingItem { entityId = -1, holdingItemStack = ItemStack.Empty.Clone(), holdingItemIndex = 10 };
                NetPatches.HoldingItemSetupPostfix(package);
                NetPackageHoldingItem copy = RoundTrip(package, new NetPackageHoldingItem());
                return (copy.holdingItemIndex == HoldingIndexPolicy.WireMax, $"holding index 10 sent as {copy.holdingItemIndex}");
            });
            Probe(_result, "wire_clamp.stats", () =>
            {
                var package = new NetPackagePlayerStats();
                package.entityNetworkStats.holdingItemIndex = 10;
                NetPatches.PlayerStatsSetupPostfix(package);
                return (package.entityNetworkStats.holdingItemIndex == HoldingIndexPolicy.WireMax,
                    $"player stats holding index 10 sent as {package.entityNetworkStats.holdingItemIndex}");
            });
        }
        finally
        {
            NetPatches.Probing = false;
        }
    }

    private static void Probe(SelfTestResult _result, string _name, Func<(bool ok, string detail)> _probe)
    {
        try
        {
            (bool ok, string detail) = _probe();
            _result.Add(_name, ok ? Pass : Fail, detail);
        }
        catch (Exception e)
        {
            _result.Add(_name, Warn, "probe not possible here: " + e.GetType().Name + ": " + e.Message);
        }
    }

    /// <summary>Writes a package like the network does and reads it into <paramref name="_copy"/>.</summary>
    private static T RoundTrip<T>(T _package, T _copy) where T : NetPackage
    {
        byte[] bytes;
        using (var stream = new MemoryStream())
        {
            using (PooledBinaryWriter writer = MemoryPools.poolBinaryWriter.AllocSync(_bReset: false))
            {
                writer.SetBaseStream(stream);
                _package.write(writer);
                writer.Flush();
            }
            bytes = stream.ToArray();
        }
        using (var stream = new MemoryStream(bytes))
        {
            stream.Position = 2;   // NetPackage.write starts with the package id (ushort)
            using (PooledBinaryReader reader = MemoryPools.poolBinaryReader.AllocSync(_bReset: false))
            {
                reader.SetBaseStream(stream);
                _copy.read(reader);
            }
        }
        return _copy;
    }

    private static void CheckKeyboard(SelfTestResult _result)
    {
        if (GameManager.IsDedicatedServer)
        {
            return;
        }
        ModSettings settings = GamepadToolbeltMod.Settings;
        if (!settings.HasKeyboardSlot11Key)
        {
            _result.Add("keyboard", Pass, "no key for slot 11 (KeyboardSlot11Key=None)");
            return;
        }
        _result.Add("keyboard", KeyboardSlotKey.BoundKey.HasValue ? Pass : Warn,
            KeyboardSlotKey.BoundKey.HasValue ? "slot 11 key " + KeyboardSlotKey.BoundKey.Value : "slot 11 key '" + settings.KeyboardSlot11Key + "' could not be bound");
    }

    // ----- plumbing -----------------------------------------------------------------------------

    private static void Guard(SelfTestResult _result, string _name, Action<SelfTestResult> _check)
    {
        try
        {
            _check(_result);
        }
        catch (Exception e)
        {
            _result.Add(_name, Fail, "check threw " + e.GetType().Name + ": " + e.Message);
        }
    }

    private static void Write(SelfTestResult _result)
    {
        string summary = string.Join(", ", _result.Checks.Where(c => c.Status != Pass).Select(c => $"{c.Name} {c.Status}"));
        string line = $"Selftest ({_result.Trigger}): {_result.Overall}{(summary.Length > 0 ? " - " + summary : string.Empty)}";
        if (_result.Overall == Pass)
        {
            ModLog.Info(line);
        }
        else
        {
            Log.Warning(ModLog.Prefix + line);
        }
        if (!AuditLog.IsOpen)
        {
            return;
        }
        JsonLineWriter record = AuditLog.Begin(AuditRecords.SelfTest);
        record.Prop("trigger", _result.Trigger).Prop("overall", _result.Overall).Prop("role", GameRole.Current);
        record.BeginArray("checks");
        foreach (SelfTestCheck check in _result.Checks)
        {
            record.BeginObject().Prop("name", check.Name).Prop("status", check.Status).Prop("detail", check.Detail).EndObject();
        }
        record.EndArray();
        AuditLog.Commit(record, _flushSoon: true);
    }
}
