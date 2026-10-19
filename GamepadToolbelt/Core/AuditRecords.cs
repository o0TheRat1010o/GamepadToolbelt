using System;
using System.Globalization;

namespace GamepadToolbelt.Core;

/// <summary>
/// Audit log schema (JSON lines). Every line carries the common header written by <see cref="BeginRecord"/>:
/// <c>v</c> schema version, <c>seq</c> sequence number, <c>t</c> UTC timestamp, <c>session</c> id, <c>type</c>,
/// <c>day</c>/<c>time</c> game clock (null outside a world). The fields of each record type are written by the producers
/// (PadLayer, SelectionApplier, NetPatches, ToolbeltState, SelfTest, the mod entry point).
/// </summary>
public static class AuditRecords
{
    public const int SchemaVersion = 1;

    public const string SessionStart = "session_start";
    public const string SessionEnd = "session_end";
    public const string SelfTest = "selftest";

    /// <summary>The toolbelt configuration this machine uses (server offer, parsed layout, inventory size).</summary>
    public const string Config = "config";

    /// <summary>Result of the client-local XUi patch (templates and windows).</summary>
    public const string XuiPatch = "xui_patch";

    /// <summary>A chord (or the keyboard key) selected a slot.</summary>
    public const string Chord = "chord";

    /// <summary>Outcome of a selection request: reached, same_slot, superseded, gate_closed, timeout, not_reached.</summary>
    public const string SelectResult = "select_result";

    public const string TapCancel = "tap_cancel";
    public const string Unavailable = "unavailable";

    /// <summary>Toolbelt-active transitions with the reason (Detailed).</summary>
    public const string Gate = "gate";

    /// <summary>Buttons taken from or given back to the game (Detailed).</summary>
    public const string Mask = "mask";

    public const string Device = "device";

    /// <summary>The server clamped a holding index for other players (10 -> 9).</summary>
    public const string NetClamp = "net_clamp";

    /// <summary>A receiver clamped or ignored a holding index outside its toolbelt.</summary>
    public const string NetGuard = "net_guard";

    public const string Invariant = "invariant";
    public const string Screenshot = "screenshot";
    public const string Stats = "stats";
    public const string Reload = "reload";
    public const string LogLevelChanged = "loglevel";
    public const string Error = "error";
    public const string Warning = "warning";

    /// <summary>
    /// Records that are written even when a session reached its size cap (from a small reserve): the session frame and
    /// the problems, so an analysis still sees how the session ended and how many records were dropped.
    /// </summary>
    public static bool IsEssential(string _type)
    {
        return _type == SessionStart || _type == SessionEnd || _type == Error || _type == Invariant || _type == SelfTest;
    }

    /// <summary>Starts a record with the common header; the caller adds its fields and calls Finish().</summary>
    public static JsonLineWriter BeginRecord(string _type, long _seq, DateTime _utc, string _session, int? _gameDay, string _gameTime)
    {
        var writer = new JsonLineWriter { Sequence = _seq, RecordType = _type };
        writer.BeginObject()
            .Prop("v", SchemaVersion)
            .Prop("seq", _seq)
            .Prop("t", FormatUtc(_utc))
            .Prop("session", _session)
            .Prop("type", _type);
        if (_gameDay.HasValue)
        {
            writer.Prop("day", _gameDay.Value);
        }
        else
        {
            writer.PropNull("day");
        }
        writer.Prop("time", _gameTime);
        return writer;
    }

    public static string FormatUtc(DateTime _utc)
    {
        return _utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>Formats a game clock value as HH:MM.</summary>
    public static string FormatGameTime(int _hours, int _minutes)
    {
        return _hours.ToString("00", CultureInfo.InvariantCulture) + ":" + _minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>Milliseconds rounded to 0.1 ms for latency fields.</summary>
    public static double Ms(double _seconds) => Math.Round(_seconds * 10000d) / 10d;

    public static string GestureCode(ChordGesture _gesture)
    {
        switch (_gesture)
        {
            case ChordGesture.Tap: return "tap";
            case ChordGesture.Face: return "face";
            case ChordGesture.BothBumpers: return "both";
            default: return "key";
        }
    }
}
