using System;
using System.Collections.Generic;
using System.Linq;
using GamepadToolbelt.Core;

namespace GamepadToolbelt.Engine;

/// <summary>Session counters for "gtb status", the periodic "stats" record and "session_end". Main thread only.</summary>
internal static class RuntimeStats
{
    public static DateTime SessionStartUtc = DateTime.UtcNow;

    public static long InputTicks;
    public static long DuplicateTicks;
    public static long Chords;
    public static readonly long[] ChordsPerSlot = new long[SlotMap.MaxSlots + 1];
    public static long KeyboardSelects;
    public static long TapCancels;
    public static long Unavailable;
    public static long Reached;
    public static long SameSlot;
    public static long Superseded;
    public static long DroppedGateClosed;
    public static long Timeouts;
    public static long NotReached;
    public static double LatencyMsTotal;
    public static double LatencyMsMax;
    public static long HapticPulses;
    public static long GateTransitions;
    public static readonly Dictionary<string, long> GateClosedReasons = new Dictionary<string, long>(StringComparer.Ordinal);
    public static long DeviceResets;
    public static long NetClamps;
    public static long NetGuards;
    public static long InvariantViolations;
    public static long LayerErrors;
    public static long Screenshots;

    public static void Reset()
    {
        SessionStartUtc = DateTime.UtcNow;
        InputTicks = DuplicateTicks = Chords = KeyboardSelects = TapCancels = Unavailable = 0;
        Array.Clear(ChordsPerSlot, 0, ChordsPerSlot.Length);
        Reached = SameSlot = Superseded = DroppedGateClosed = Timeouts = NotReached = 0;
        LatencyMsTotal = LatencyMsMax = 0;
        HapticPulses = GateTransitions = DeviceResets = NetClamps = NetGuards = InvariantViolations = LayerErrors = Screenshots = 0;
        GateClosedReasons.Clear();
    }

    public static void CountChord(int _slot, bool _keyboard)
    {
        if (_keyboard)
        {
            KeyboardSelects++;
        }
        else
        {
            Chords++;
        }
        if (_slot >= 0 && _slot < ChordsPerSlot.Length)
        {
            ChordsPerSlot[_slot]++;
        }
    }

    public static void CountReached(double _latencyMs)
    {
        Reached++;
        LatencyMsTotal += _latencyMs;
        LatencyMsMax = Math.Max(LatencyMsMax, _latencyMs);
    }

    public static void CountGateClosed(string _reason)
    {
        GateTransitions++;
        GateClosedReasons.TryGetValue(_reason, out long count);
        GateClosedReasons[_reason] = count + 1;
    }

    public static double LatencyMsAverage => Reached == 0 ? 0d : LatencyMsTotal / Reached;

    /// <summary>Writes all counters (stats and session_end records).</summary>
    public static void Write(JsonLineWriter _record)
    {
        _record.Prop("uptime_s", Math.Round((DateTime.UtcNow - SessionStartUtc).TotalSeconds, 1));
        _record.BeginObject("input")
            .Prop("ticks", InputTicks)
            .Prop("duplicate_ticks", DuplicateTicks)
            .Prop("chords", Chords)
            .Prop("keyboard", KeyboardSelects)
            .Prop("tap_cancels", TapCancels)
            .Prop("unavailable", Unavailable)
            .Prop("device_resets", DeviceResets)
            .Prop("haptic_pulses", HapticPulses)
            .Prop("errors", LayerErrors)
            .PropInts("per_slot", ChordsPerSlot.Skip(1).Select(c => (int)Math.Min(int.MaxValue, c)))
            .EndObject();
        _record.BeginObject("selection")
            .Prop("reached", Reached)
            .Prop("same_slot", SameSlot)
            .Prop("superseded", Superseded)
            .Prop("gate_closed", DroppedGateClosed)
            .Prop("timeout", Timeouts)
            .Prop("not_reached", NotReached)
            .Prop("latency_ms_avg", Math.Round(LatencyMsAverage, 1))
            .Prop("latency_ms_max", Math.Round(LatencyMsMax, 1))
            .EndObject();
        _record.BeginObject("gate").Prop("transitions", GateTransitions);
        _record.BeginObject("closed_reasons");
        foreach (KeyValuePair<string, long> pair in GateClosedReasons.OrderByDescending(p => p.Value))
        {
            _record.Prop(pair.Key, pair.Value);
        }
        _record.EndObject().EndObject();
        _record.BeginObject("net").Prop("clamps", NetClamps).Prop("guards", NetGuards).EndObject();
        _record.Prop("invariant_violations", InvariantViolations);
        _record.Prop("screenshots", Screenshots);
        _record.Prop("warnings", ModLog.WarningCount);
        _record.Prop("errors", ModLog.ErrorCount);
        _record.Prop("audit_dropped", AuditLog.Dropped);
    }
}
