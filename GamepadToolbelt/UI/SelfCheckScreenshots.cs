using System;
using System.Collections.Generic;
using System.IO;
using GamepadToolbelt.Core;
using GamepadToolbelt.Engine;
using UnityEngine;

namespace GamepadToolbelt.UI;

/// <summary>
/// Optional diagnostics (setting SelfCheckScreenshots): one screenshot per session of each hint state - rest, LB held,
/// RB held, both held, slot 11 just selected - once the state has been stable long enough for the animations to
/// settle. Files go to Mods/GamepadToolbelt/Logs/screens and are listed in the audit log.
/// </summary>
internal static class SelfCheckScreenshots
{
    public const double SettleSeconds = 0.35d;

    private static readonly HashSet<string> taken = new HashSet<string>(StringComparer.Ordinal);
    private static string candidate;
    private static double candidateSince;

    public static void ResetSession()
    {
        taken.Clear();
        candidate = null;
    }

    public static void Update()
    {
        if (!GamepadToolbeltMod.Settings.SelfCheckScreenshots || GameManager.IsDedicatedServer || !HintPresenter.ControllerMode)
        {
            candidate = null;
            return;
        }
        ChordUiState ui = HintPresenter.Ui;
        double now = Time.unscaledTime;
        string state;
        if (ui.LeftHeld && ui.RightHeld)
        {
            state = "both";
        }
        else if (ui.LeftHeld)
        {
            state = "lb";
        }
        else if (ui.RightHeld)
        {
            state = "rb";
        }
        else if (ui.LastSelectedSlot == SlotMap.ExtraSlot && now - ui.LastSelectTime < 1.5d)
        {
            state = "slot11";
        }
        else
        {
            state = HintPresenter.Visible && HintPresenter.GlobalAlpha > 0.98f ? "rest" : null;
        }
        if (state == null || taken.Contains(state))
        {
            candidate = null;
            return;
        }
        if (candidate != state)
        {
            candidate = state;
            candidateSince = now;
            return;
        }
        if (now - candidateSince >= SettleSeconds)
        {
            Capture(state);
            taken.Add(state);
            candidate = null;
        }
    }

    public static string Capture(string _state)
    {
        try
        {
            string directory = Path.Combine(GamepadToolbeltMod.ModPath, "Logs", "screens");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"{AuditLog.SessionId}_{_state}.png");
            ScreenCapture.CaptureScreenshot(path);
            RuntimeStats.Screenshots++;
            if (AuditLog.Summary)
            {
                JsonLineWriter record = AuditLog.Begin(AuditRecords.Screenshot);
                record.Prop("state", _state).Prop("file", path).Prop("width", Screen.width).Prop("height", Screen.height)
                    .Prop("hints_alpha", HintPresenter.GlobalAlpha).Prop("xp_offset", HintPresenter.XpOffset);
                AuditLog.Commit(record);
            }
            return path;
        }
        catch (Exception e)
        {
            ModLog.Warning("Self-check screenshot failed: " + e.Message, "screenshot");
            return null;
        }
    }
}
