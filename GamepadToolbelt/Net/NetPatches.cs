using System;
using System.Collections.Generic;
using System.Xml.Linq;
using GamepadToolbelt.Core;
using GamepadToolbelt.Engine;
using GamepadToolbelt.Patches;
using UnityEngine;

namespace GamepadToolbelt.Net;

/// <summary>
/// Group "Net": the server offer of the 11th slot and the toolbelt-index safety.
/// <list type="bullet">
/// <item>Offer: the server adds a gameconfig to toolbelt.xml right before the configs are cached for the clients and
/// parsed by the server itself (after all XML patches and host conditionals).</item>
/// <item>Clamp: a server never sends a toolbelt index beyond 9 to other players (they may be vanilla): holding item,
/// player stats and item action effects (shots, throws, trigger releases).</item>
/// <item>Guards: a receiver clamps an index outside its toolbelt instead of throwing (which would drop the rest of the
/// package batch), and the local player never applies an extra-slot index received from the network.</item>
/// </list>
/// </summary>
internal static class NetPatches
{
    public const double ClampLogIntervalSeconds = 10d;

    private static readonly Dictionary<string, double> lastClampLog = new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>While set, clamps are applied but neither counted nor logged (the selftest's own probe packages).</summary>
    internal static bool Probing;

    public static void ResetSession() => lastClampLog.Clear();

    // ----- offer --------------------------------------------------------------------------------

    public static void CachePatchedXmlPrefix(WorldStaticData.XmlLoadInfo _loadInfo, XmlFile _xmlFile)
    {
        if (_loadInfo == null || _xmlFile?.XmlDoc == null || !string.Equals(_loadInfo.XmlName, "toolbelt", StringComparison.OrdinalIgnoreCase) || !GameRole.IsServer)
        {
            return;
        }
        Offer(_xmlFile.XmlDoc);
    }

    /// <summary>Decides and writes the server offer into the toolbelt.xml that is cached for clients and loaded locally.</summary>
    public static void Offer(XDocument _toolbelt)
    {
        ModSettings settings = GamepadToolbeltMod.Settings;
        try
        {
            if (settings.ToolbeltSlots <= ToolbeltConfigXml.VanillaSlots)
            {
                ToolbeltState.SetOffer("vanilla_setting", ToolbeltConfigXml.VanillaSlots, "ToolbeltSlots=" + settings.ToolbeltSlots);
                return;
            }
            if (!PatchInstaller.ExtraSlotAllowed)
            {
                string detail = $"Toolbelt={PatchInstaller.Find(PatchInstaller.ToolbeltGroup)?.State}, Net={PatchInstaller.Find(PatchInstaller.NetGroup)?.State}";
                ToolbeltState.SetOffer("interlock", ToolbeltConfigXml.VanillaSlots, detail);
                ModLog.Warning("The 11th toolbelt slot is not offered because a required patch group is missing (" + detail + ").", "offer_interlock");
                return;
            }
            ToolbeltConfigXml.InjectResult result = ToolbeltConfigXml.InjectGameConfig(_toolbelt, settings.ToolbeltSlots);
            switch (result)
            {
                case ToolbeltConfigXml.InjectResult.Injected:
                case ToolbeltConfigXml.InjectResult.AlreadyPresent:
                    ToolbeltState.SetOffer("offered", settings.ToolbeltSlots, result.ToString());
                    ModLog.Info($"Offering {settings.ToolbeltSlots} toolbelt slots to this game (players with the mod follow, players without keep 10).");
                    break;
                case ToolbeltConfigXml.InjectResult.Foreign:
                    ToolbeltLayout foreign = ToolbeltConfigXml.ParseGameConfig(_toolbelt.Root, null, out _);
                    ToolbeltState.SetOffer("foreign", foreign.TotalSlots, "toolbelt.xml already has a gameconfig (another mod?)");
                    ModLog.Warning($"toolbelt.xml already has a gameconfig from another mod ({foreign}); it is used as it is.", "offer_foreign");
                    break;
                default:
                    ToolbeltState.SetOffer("no_root", ToolbeltConfigXml.VanillaSlots, "toolbelt.xml has no root element");
                    ModLog.Warning("toolbelt.xml has no root element; the toolbelt keeps 10 slots.", "offer_failed");
                    break;
            }
        }
        catch (Exception e)
        {
            ToolbeltState.SetOffer("failed", ToolbeltConfigXml.VanillaSlots, e.GetType().Name + ": " + e.Message);
            ModLog.Error("Offering the 11th toolbelt slot failed; the toolbelt keeps 10 slots.", e, "offer_failed");
        }
    }

    // ----- clamp (server, outgoing) ---------------------------------------------------------------
    //
    // A server builds every one of these packages with Setup on the main thread - its own player's updates as well as
    // the relays of what a client sent - and never sends them to the player they describe. Clamping right after Setup
    // keeps all of this on the main thread (write runs once per connection on network threads) and also covers packages
    // that end up with no recipient.

    public static void HoldingItemSetupPostfix(NetPackageHoldingItem __instance)
    {
        byte index = __instance.holdingItemIndex;
        if (!HoldingIndexPolicy.NeedsWireClamp(index) || !GameRole.IsServer)
        {
            return;
        }
        __instance.holdingItemIndex = HoldingIndexPolicy.ForWire(index);
        OnClamp("NetPackageHoldingItem", __instance.entityId, index, __instance.holdingItemIndex);
    }

    /// <summary>The package owns its stats object (filled from the entity in Setup), so the entity stays untouched.</summary>
    public static void PlayerStatsSetupPostfix(NetPackagePlayerStats __instance)
    {
        EntityAlive.EntityNetworkStats stats = __instance.entityNetworkStats;
        if (stats == null || !HoldingIndexPolicy.NeedsWireClamp(stats.holdingItemIndex) || !GameRole.IsServer)
        {
            return;
        }
        byte index = stats.holdingItemIndex;
        stats.holdingItemIndex = HoldingIndexPolicy.ForWire(index);
        OnClamp("NetPackagePlayerStats", __instance.entityId, index, stats.holdingItemIndex);
    }

    /// <summary>
    /// Shots, throws and trigger releases name the toolbelt slot of the item. Other players look it up in their copy of
    /// the shooter's toolbelt, which holds a slot 11 item at index 9 - and a vanilla peer would throw on index 10.
    /// </summary>
    public static void ItemActionEffectsSetupPostfix(NetPackageItemActionEffects __instance)
    {
        int slot = __instance.slotIdx;
        int wire = HoldingIndexPolicy.EffectSlotForWire(slot);
        if (wire == slot || !GameRole.IsServer)
        {
            return;
        }
        __instance.slotIdx = (sbyte)wire;
        OnClamp("NetPackageItemActionEffects", __instance.entityId, slot, wire);
    }

    private static void OnClamp(string _package, int _entityId, int _from, int _to)
    {
        if (Probing)
        {
            return;
        }
        RuntimeStats.NetClamps++;
        ToolbeltState.MaxClampedIndex = Math.Max(ToolbeltState.MaxClampedIndex, _from);
        if (!AuditLog.Summary)
        {
            return;
        }
        string key = _package + ":" + _entityId;
        double now = Time.unscaledTime;
        if (!AuditLog.Detailed && lastClampLog.TryGetValue(key, out double last) && now - last < ClampLogIntervalSeconds)
        {
            return;
        }
        lastClampLog[key] = now;
        JsonLineWriter record = AuditLog.Begin(AuditRecords.NetClamp);
        record.Prop("package", _package).Prop("entity", _entityId).Prop("from", _from).Prop("to", _to).Prop("server", GameRole.IsServer);
        AuditLog.Commit(record);
    }

    // ----- guards (any side, incoming) --------------------------------------------------------------

    public static void ToEntityPrefix(EntityAlive.EntityNetworkStats __instance, EntityAlive _entity)
    {
        Inventory inventory = _entity?.inventory;
        if (inventory == null)
        {
            return;
        }
        byte index = __instance.holdingItemIndex;
        if (_entity is EntityPlayerLocal && HoldingIndexPolicy.LocalPlayerIgnores(index))
        {
            // Keep the local selection: index and stack become the current ones, so ToEntity changes nothing there.
            __instance.holdingItemIndex = (byte)inventory.holdingItemIdx;
            __instance.holdingItemStack = inventory.GetItem(inventory.holdingItemIdx);
            OnGuard("stats", _entity.entityId, index, "local_ignored", inventory.Length);
        }
        else if (index >= inventory.Length)
        {
            __instance.holdingItemIndex = HoldingIndexPolicy.ForReceiver(index, inventory.Length);
            OnGuard("stats", _entity.entityId, index, "clamped", inventory.Length);
        }
    }

    public static void UpdateNetworkStatsPrefix(EntityAlive __instance)
    {
        List<EntityAlive.NetworkStatChange> queue = __instance.networkStatsUpdateQueue;
        if (queue == null || queue.Count == 0)
        {
            return;
        }
        EntityAlive.EntityNetworkHoldingData data = queue[0]?.m_HoldingData;
        Inventory inventory = __instance.inventory;
        if (data == null || inventory == null)
        {
            return;
        }
        byte index = data.m_HoldingItemIndex;
        if (__instance is EntityPlayerLocal && HoldingIndexPolicy.LocalPlayerIgnores(index))
        {
            data.m_HoldingItemIndex = (byte)inventory.holdingItemIdx;
            data.m_HoldingItemStack = inventory.GetItem(inventory.holdingItemIdx);
            OnGuard("holding", __instance.entityId, index, "local_ignored", inventory.Length);
        }
        else if (index >= inventory.Length)
        {
            data.m_HoldingItemIndex = HoldingIndexPolicy.ForReceiver(index, inventory.Length);
            OnGuard("holding", __instance.entityId, index, "clamped", inventory.Length);
        }
    }

    /// <summary>
    /// Prefix of GameManager.ItemActionEffectsClient: an effect slot outside the entity's toolbelt copy (an unclamped
    /// server) becomes -1, the item the entity holds, instead of an IndexOutOfRangeException in Hand.GetSlotData.
    /// </summary>
    public static void ItemActionEffectsClientPrefix(int _entityId, ref int _slotIdx)
    {
        if (_slotIdx < 0)
        {
            return;
        }
        ItemInventoryData[] slots = (GameManager.Instance?.World?.GetEntity(_entityId) as EntityAlive)?.Hand?.slotDatas;
        if (slots == null || _slotIdx < slots.Length)
        {
            return;
        }
        int index = _slotIdx;
        _slotIdx = HoldingIndexPolicy.EffectSlotForReceiver(index, slots.Length);
        OnGuard("effects", _entityId, index, "held_item", slots.Length);
    }

    private static void OnGuard(string _source, int _entityId, int _index, string _action, int _length)
    {
        RuntimeStats.NetGuards++;
        Invariants.Report("net_guard", $"toolbelt index {_index} received for entity {_entityId} ({_source}) was {_action} (toolbelt has {_length} slots)",
            r => r.Prop("source", _source).Prop("entity", _entityId).Prop("index", _index).Prop("action", _action).Prop("length", _length));
    }
}
