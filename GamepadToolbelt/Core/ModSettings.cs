using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace GamepadToolbelt.Core;

/// <summary>Verbosity of the JSON-lines audit log.</summary>
public enum AuditLevel
{
    Off = 0,
    Summary = 1,
    Detailed = 2
}

/// <summary>When the toolbelt hints are shown (always only in controller mode).</summary>
public enum HintMode
{
    Always = 0,
    WhileHeld = 1,
    Off = 2
}

/// <summary>
/// Immutable mod configuration. Every setting is described once in <see cref="Descriptors"/>; parsing, defaults,
/// validation, logging and the status output are all driven by that table. All values apply live with
/// "gtb reload"; <see cref="ToolbeltSlots"/> is read when a world is loaded or joined.
/// </summary>
public sealed class ModSettings
{
    public const float MinTapSeconds = 0.10f;
    public const float MaxTapSeconds = 1.00f;
    public const float MinHapticStrength = 0.05f;
    public const float MaxHapticStrength = 1.0f;
    public const int MinHapticMs = 20;
    public const int MaxHapticMs = 300;
    public const string NoKey = "None";

    private static readonly Regex KeyNamePattern = new Regex("^[A-Za-z][A-Za-z0-9]{0,31}$", RegexOptions.Compiled);

    public bool Enabled { get; private set; } = true;
    public float TapMaxSeconds { get; private set; } = 0.35f;
    public bool AllowWhileAiming { get; private set; } = true;
    public HintMode HintMode { get; private set; } = HintMode.Always;
    public float HintIdleOpacity { get; private set; } = 0.8f;
    public bool Haptics { get; private set; } = true;
    public float HapticStrength { get; private set; } = 0.25f;
    public int HapticMs { get; private set; } = 50;

    /// <summary>InControl key name for slot 11, or <see cref="NoKey"/>.</summary>
    public string KeyboardSlot11Key { get; private set; } = "Minus";

    /// <summary>Toolbelt size this machine offers when it hosts a world (10 or 11).</summary>
    public int ToolbeltSlots { get; private set; } = 11;

    public AuditLevel LogLevel { get; private set; } = AuditLevel.Summary;
    public bool SelfCheckScreenshots { get; private set; }

    public static ModSettings Default { get; } = new ModSettings();

    public bool HasKeyboardSlot11Key => !string.Equals(KeyboardSlot11Key, NoKey, StringComparison.OrdinalIgnoreCase);

    private sealed class Descriptor
    {
        public readonly string Name;
        public readonly bool AppliesOnWorldLoad;
        public readonly Func<ModSettings, string, string> Apply;   // returns an error text or null
        public readonly Func<ModSettings, string> Format;

        public Descriptor(string _name, bool _appliesOnWorldLoad, Func<ModSettings, string, string> _apply, Func<ModSettings, string> _format)
        {
            Name = _name;
            AppliesOnWorldLoad = _appliesOnWorldLoad;
            Apply = _apply;
            Format = _format;
        }
    }

    private static readonly Descriptor[] Descriptors =
    {
        Bool("Enabled", (s, v) => s.Enabled = v, s => s.Enabled),
        Number("TapMaxSeconds", MinTapSeconds, MaxTapSeconds, (s, v) => s.TapMaxSeconds = v, s => s.TapMaxSeconds),
        Bool("AllowWhileAiming", (s, v) => s.AllowWhileAiming = v, s => s.AllowWhileAiming),
        Choice("HintMode", (s, v) => s.HintMode = v, s => s.HintMode),
        Number("HintIdleOpacity", 0f, 1f, (s, v) => s.HintIdleOpacity = v, s => s.HintIdleOpacity),
        Bool("Haptics", (s, v) => s.Haptics = v, s => s.Haptics),
        Number("HapticStrength", MinHapticStrength, MaxHapticStrength, (s, v) => s.HapticStrength = v, s => s.HapticStrength),
        Integer("HapticMs", MinHapticMs, MaxHapticMs, (s, v) => s.HapticMs = v, s => s.HapticMs),
        new Descriptor("KeyboardSlot11Key", false, ApplyKey, s => s.KeyboardSlot11Key),
        new Descriptor("ToolbeltSlots", true, ApplySlots, s => s.ToolbeltSlots.ToString(CultureInfo.InvariantCulture)),
        Choice("LogLevel", (s, v) => s.LogLevel = v, s => s.LogLevel),
        Bool("SelfCheckScreenshots", (s, v) => s.SelfCheckScreenshots = v, s => s.SelfCheckScreenshots)
    };

    /// <summary>Names of all known settings in declaration order.</summary>
    public static IEnumerable<string> SettingNames => Descriptors.Select(d => d.Name);

    /// <summary>True for settings that are read when a world is loaded or joined (not on "gtb reload").</summary>
    public static bool AppliesOnWorldLoad(string _name)
    {
        return Descriptors.Any(d => d.AppliesOnWorldLoad && string.Equals(d.Name, _name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parses a settings document. Unknown names, duplicates and invalid values produce a warning and keep the default.
    /// A missing or malformed document yields the defaults.
    /// </summary>
    public static ModSettings Parse(string _xml, ICollection<string> _warnings)
    {
        var settings = new ModSettings();
        if (string.IsNullOrWhiteSpace(_xml))
        {
            _warnings?.Add("Settings document is empty, using defaults.");
            return settings;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(_xml);
        }
        catch (XmlException e)
        {
            _warnings?.Add($"Settings document is not valid XML ({e.Message}), using defaults.");
            return settings;
        }

        var byName = Descriptors.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reportedDuplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement property in doc.Descendants("property"))
        {
            string name = ((string)property.Attribute("name"))?.Trim();
            string value = (string)property.Attribute("value");
            if (string.IsNullOrEmpty(name))
            {
                _warnings?.Add("Settings: <property> without name attribute ignored.");
                continue;
            }
            if (!byName.TryGetValue(name, out Descriptor descriptor))
            {
                _warnings?.Add($"Settings: unknown setting '{name}' ignored.");
                continue;
            }
            if (!seen.Add(descriptor.Name) && reportedDuplicates.Add(descriptor.Name))
            {
                _warnings?.Add($"Settings: '{descriptor.Name}' is defined more than once, the last value wins.");
            }
            if (value == null)
            {
                _warnings?.Add($"Settings: '{descriptor.Name}' has no value attribute, keeping '{descriptor.Format(settings)}'.");
                continue;
            }
            string error = descriptor.Apply(settings, value);
            if (error != null)
            {
                string fallback = descriptor.Format(Default);
                descriptor.Apply(settings, fallback);
                _warnings?.Add($"Settings: '{descriptor.Name}' value '{value}' is invalid ({error}), using default '{fallback}'.");
            }
        }
        return settings;
    }

    /// <summary>All settings as name/value pairs (stable order), for logs and the status command.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ToPairs()
    {
        return Descriptors.Select(d => new KeyValuePair<string, string>(d.Name, d.Format(this))).ToList();
    }

    /// <summary>Returns a copy with a different audit level (used by "gtb loglevel").</summary>
    public ModSettings WithLogLevel(AuditLevel _level)
    {
        var copy = (ModSettings)MemberwiseClone();
        copy.LogLevel = _level;
        return copy;
    }

    /// <summary>Returns a copy with the keyboard key replaced (used when the key name is unknown to the game).</summary>
    public ModSettings WithKeyboardSlot11Key(string _key)
    {
        var copy = (ModSettings)MemberwiseClone();
        copy.KeyboardSlot11Key = _key;
        return copy;
    }

    // ----- descriptor factories -------------------------------------------------------------

    private static Descriptor Bool(string _name, Action<ModSettings, bool> _set, Func<ModSettings, bool> _get)
    {
        return new Descriptor(_name, false, (s, raw) =>
        {
            if (!TryParseBool(raw, out bool value))
            {
                return "expected true or false";
            }
            _set(s, value);
            return null;
        }, s => _get(s) ? "true" : "false");
    }

    private static Descriptor Number(string _name, float _min, float _max, Action<ModSettings, float> _set, Func<ModSettings, float> _get)
    {
        return new Descriptor(_name, false, (s, raw) =>
        {
            if (!float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || float.IsNaN(value) || float.IsInfinity(value))
            {
                return "expected a number";
            }
            if (value < _min || value > _max)
            {
                return $"expected a value between {_min.ToString("0.###", CultureInfo.InvariantCulture)} and {_max.ToString("0.###", CultureInfo.InvariantCulture)}";
            }
            _set(s, value);
            return null;
        }, s => _get(s).ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static Descriptor Integer(string _name, int _min, int _max, Action<ModSettings, int> _set, Func<ModSettings, int> _get)
    {
        return new Descriptor(_name, false, (s, raw) =>
        {
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                return "expected a whole number";
            }
            if (value < _min || value > _max)
            {
                return $"expected a value between {_min} and {_max}";
            }
            _set(s, value);
            return null;
        }, s => _get(s).ToString(CultureInfo.InvariantCulture));
    }

    private static Descriptor Choice<T>(string _name, Action<ModSettings, T> _set, Func<ModSettings, T> _get) where T : struct, Enum
    {
        return new Descriptor(_name, false, (s, raw) =>
        {
            if (!TryParseEnum(raw, out T value))
            {
                return "expected " + string.Join(", ", Enum.GetNames(typeof(T)));
            }
            _set(s, value);
            return null;
        }, s => _get(s).ToString());
    }

    private static string ApplyKey(ModSettings _settings, string _raw)
    {
        string key = (_raw ?? string.Empty).Trim();
        if (key.Length == 0 || string.Equals(key, NoKey, StringComparison.OrdinalIgnoreCase))
        {
            _settings.KeyboardSlot11Key = NoKey;
            return null;
        }
        if (!KeyNamePattern.IsMatch(key))
        {
            return "expected a key name such as Minus or None";
        }
        _settings.KeyboardSlot11Key = key;
        return null;
    }

    private static string ApplySlots(ModSettings _settings, string _raw)
    {
        if (!int.TryParse((_raw ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || (value != 10 && value != 11))
        {
            return "expected 10 or 11";
        }
        _settings.ToolbeltSlots = value;
        return null;
    }

    public static bool TryParseLevel(string _raw, out AuditLevel _level) => TryParseEnum(_raw, out _level);

    public static bool TryParseEnum<T>(string _raw, out T _value) where T : struct, Enum
    {
        _value = default;
        if (string.IsNullOrWhiteSpace(_raw))
        {
            return false;
        }
        string text = _raw.Trim();
        if (int.TryParse(text, out _))
        {
            return false;   // numeric enum values are not part of the documented format
        }
        return Enum.TryParse(text, true, out _value) && Enum.IsDefined(typeof(T), _value);
    }

    public static bool TryParseBool(string _raw, out bool _value)
    {
        switch ((_raw ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "true":
            case "1":
            case "yes":
            case "on":
                _value = true;
                return true;
            case "false":
            case "0":
            case "no":
            case "off":
                _value = false;
                return true;
            default:
                _value = false;
                return false;
        }
    }
}
