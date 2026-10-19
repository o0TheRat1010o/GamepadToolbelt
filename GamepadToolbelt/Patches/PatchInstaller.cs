using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using GamepadToolbelt.Core;
using GamepadToolbelt.Engine;
using GamepadToolbelt.Input;
using GamepadToolbelt.Net;
using HarmonyLib;
using InControl;
using UnityEngine;

namespace GamepadToolbelt.Patches;

/// <summary>One Harmony patch: a resolved game method plus our prefix and/or postfix.</summary>
internal sealed class PatchSpec
{
    public string Label;
    public Func<MethodBase> Resolve;
    public MethodInfo Prefix;
    public MethodInfo Postfix;
    public int PrefixPriority = Priority.Normal;
    public int PostfixPriority = Priority.Normal;

    public IEnumerable<MethodInfo> PatchMethods => new[] { Prefix, Postfix }.Where(m => m != null);
}

internal sealed class PatchGroupStatus
{
    public string Name;
    public string HarmonyId;
    public string State = "not_installed";
    public string Error;
    public readonly List<string> Targets = new List<string>();
    public readonly List<MethodBase> Methods = new List<MethodBase>();

    public bool Installed => State == "installed";
}

/// <summary>
/// Installs the patch groups. Each group is all-or-nothing: every target is resolved with its exact parameter types and
/// every parameter our patch methods bind by name must exist on the target; if anything fails (now or while patching),
/// the group's Harmony instance is unpatched completely and that feature keeps its vanilla behaviour:
/// <list type="bullet">
/// <item><b>Input</b> - controller chords (InControl tick, binding mask, selection in PlayerMoveController).</item>
/// <item><b>Toolbelt</b> - the toolbelt size from toolbelt.xml's gameconfig (back to 10 whenever a client joins).</item>
/// <item><b>Net</b> - the server offer of the 11th slot, the toolbelt-index clamp and the receive guards.</item>
/// <item><b>UI</b> - the client-local XUi patch (hint template, window geometry, XP bar).</item>
/// <item><b>Haptics</b> - the chord pulse, kept alive after the game's own rumble update and stopped explicitly.</item>
/// <item><b>Diagnostics</b> - invariant checks (vanilla toolbelt radial while a bumper is owned) and the local "gtb"
/// console command on clients.</item>
/// </list>
/// The extra slot is only offered when Toolbelt and Net are both installed (the clamp protects vanilla players).
/// Haptics and Diagnostics are optional: without them the mod works, just without the pulse or these checks.
/// </summary>
internal static class PatchInstaller
{
    public const string InputGroup = "Input";
    public const string ToolbeltGroup = "Toolbelt";
    public const string NetGroup = "Net";
    public const string UiGroup = "UI";
    public const string HapticsGroup = "Haptics";
    public const string DiagnosticsGroup = "Diagnostics";
    public const string HarmonyIdPrefix = "studio.softdot.gamepadtoolbelt.";

    public static readonly List<PatchGroupStatus> Groups = new List<PatchGroupStatus>();

    public static PatchGroupStatus Find(string _name) => Groups.FirstOrDefault(g => g.Name == _name);

    public static bool IsInstalled(string _name) => Find(_name)?.Installed == true;

    /// <summary>Optional groups: a failure is a warning, the features that matter keep working.</summary>
    public static bool IsOptional(string _name) => _name == HapticsGroup || _name == DiagnosticsGroup;

    /// <summary>Safety interlock: the 11th slot needs the toolbelt size patch and the network clamp.</summary>
    public static bool ExtraSlotAllowed => IsInstalled(ToolbeltGroup) && IsInstalled(NetGroup);

    public static void InstallAll()
    {
        Groups.Clear();
        bool dedicated = GameManager.IsDedicatedServer;
        Groups.Add(dedicated ? Skipped(InputGroup, "dedicated server (no local player)") : InstallGroup(InputGroup, InputSpecs()));
        Groups.Add(InstallGroup(ToolbeltGroup, ToolbeltSpecs()));
        Groups.Add(InstallGroup(NetGroup, NetSpecs()));
        Groups.Add(dedicated ? Skipped(UiGroup, "dedicated server (no UI)") : InstallGroup(UiGroup, UiSpecs()));
        Groups.Add(dedicated ? Skipped(HapticsGroup, "dedicated server (no controller)") : InstallGroup(HapticsGroup, HapticsSpecs()));
        Groups.Add(dedicated ? Skipped(DiagnosticsGroup, "dedicated server (no UI)") : InstallGroup(DiagnosticsGroup, DiagnosticsSpecs()));
    }

    // ----- group definitions ------------------------------------------------------------------

    private static List<PatchSpec> InputSpecs()
    {
        return new List<PatchSpec>
        {
            new PatchSpec
            {
                Label = "InControl.InputManager.UpdatePlayerActionSets",
                Resolve = () => AccessTools.DeclaredMethod(typeof(InputManager), "UpdatePlayerActionSets", new[] { typeof(float) }),
                Prefix = Method(typeof(InputPatches), nameof(InputPatches.UpdatePlayerActionSetsPrefix)),
                PrefixPriority = Priority.First
            },
            new PatchSpec
            {
                Label = "InControl.DeviceBindingSource.GetValue",
                Resolve = () => AccessTools.DeclaredMethod(typeof(DeviceBindingSource), nameof(DeviceBindingSource.GetValue), new[] { typeof(InputDevice) }),
                Postfix = Method(typeof(InputPatches), nameof(InputPatches.GetValuePostfix)),
                PostfixPriority = Priority.Last
            },
            new PatchSpec
            {
                Label = "InControl.DeviceBindingSource.GetState",
                Resolve = () => AccessTools.DeclaredMethod(typeof(DeviceBindingSource), nameof(DeviceBindingSource.GetState), new[] { typeof(InputDevice) }),
                Postfix = Method(typeof(InputPatches), nameof(InputPatches.GetStatePostfix)),
                PostfixPriority = Priority.Last
            },
            new PatchSpec
            {
                Label = "PlayerMoveController.Update",
                Resolve = () => AccessTools.DeclaredMethod(typeof(PlayerMoveController), "Update", Type.EmptyTypes),
                Postfix = Method(typeof(InputPatches), nameof(InputPatches.PlayerMoveControllerUpdatePostfix))
            }
        };
    }

    private static List<PatchSpec> ToolbeltSpecs()
    {
        return new List<PatchSpec>
        {
            new PatchSpec
            {
                Label = "ToolbeltFromXml.Load",
                Resolve = () => AccessTools.DeclaredMethod(typeof(ToolbeltFromXml), nameof(ToolbeltFromXml.Load), new[] { typeof(XmlFile) }),
                Prefix = Method(typeof(ToolbeltConfigPatches), nameof(ToolbeltConfigPatches.LoadPrefix))
            },
            new PatchSpec
            {
                Label = "ToolbeltFromXml.ParseConfig",
                Resolve = () => AccessTools.DeclaredMethod(typeof(ToolbeltFromXml), "ParseConfig", new[] { typeof(XElement), typeof(string), typeof(ToolbeltConfig) }),
                Postfix = Method(typeof(ToolbeltConfigPatches), nameof(ToolbeltConfigPatches.ParseConfigPostfix))
            },
            new PatchSpec
            {
                Label = "WorldStaticData.WaitForConfigsFromServer",
                Resolve = () => AccessTools.DeclaredMethod(typeof(WorldStaticData), nameof(WorldStaticData.WaitForConfigsFromServer), Type.EmptyTypes),
                Prefix = Method(typeof(ToolbeltConfigPatches), nameof(ToolbeltConfigPatches.WaitForConfigsFromServerPrefix))
            }
        };
    }

    private static List<PatchSpec> NetSpecs()
    {
        return new List<PatchSpec>
        {
            new PatchSpec
            {
                Label = "WorldStaticData.CachePatchedXml",
                Resolve = () => typeof(WorldStaticData).GetMethods(AccessTools.all)
                    .SingleOrDefault(m => m.Name == "CachePatchedXml"
                                          && m.GetParameters().Select(p => p.ParameterType.Name).SequenceEqual(new[]
                                          {
                                              "XmlLoadInfo", "XmlFile", "MicroStopwatch", "MemoryStream", "DeflateOutputStream", "Action"
                                          })),
                Prefix = Method(typeof(NetPatches), nameof(NetPatches.CachePatchedXmlPrefix))
            },
            new PatchSpec
            {
                Label = "NetPackageHoldingItem.Setup",
                Resolve = () => AccessTools.DeclaredMethod(typeof(NetPackageHoldingItem), nameof(NetPackageHoldingItem.Setup), new[] { typeof(EntityAlive) }),
                Postfix = Method(typeof(NetPatches), nameof(NetPatches.HoldingItemSetupPostfix)),
                PostfixPriority = Priority.Last
            },
            new PatchSpec
            {
                Label = "NetPackagePlayerStats.Setup",
                Resolve = () => AccessTools.DeclaredMethod(typeof(NetPackagePlayerStats), nameof(NetPackagePlayerStats.Setup), new[] { typeof(EntityAlive) }),
                Postfix = Method(typeof(NetPatches), nameof(NetPatches.PlayerStatsSetupPostfix)),
                PostfixPriority = Priority.Last
            },
            new PatchSpec
            {
                Label = "NetPackageItemActionEffects.Setup",
                Resolve = () => AccessTools.DeclaredMethod(typeof(NetPackageItemActionEffects), nameof(NetPackageItemActionEffects.Setup),
                    new[] { typeof(int), typeof(int), typeof(int), typeof(ItemActionFiringState), typeof(Vector3), typeof(Vector3), typeof(int) }),
                Postfix = Method(typeof(NetPatches), nameof(NetPatches.ItemActionEffectsSetupPostfix)),
                PostfixPriority = Priority.Last
            },
            new PatchSpec
            {
                Label = "GameManager.ItemActionEffectsClient",
                Resolve = () => AccessTools.DeclaredMethod(typeof(GameManager), nameof(GameManager.ItemActionEffectsClient),
                    new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(Vector3), typeof(Vector3), typeof(int) }),
                Prefix = Method(typeof(NetPatches), nameof(NetPatches.ItemActionEffectsClientPrefix)),
                PrefixPriority = Priority.First
            },
            new PatchSpec
            {
                Label = "EntityAlive.EntityNetworkStats.ToEntity",
                Resolve = () => AccessTools.DeclaredMethod(typeof(EntityAlive.EntityNetworkStats), nameof(EntityAlive.EntityNetworkStats.ToEntity), new[] { typeof(EntityAlive) }),
                Prefix = Method(typeof(NetPatches), nameof(NetPatches.ToEntityPrefix)),
                PrefixPriority = Priority.First
            },
            new PatchSpec
            {
                Label = "EntityAlive.updateNetworkStats",
                Resolve = () => AccessTools.DeclaredMethod(typeof(EntityAlive), "updateNetworkStats", Type.EmptyTypes),
                Prefix = Method(typeof(NetPatches), nameof(NetPatches.UpdateNetworkStatsPrefix)),
                PrefixPriority = Priority.First
            }
        };
    }

    private static List<PatchSpec> UiSpecs()
    {
        return new List<PatchSpec>
        {
            new PatchSpec
            {
                Label = "XmlPatcher.ApplyConditionalXmlBlocks",
                Resolve = () => typeof(XmlPatcher).GetMethods(AccessTools.all)
                    .SingleOrDefault(m => m.Name == nameof(XmlPatcher.ApplyConditionalXmlBlocks)
                                          && m.GetParameters().Select(p => p.ParameterType.Name).SequenceEqual(new[]
                                          {
                                              "String", "XmlFile", "MicroStopwatch", "EEvaluator", "Action"
                                          })),
                Prefix = Method(typeof(UiPatches), nameof(UiPatches.ApplyConditionalXmlBlocksPrefix)),
                PrefixPriority = Priority.Last
            }
        };
    }

    private static List<PatchSpec> HapticsSpecs()
    {
        return new List<PatchSpec>
        {
            new PatchSpec
            {
                Label = "TriggerEffectManager.Update",
                Resolve = () => AccessTools.DeclaredMethod(typeof(TriggerEffectManager), nameof(TriggerEffectManager.Update), Type.EmptyTypes),
                Postfix = Method(typeof(Haptics), nameof(Haptics.AfterTriggerEffectsUpdate))
            }
        };
    }

    private static List<PatchSpec> DiagnosticsSpecs()
    {
        return new List<PatchSpec>
        {
            new PatchSpec
            {
                Label = "XUiC_Radial.SetupToolbeltMenu",
                Resolve = () => AccessTools.DeclaredMethod(typeof(XUiC_Radial), nameof(XUiC_Radial.SetupToolbeltMenu), new[] { typeof(int) }),
                Prefix = Method(typeof(DiagnosticsPatches), nameof(DiagnosticsPatches.SetupToolbeltMenuPrefix))
            },
            new PatchSpec
            {
                Label = "ConnectionManager.SendToServer",
                Resolve = () => AccessTools.DeclaredMethod(typeof(ConnectionManager), nameof(ConnectionManager.SendToServer), new[] { typeof(NetPackage), typeof(bool) }),
                Prefix = Method(typeof(DiagnosticsPatches), nameof(DiagnosticsPatches.SendToServerPrefix)),
                PrefixPriority = Priority.First
            }
        };
    }

    // ----- installation -------------------------------------------------------------------------

    private static PatchGroupStatus Skipped(string _name, string _reason)
    {
        return new PatchGroupStatus { Name = _name, HarmonyId = HarmonyIdPrefix + _name.ToLowerInvariant(), State = "skipped", Error = _reason };
    }

    private static PatchGroupStatus InstallGroup(string _name, List<PatchSpec> _specs)
    {
        var status = new PatchGroupStatus { Name = _name, HarmonyId = HarmonyIdPrefix + _name.ToLowerInvariant() };
        var resolved = new List<KeyValuePair<PatchSpec, MethodBase>>();
        foreach (PatchSpec spec in _specs)
        {
            MethodBase target;
            try
            {
                target = spec.Resolve();
            }
            catch (Exception e)
            {
                return Fail(status, $"{spec.Label}: resolving the target threw {e.GetType().Name}: {e.Message}");
            }
            if (target == null)
            {
                return Fail(status, $"{spec.Label}: target method not found (game update?)");
            }
            foreach (MethodInfo patch in spec.PatchMethods)
            {
                string bindingError = PatchBinding.Validate(target, patch);
                if (bindingError != null)
                {
                    return Fail(status, $"{spec.Label}: {bindingError}");
                }
            }
            resolved.Add(new KeyValuePair<PatchSpec, MethodBase>(spec, target));
            status.Targets.Add(spec.Label);
        }

        var harmony = new Harmony(status.HarmonyId);
        try
        {
            foreach (KeyValuePair<PatchSpec, MethodBase> pair in resolved)
            {
                PatchSpec spec = pair.Key;
                harmony.Patch(pair.Value,
                    prefix: spec.Prefix != null ? new HarmonyMethod(spec.Prefix, spec.PrefixPriority) : null,
                    postfix: spec.Postfix != null ? new HarmonyMethod(spec.Postfix, spec.PostfixPriority) : null);
                status.Methods.Add(pair.Value);
            }
        }
        catch (Exception e)
        {
            try
            {
                harmony.UnpatchSelf();
            }
            catch (Exception unpatchError)
            {
                ModLog.Error($"Unpatching group {_name} after a failure also failed.", unpatchError, "unpatch");
            }
            status.Methods.Clear();
            ModLog.Error($"Patch group {_name} could not be installed and was rolled back; this feature stays vanilla.", e, "patch_install");
            return Fail(status, e.GetType().Name + ": " + e.Message, _logError: false);
        }

        status.State = "installed";
        ModLog.Info($"Patch group {_name} installed ({status.Methods.Count} methods).");
        return status;
    }

    /// <summary>True when Harmony reports our patches on every target of the group (used by the selftest).</summary>
    public static bool VerifyApplied(PatchGroupStatus _group, out string _problem)
    {
        _problem = null;
        foreach (MethodBase method in _group.Methods)
        {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
            bool ours = info != null && info.Owners.Contains(_group.HarmonyId);
            if (!ours)
            {
                _problem = $"{method.DeclaringType?.Name}.{method.Name} has no patch from {_group.HarmonyId}";
                return false;
            }
        }
        return true;
    }

    private static PatchGroupStatus Fail(PatchGroupStatus _status, string _error, bool _logError = true)
    {
        _status.State = "failed";
        _status.Error = _error;
        if (_logError)
        {
            ModLog.Error($"Patch group {_status.Name} not installed: {_error}. This feature stays vanilla.", null, "patch_install");
        }
        return _status;
    }

    private static MethodInfo Method(Type _type, string _name)
    {
        return AccessTools.DeclaredMethod(_type, _name) ?? throw new MissingMethodException(_type.Name, _name);
    }
}
