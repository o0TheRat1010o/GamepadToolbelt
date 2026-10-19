using System;
using GamepadToolbelt.Core;
using GamepadToolbelt.Engine;
using GamepadToolbelt.Input;
using UnityEngine;

namespace GamepadToolbelt.UI;

/// <summary>
/// Toolbelt slot with the controller hint plate (template <c>gtb_toolbelt_stack</c>). Everything vanilla does stays in
/// <see cref="XUiC_ItemStack"/>; this subclass only animates its own views. Per frame values (scale, opacity, glow,
/// tap ring, flash) are written straight to the NGUI components, so no XUi view is dirtied every frame; layout and
/// sprites change through the XUi views only when the slot or the glyph family changes.
/// <list type="bullet">
/// <item>Idle: every plate at idle opacity.</item>
/// <item>LB or RB held: reachable plates lift (scale, deeper shadow, glowing frame, face glyph emphasised), the tap
/// slot shows a draining ring, all others dim.</item>
/// <item>Selection: the target plate pops and the slot frame flashes.</item>
/// </list>
/// </summary>
public class XUiC_GtbToolbeltSlot : XUiC_ItemStack
{
    private static readonly Color NeutralFrame = new Color32(110, 110, 110, 255);
    private static readonly Color AccentFrame = new Color32(255, 210, 120, 255);
    private static readonly Color RingColor = new Color32(255, 210, 120, 230);
    private static readonly Color PlateIdle = new Color32(14, 14, 14, 225);
    private static readonly Color PlateWarm = new Color32(44, 33, 14, 235);

    private XUiV_Rect hint;
    private XUiV_Sprite shadow;
    private XUiV_Sprite plate;
    private XUiV_Sprite frame;
    private XUiV_Sprite ring;
    private XUiV_Sprite ringHole;
    private XUiV_Sprite glyph1;
    private XUiV_Sprite glyph2;
    private XUiV_Sprite flash;
    private bool broken;

    private int layoutSlot = -1;
    private int layoutStyle = -1;
    private int glyphCount;
    private bool secondIsFace;

    private float scale = 1f;
    private float alpha;
    private float glow;
    private float bumperAlpha = 1f;
    private float faceAlpha = 1f;
    private float shadowOffset = 2f;
    private int seenSerial;
    private double pulseStart = -10d;

    /// <summary>True when every hint view of the template was found (checked by the selftest).</summary>
    public bool HintViewsResolved { get; private set; }

    /// <summary>The 1-based slot whose chord this plate shows.</summary>
    public int HintSlot => layoutSlot;

    public float HintAlpha => alpha;

    public float HintScale => scale;

    public override void Init()
    {
        base.Init();
        // The chord engine outlives worlds: selections made before this UI existed must not pulse when it appears.
        seenSerial = PadLayer.Engine.Ui.SelectSerial;
        hint = View<XUiV_Rect>(ToolbeltXuiPatcher.HintRectName);
        shadow = View<XUiV_Sprite>("gtbShadow");
        plate = View<XUiV_Sprite>("gtbPlate");
        frame = View<XUiV_Sprite>("gtbFrame");
        ring = View<XUiV_Sprite>("gtbRing");
        ringHole = View<XUiV_Sprite>("gtbRingHole");
        glyph1 = View<XUiV_Sprite>("gtbGlyph1");
        glyph2 = View<XUiV_Sprite>("gtbGlyph2");
        flash = View<XUiV_Sprite>(ToolbeltXuiPatcher.FlashName);
        HintViewsResolved = hint != null && shadow != null && plate != null && frame != null && ring != null && ringHole != null
                            && glyph1 != null && glyph2 != null && flash != null;
        if (!HintViewsResolved)
        {
            ModLog.Warning("A toolbelt slot is missing hint views; its hints stay hidden.", "hint_ui");
        }
        else
        {
            ring.FillDirection = UIBasicSprite.FillDirection.Radial360;
        }
    }

    public override void Update(float _dt)
    {
        base.Update(_dt);
        if (!HintViewsResolved || broken)
        {
            return;
        }
        try
        {
            Animate();
        }
        catch (Exception e)
        {
            broken = true;
            hint.IsVisible = false;
            flash.IsVisible = false;
            ModLog.Error("Toolbelt hint animation failed; the hints of this slot are hidden.", e, "hint_ui");
        }
    }

    private T View<T>(string _id) where T : XUiView => GetChildById(_id)?.ViewComponent as T;

    private void Animate()
    {
        HintPresenter.Update();
        int slot = SlotNumber + 1;
        if (slot != layoutSlot || layoutStyle != HintPresenter.StyleVersion)
        {
            Layout(slot);
        }
        if (glyphCount == 0)
        {
            SetVisible(hint, false);
            SetVisible(flash, false);
            return;
        }

        ChordUiState ui = HintPresenter.Ui;
        double now = HintPresenter.Now;
        float dt = HintPresenter.DeltaTime;
        SlotHintState state = SlotMap.HintState(slot, ui, HintPresenter.SlotCount);
        double tapProgress = slot == SlotMap.FirstSlot ? ui.TapProgress(PadSide.Left, now, HintPresenter.TapMaxSeconds)
            : slot == SlotMap.ExtraSlot ? ui.TapProgress(PadSide.Right, now, HintPresenter.TapMaxSeconds) : 1d;
        HintTargets target = HintMotion.Targets(state, tapProgress, HintPresenter.IdleOpacity);

        if (ui.SelectSerial != seenSerial)
        {
            seenSerial = ui.SelectSerial;
            if (ui.LastSelectedSlot == slot)
            {
                pulseStart = now;
            }
        }
        double pulseAge = now - pulseStart;

        scale = HintMotion.Approach(scale, target.Scale, HintMotion.EaseSpeed, dt);
        alpha = HintMotion.Approach(alpha, target.Alpha * HintPresenter.GlobalAlpha, HintMotion.EaseSpeed, dt);
        glow = HintMotion.Approach(glow, target.Glow, HintMotion.EaseSpeed, dt);
        bumperAlpha = HintMotion.Approach(bumperAlpha, target.BumperGlyphAlpha, HintMotion.EaseSpeed, dt);
        faceAlpha = HintMotion.Approach(faceAlpha, target.FaceGlyphAlpha, HintMotion.EaseSpeed, dt);
        shadowOffset = HintMotion.Approach(shadowOffset, target.ShadowOffset, HintMotion.EaseSpeed, dt);

        // Selection flash on the slot frame (feedback even while the plates are hidden, unless hints are off).
        float flashAlpha = HintPresenter.Enabled ? HintMotion.FlashAlpha(pulseAge) : 0f;
        SetVisible(flash, flashAlpha > 0.004f);
        if (flashAlpha > 0.004f)
        {
            SetColor(flash, new Color(AccentFrame.r, AccentFrame.g, AccentFrame.b, flashAlpha));
        }

        bool visible = alpha > 0.004f;
        SetVisible(hint, visible);
        if (!visible)
        {
            return;
        }
        float popped = Math.Min(HintMotion.MaxScale, scale * (1f + HintMotion.PulseBump(pulseAge)));
        hint.UiTransform.localScale = new Vector3(popped, popped, 1f);
        hint.widget.alpha = alpha;

        Vector3 shadowPosition = shadow.UiTransform.localPosition;
        shadow.UiTransform.localPosition = new Vector3(shadowOffset, -shadowOffset, shadowPosition.z);

        float frameGlow = Math.Max(glow, HintMotion.FlashAlpha(pulseAge));
        SetColor(frame, Color.Lerp(NeutralFrame, AccentFrame, frameGlow));
        SetColor(plate, Color.Lerp(PlateIdle, PlateWarm, glow));
        SetColor(glyph1, new Color(1f, 1f, 1f, glyphCount == 2 && secondIsFace ? bumperAlpha : 1f));
        if (glyphCount == 2)
        {
            SetColor(glyph2, new Color(1f, 1f, 1f, secondIsFace ? faceAlpha : 1f));
            float boost = secondIsFace ? 1f + HintMotion.FaceGlyphBoost * glow : 1f;
            glyph2.UiTransform.localScale = new Vector3(boost, boost, 1f);
        }

        bool ringVisible = target.RingVisible && target.RingFill > 0f;
        SetVisible(ring, ringVisible);
        SetVisible(ringHole, ringVisible);
        if (ringVisible)
        {
            ring.fillAmount = target.RingFill;
            if (ring.sprite != null)
            {
                ring.sprite.fillAmount = target.RingFill;
            }
            SetColor(ring, RingColor);
        }
    }

    /// <summary>Plate width, glyph positions and sprites for the slot's chord (one or two glyphs).</summary>
    private void Layout(int _slot)
    {
        layoutSlot = _slot;
        layoutStyle = HintPresenter.StyleVersion;
        SlotChord? chord = SlotMap.ChordFor(_slot);
        if (!chord.HasValue)
        {
            glyphCount = 0;
            return;
        }
        glyphCount = chord.Value.GlyphCount;
        secondIsFace = chord.Value.Second.HasValue && SlotMap.IsFace(chord.Value.Second.Value);
        int width = ToolbeltXuiPatcher.PlateWidth(glyphCount);
        var size = new Vector2i(width, ToolbeltXuiPatcher.PlateHeight);
        shadow.Size = size;
        plate.Size = size;
        frame.Size = size;
        glyph1.Position = new Vector2i(ToolbeltXuiPatcher.GlyphX(glyphCount, 0), 0);
        glyph1.SpriteName = HintPresenter.SpriteFor(chord.Value.First);
        ring.Position = new Vector2i(ToolbeltXuiPatcher.GlyphX(glyphCount, glyphCount - 1), 0);
        ringHole.Position = ring.Position;
        SetVisible(ring, false);
        SetVisible(ringHole, false);
        if (glyphCount == 2)
        {
            glyph2.Position = new Vector2i(ToolbeltXuiPatcher.GlyphX(2, 1), 0);
            glyph2.SpriteName = HintPresenter.SpriteFor(chord.Value.Second.Value);
            SetVisible(glyph2, true);
        }
        else
        {
            SetVisible(glyph2, false);
        }
    }

    private static void SetVisible(XUiView _view, bool _visible)
    {
        if (_view.IsVisible != _visible)
        {
            _view.IsVisible = _visible;
        }
    }

    /// <summary>Colour straight to the NGUI sprite; the XUi field is kept in sync so a later refresh keeps it.</summary>
    private static void SetColor(XUiV_Sprite _sprite, Color _color)
    {
        _sprite.color = _color;
        if (_sprite.sprite != null && _sprite.sprite.color != _color)
        {
            _sprite.sprite.color = _color;
        }
    }
}
