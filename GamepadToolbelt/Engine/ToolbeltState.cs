using System;
using System.Collections.Generic;
using GamepadToolbelt.Core;

namespace GamepadToolbelt.Engine;

/// <summary>
/// What this machine decided about the toolbelt: the server offer, the layout read from toolbelt.xml and the XUi patch
/// results. The configs are loaded before the session starts on a host (and after it on a client), so the results are
/// kept here and written both when they happen (if the audit log is open) and in session_start.
/// </summary>
internal static class ToolbeltState
{
    public sealed class OfferInfo
    {
        public string State = "none";
        public int Slots = ToolbeltConfigXml.VanillaSlots;
        public string Detail;
        public DateTime Utc;
    }

    public sealed class LayoutInfo
    {
        public bool Present;
        public ToolbeltLayout Layout = ToolbeltLayout.Vanilla;
        public readonly List<string> Warnings = new List<string>();
        public DateTime Utc;
        public bool Received;
    }

    public static OfferInfo Offer { get; private set; } = new OfferInfo();
    public static LayoutInfo Layout { get; private set; } = new LayoutInfo();
    public static XuiPatchReport TemplatesReport { get; private set; }
    public static XuiPatchReport WindowsReport { get; private set; }
    public static int XuiSlotCount { get; private set; }

    /// <summary>Highest holding index sent to other players before the clamp (server), for "gtb status".</summary>
    public static int MaxClampedIndex;

    public static void SetOffer(string _state, int _slots, string _detail)
    {
        Offer = new OfferInfo { State = _state, Slots = _slots, Detail = _detail, Utc = DateTime.UtcNow };
        if (AuditLog.Summary)
        {
            JsonLineWriter record = AuditLog.Begin(AuditRecords.Config);
            record.Prop("event", "offer");
            WriteOffer(record);
            AuditLog.Commit(record);
        }
    }

    public static void SetLayout(bool _present, ToolbeltLayout _layout, IEnumerable<string> _warnings)
    {
        var info = new LayoutInfo { Present = _present, Layout = _layout, Utc = DateTime.UtcNow, Received = !GameRole.IsServer };
        info.Warnings.AddRange(_warnings);
        Layout = info;
        if (AuditLog.Summary)
        {
            JsonLineWriter record = AuditLog.Begin(AuditRecords.Config);
            record.Prop("event", "layout");
            WriteLayout(record);
            AuditLog.Commit(record);
        }
    }

    public static void SetXuiReport(bool _templates, XuiPatchReport _report, int _slotCount)
    {
        if (_templates)
        {
            TemplatesReport = _report;
        }
        else
        {
            WindowsReport = _report;
        }
        XuiSlotCount = _slotCount;
        if (AuditLog.Summary)
        {
            JsonLineWriter record = AuditLog.Begin(AuditRecords.XuiPatch);
            record.Prop("file", _templates ? "templates" : "windows").Prop("slots", _slotCount);
            WriteReport(record, _report);
            AuditLog.Commit(record);
        }
    }

    /// <summary>Forgets everything about the previous world (the next world loads its configs and builds its UI again).</summary>
    public static void ResetForWorld()
    {
        ResetConfig();
        TemplatesReport = null;
        WindowsReport = null;
        MaxClampedIndex = -1;
    }

    /// <summary>No offer, vanilla layout: the state before this machine has loaded (or received) a toolbelt.xml.</summary>
    public static void ResetConfig()
    {
        Offer = new OfferInfo();
        Layout = new LayoutInfo();
    }

    /// <summary>A client whose server configs have not been loaded yet (its toolbelt values are still the defaults).</summary>
    public static bool ConfigsPending => !GameRole.IsServer && !WorldStaticData.AllConfigsReceivedAndLoaded();

    // ----- JSON ---------------------------------------------------------------------------------

    public static void WriteOffer(JsonLineWriter _record)
    {
        _record.BeginObject("offer")
            .Prop("state", Offer.State)
            .Prop("slots", Offer.Slots)
            .Prop("detail", Offer.Detail)
            .EndObject();
    }

    public static void WriteLayout(JsonLineWriter _record)
    {
        _record.BeginObject("layout")
            .Prop("gameconfig", Layout.Present)
            .Prop("slots_per_bar", Layout.Layout.SlotsPerBar)
            .Prop("bars", Layout.Layout.BarsPerPage)
            .Prop("pages", Layout.Layout.PageCount)
            .Prop("received", Layout.Received)
            .PropStrings("warnings", Layout.Warnings)
            .EndObject();
    }

    public static void WriteXui(JsonLineWriter _record)
    {
        _record.BeginObject("xui").Prop("slots", XuiSlotCount);
        _record.BeginObject("templates");
        WriteReport(_record, TemplatesReport);
        _record.EndObject();
        _record.BeginObject("windows");
        WriteReport(_record, WindowsReport);
        _record.EndObject();
        _record.EndObject();
    }

    private static void WriteReport(JsonLineWriter _record, XuiPatchReport _report)
    {
        if (_report == null)
        {
            _record.Prop("ran", false);
            return;
        }
        _record.Prop("ran", true)
            .Prop("ok", _report.Ok)
            .Prop("template", _report.TemplateInstalled)
            .Prop("grid", _report.GridUsesTemplate)
            .Prop("geometry", _report.GeometryPatched)
            .Prop("xp_bar", _report.XpBarWrapped)
            .PropStrings("applied", _report.Applied)
            .PropStrings("skipped", _report.Skipped)
            .PropStrings("problems", _report.Problems);
    }
}

/// <summary>Which part this machine plays in the current game.</summary>
internal static class GameRole
{
    public static bool IsServer => SingletonMonoBehaviour<ConnectionManager>.Instance != null && SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer;

    public static bool IsDedicated => GameManager.IsDedicatedServer;

    /// <summary>"dedicated", "singleplayer", "host" or "client".</summary>
    public static string Current
    {
        get
        {
            if (GameManager.IsDedicatedServer)
            {
                return "dedicated";
            }
            ConnectionManager connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
            if (connection == null)
            {
                return "unknown";
            }
            if (!connection.IsServer)
            {
                return "client";
            }
            return connection.CurrentMode == ProtocolManager.NetworkType.OfflineServer ? "singleplayer" : "host";
        }
    }
}

/// <summary>Runtime invariant violations: counted, written to the audit log, reported in the game log (rate limited).</summary>
internal static class Invariants
{
    private const int MaxLogPerCode = 5;
    private static readonly Dictionary<string, int> logged = new Dictionary<string, int>(StringComparer.Ordinal);

    public static void Reset() => logged.Clear();

    public static void Report(string _code, string _message, Action<JsonLineWriter> _details = null)
    {
        RuntimeStats.InvariantViolations++;
        logged.TryGetValue(_code, out int count);
        logged[_code] = count + 1;
        if (count < MaxLogPerCode)
        {
            Log.Warning(ModLog.Prefix + "Invariant '" + _code + "': " + _message + (count + 1 == MaxLogPerCode ? " (further reports of this kind only in the audit log)" : string.Empty));
        }
        if (AuditLog.IsOpen)
        {
            JsonLineWriter record = AuditLog.Begin(AuditRecords.Invariant);
            record.Prop("code", _code).Prop("message", _message);
            _details?.Invoke(record);
            AuditLog.Commit(record, _flushSoon: true);
        }
    }
}
