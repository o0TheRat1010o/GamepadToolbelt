using System;
using GamepadToolbelt.Core;
using GamepadToolbelt.Engine;
using GamepadToolbelt.Input;
using InControl;
using UnityEngine;

namespace GamepadToolbelt.Patches;

/// <summary>Group "Input": the InControl tick, the binding mask and the selection in PlayerMoveController.</summary>
internal static class InputPatches
{
    private static int updateErrors;

    /// <summary>Prefix of InputManager.UpdatePlayerActionSets: devices are committed, action sets not yet updated.</summary>
    public static void UpdatePlayerActionSetsPrefix()
    {
        PadLayer.OnInputTick();
    }

    /// <summary>Owned controls of the owned device read as released for every PlayerAction binding.</summary>
    public static void GetValuePostfix(DeviceBindingSource __instance, InputDevice inputDevice, ref float __result)
    {
        if (__result != 0f && PadLayer.MaskDevice != null && ReferenceEquals(inputDevice, PadLayer.MaskDevice) && PadLayer.IsMasked(__instance.Control))
        {
            __result = 0f;
        }
    }

    public static void GetStatePostfix(DeviceBindingSource __instance, InputDevice inputDevice, ref bool __result)
    {
        if (__result && PadLayer.MaskDevice != null && ReferenceEquals(inputDevice, PadLayer.MaskDevice) && PadLayer.IsMasked(__instance.Control))
        {
            __result = false;
        }
    }

    /// <summary>
    /// Postfix of PlayerMoveController.Update (it runs after every early return of the original as well): the slot 11
    /// key, pending selections and the mask invariant, for the local player only.
    /// </summary>
    public static void PlayerMoveControllerUpdatePostfix(PlayerMoveController __instance)
    {
        EntityPlayerLocal player = __instance?.entityPlayerLocal;
        if (player == null || GameManager.Instance?.World == null || !ReferenceEquals(GameManager.Instance.World.GetPrimaryPlayer(), player))
        {
            return;
        }
        try
        {
            ModSettings settings = GamepadToolbeltMod.Settings;
            if (KeyboardSlotKey.WasPressed(player))
            {
                OnSlotKey(player, settings);
            }
            SelectionApplier.OnPlayerUpdate(__instance, player, settings);
            CheckMask(player);
        }
        catch (Exception e)
        {
            updateErrors++;
            if (updateErrors <= 3)
            {
                ModLog.Error("Applying a toolbelt selection failed.", e, "select_apply");
            }
        }
    }

    private static void OnSlotKey(EntityPlayerLocal _player, ModSettings _settings)
    {
        GateResult gate = ToolbeltGate.Evaluate(_settings, _forKeyboard: true);
        if (!gate.Open)
        {
            return;
        }
        int slots = _player.inventory?.Length ?? 0;
        double now = Time.unscaledTime;
        if (slots < SlotMap.ExtraSlot)
        {
            RuntimeStats.Unavailable++;
            if (AuditLog.Summary)
            {
                JsonLineWriter record = AuditLog.Begin(AuditRecords.Unavailable);
                record.Prop("slot", SlotMap.ExtraSlot).Prop("gesture", "key").Prop("reason", "slot_count_" + slots).Prop("key", KeyboardSlotKey.BoundKey?.ToString());
                AuditLog.Commit(record);
            }
            return;
        }
        RuntimeStats.CountChord(SlotMap.ExtraSlot, true);
        long id = SelectionApplier.Request(SlotMap.ExtraSlot, ChordGesture.Keyboard, PadSide.None, PadButton.RightBumper, now);
        if (AuditLog.Summary)
        {
            JsonLineWriter record = AuditLog.Begin(AuditRecords.Chord);
            record.Prop("id", id)
                .Prop("slot", SlotMap.ExtraSlot)
                .Prop("gesture", "key")
                .Prop("key", KeyboardSlotKey.BoundKey?.ToString())
                .Prop("slots", slots)
                .Prop("gate", true)
                .Prop("haptic", false);
            AuditLog.Commit(record);
        }
    }

    /// <summary>An owned bumper or A must never reach the game from the controller.</summary>
    private static void CheckMask(EntityPlayerLocal _player)
    {
        ChordEngine engine = PadLayer.Engine;
        if (!engine.AnyOwned)
        {
            return;
        }
        PlayerActionsLocal actions = _player.playerInput;
        if (actions == null)
        {
            return;
        }
        if (engine.IsOwned(PadButton.LeftBumper) && LeakedThroughMask(actions.InventorySlotLeft))
        {
            Invariants.Report("mask_leak", "InventorySlotLeft was pressed from the controller while LB is owned", r => r.Prop("action", "InventorySlotLeft"));
        }
        if (engine.IsOwned(PadButton.RightBumper) && LeakedThroughMask(actions.InventorySlotRight))
        {
            Invariants.Report("mask_leak", "InventorySlotRight was pressed from the controller while RB is owned", r => r.Prop("action", "InventorySlotRight"));
        }
        if (engine.IsOwned(PadButton.A) && LeakedThroughMask(actions.Jump))
        {
            Invariants.Report("mask_leak", "Jump was pressed from the controller while A is owned", r => r.Prop("action", "Jump"));
        }
    }

    /// <summary>Pressed from the controller although no unmasked binding of the action is active: only a masked control can be the source.</summary>
    private static bool LeakedThroughMask(PlayerAction _action)
    {
        return _action != null && _action.WasPressed && _action.LastInputType == BindingSourceType.DeviceBindingSource
               && !PadLayer.ActiveThroughUnmaskedControl(_action);
    }
}
