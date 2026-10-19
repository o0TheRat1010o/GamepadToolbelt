using System;
using GamepadToolbelt.Engine;
using UnityEngine;

namespace GamepadToolbelt.UI;

/// <summary>
/// Controller of the rect that wraps the XP bar (<c>gtbXpBar</c>): while hints are shown the bar glides up so the
/// plates on the slots' top edge have room; in keyboard mode, or with hints off, it sits at its vanilla position.
/// The XP bar's own bindings (xp, xpcolor, showxp) still resolve to the toolbelt window's controller.
/// </summary>
public class XUiC_GtbHintState : XUiController
{
    private Vector3 basePosition;
    private bool baseKnown;
    private bool broken;

    public static int LiveInstances { get; private set; }

    public float AppliedOffset { get; private set; }

    public override void Init()
    {
        base.Init();
        LiveInstances++;
    }

    public override void Cleanup()
    {
        base.Cleanup();
        LiveInstances = Math.Max(0, LiveInstances - 1);
    }

    public override void Update(float _dt)
    {
        base.Update(_dt);
        if (broken || ViewComponent?.UiTransform == null)
        {
            return;
        }
        try
        {
            HintPresenter.Update();
            if (!baseKnown)
            {
                Vector2i position = ViewComponent.Position;
                basePosition = new Vector3(position.x, position.y, ViewComponent.UiTransform.localPosition.z);
                baseKnown = true;
            }
            float offset = Mathf.Round(HintPresenter.XpOffset * 2f) / 2f;
            if (!Mathf.Approximately(offset, AppliedOffset))
            {
                AppliedOffset = offset;
                ViewComponent.UiTransform.localPosition = basePosition + new Vector3(0f, offset, 0f);
            }
        }
        catch (Exception e)
        {
            broken = true;
            ModLog.Error("Moving the XP bar failed; it stays at its vanilla position.", e, "hint_ui");
        }
    }
}
