using GamepadToolbelt.Core;
using GamepadToolbelt.Engine;
using UnityEngine;

namespace GamepadToolbelt.Input;

/// <summary>
/// Applies slot selections in PlayerMoveController.Update (postfix), exactly the way vanilla applies a number key:
/// the HUD focus moves at once (<c>SetFocusedItemIdx</c>), the held item follows through vanilla's swap (holster
/// animation, network sync) or, while the hand cannot switch, through vanilla's deferred queue. The newest request
/// wins; while inventory actions are being cancelled (reload, holster) a request waits instead of being dropped. Every
/// request ends with one <c>select_result</c> record: reached (with latency), same_slot, superseded, gate_closed,
/// timeout, out_of_range or not_reached.
/// </summary>
internal static class SelectionApplier
{
    public const double CancelWaitTimeoutSeconds = 1.5;
    public const double ReachTimeoutSeconds = 3.0;

    private sealed class PendingSelection
    {
        public long Id;
        public int Slot;
        public ChordGesture Gesture;
        public PadSide Side;
        public PadButton Button;
        public double RequestTime;
        public double AppliedTime = -1;
        public string Path = "pending";
        public int FromIndex = -1;
    }

    private static PendingSelection pending;
    private static PendingSelection tracking;
    private static long nextId;

    public static bool HasPending => pending != null || tracking != null;

    /// <summary>Queues a selection (slot 1-based). Returns the request id written to the chord record.</summary>
    public static long Request(int _slot, ChordGesture _gesture, PadSide _side, PadButton _button, double _now)
    {
        if (pending != null)
        {
            Finish(pending, "superseded", _now);
        }
        if (tracking != null)
        {
            Finish(tracking, "superseded", _now);
            tracking = null;
        }
        pending = new PendingSelection { Id = ++nextId, Slot = _slot, Gesture = _gesture, Side = _side, Button = _button, RequestTime = _now };
        return pending.Id;
    }

    public static void Reset(string _reason)
    {
        double now = Time.unscaledTime;
        if (pending != null)
        {
            Finish(pending, "reset:" + _reason, now);
        }
        if (tracking != null)
        {
            Finish(tracking, "reset:" + _reason, now);
        }
        pending = null;
        tracking = null;
    }

    /// <summary>Called by the PlayerMoveController.Update postfix of the local player.</summary>
    public static void OnPlayerUpdate(PlayerMoveController _controller, EntityPlayerLocal _player, ModSettings _settings)
    {
        double now = Time.unscaledTime;
        Inventory inventory = _player.inventory;
        if (inventory == null)
        {
            return;
        }
        TrackReach(inventory, now);
        if (pending == null)
        {
            return;
        }

        GateResult gate = ToolbeltGate.Evaluate(_settings, pending.Gesture == ChordGesture.Keyboard);
        if (!gate.Open)
        {
            Finish(pending, "gate_closed", now, gate.Reason);
            pending = null;
            return;
        }
        int index = pending.Slot - 1;
        if (index < 0 || index >= inventory.Length)
        {
            Finish(pending, "out_of_range", now, "inventory length " + inventory.Length);
            pending = null;
            return;
        }
        if (_player.CancellingInventoryActions)
        {
            if (now - pending.RequestTime > CancelWaitTimeoutSeconds)
            {
                Finish(pending, "timeout", now, "inventory actions still cancelling");
                pending = null;
            }
            return;
        }

        pending.FromIndex = inventory.holdingItemIdx;
        pending.AppliedTime = now;
        if (index == inventory.GetFocusedItemIdx())
        {
            // Vanilla ignores a slot key for the focused slot as well; a swap towards it may still be running.
            pending.Path = "same_slot";
            if (inventory.holdingItemIdx == index)
            {
                Finish(pending, "same_slot", now);
            }
            else
            {
                tracking = pending;
            }
            pending = null;
            return;
        }

        if (_controller.canSwapHeldItem())
        {
            _controller.swapItem(inventory.SetFocusedItemIdx(index));
            pending.Path = "swap";
        }
        else
        {
            inventory.SetFocusedItemIdx(index);
            _controller.nextHeldItem.Add(index);
            pending.Path = "deferred";
        }
        tracking = pending;
        pending = null;
        TrackReach(inventory, now);
    }

    private static void TrackReach(Inventory _inventory, double _now)
    {
        if (tracking == null)
        {
            return;
        }
        int index = tracking.Slot - 1;
        if (_inventory.holdingItemIdx == index)
        {
            Finish(tracking, "reached", _now);
            tracking = null;
        }
        else if (_inventory.GetFocusedItemIdx() != index)
        {
            // Something else (mouse wheel, number key, a game event) moved the focus away before the swap finished.
            Finish(tracking, "superseded", _now, "focus moved to slot " + (_inventory.GetFocusedItemIdx() + 1));
            tracking = null;
        }
        else if (_now - tracking.RequestTime > ReachTimeoutSeconds)
        {
            PendingSelection failed = tracking;
            tracking = null;
            Finish(failed, "not_reached", _now, "holding slot " + (_inventory.holdingItemIdx + 1));
            Invariants.Report("slot_not_reached", $"slot {failed.Slot} was selected but not reached within {ReachTimeoutSeconds:0.#} s",
                r => r.Prop("slot", failed.Slot).Prop("holding", _inventory.holdingItemIdx + 1).Prop("path", failed.Path));
        }
    }

    private static void Finish(PendingSelection _request, string _outcome, double _now, string _detail = null)
    {
        switch (_outcome)
        {
            case "reached":
                RuntimeStats.CountReached((_now - _request.RequestTime) * 1000d);
                break;
            case "same_slot":
                RuntimeStats.SameSlot++;
                break;
            case "superseded":
                RuntimeStats.Superseded++;
                break;
            case "gate_closed":
                RuntimeStats.DroppedGateClosed++;
                break;
            case "timeout":
                RuntimeStats.Timeouts++;
                break;
            case "not_reached":
                RuntimeStats.NotReached++;
                break;
        }
        if (!AuditLog.Summary)
        {
            return;
        }
        JsonLineWriter record = AuditLog.Begin(AuditRecords.SelectResult);
        record.Prop("id", _request.Id)
            .Prop("slot", _request.Slot)
            .Prop("gesture", AuditRecords.GestureCode(_request.Gesture))
            .Prop("outcome", _outcome)
            .Prop("path", _request.Path)
            .Prop("from", _request.FromIndex + 1)
            .Prop("latency_ms", AuditRecords.Ms(_now - _request.RequestTime))
            .Prop("apply_ms", _request.AppliedTime < 0 ? -1d : AuditRecords.Ms(_request.AppliedTime - _request.RequestTime));
        if (_detail != null)
        {
            record.Prop("detail", _detail);
        }
        AuditLog.Commit(record);
    }
}
