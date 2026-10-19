using System;

namespace GamepadToolbelt.Core;

/// <summary>Visual targets of one hint plate; the slot controller eases its current values towards them.</summary>
public struct HintTargets
{
    public float Scale;
    public float Alpha;

    /// <summary>0 = neutral frame colour, 1 = full accent glow.</summary>
    public float Glow;

    public float BumperGlyphAlpha;
    public float FaceGlyphAlpha;
    public bool RingVisible;

    /// <summary>Remaining share of the tap window (1 = full ring, 0 = expired).</summary>
    public float RingFill;

    /// <summary>Drop shadow offset in pixels: the plate "lifts" when it becomes reachable.</summary>
    public float ShadowOffset;
}

/// <summary>Animation math of the hint plates and the XP bar (pure functions, unit tested).</summary>
public static class HintMotion
{
    public const float ReachableScale = 1.3f;
    public const float TapScale = 1.2f;
    public const float DimmedScale = 0.92f;
    public const float DimmedAlpha = 0.25f;
    public const float PulseSeconds = 0.3f;
    public const float PulseAmplitude = 0.22f;

    /// <summary>Upper bound of scale plus pulse, so a popping plate barely reaches into the neighbour slot.</summary>
    public const float MaxScale = 1.5f;

    /// <summary>Extra scale of the face glyph on a reachable plate (the button to press next).</summary>
    public const float FaceGlyphBoost = 0.14f;
    public const float FlashSeconds = 0.45f;

    /// <summary>The slot flash stays at full strength this long before it fades.</summary>
    public const float FlashHoldSeconds = 0.08f;

    /// <summary>Pixels the XP bar slides up while hints are visible (idle plates keep a 2 px gap below it).</summary>
    public const float XpLift = 15f;

    /// <summary>Easing speed (1/s) of scale and opacity; higher = snappier.</summary>
    public const float EaseSpeed = 18f;

    /// <summary>Easing speed (1/s) of the XP bar slide.</summary>
    public const float XpEaseSpeed = 12f;

    public static HintTargets Targets(SlotHintState _state, double _tapProgress, float _idleOpacity)
    {
        switch (_state)
        {
            case SlotHintState.Reachable:
                return new HintTargets { Scale = ReachableScale, Alpha = 1f, Glow = 1f, BumperGlyphAlpha = 0.6f, FaceGlyphAlpha = 1f, ShadowOffset = 4f };
            case SlotHintState.TapPending:
                return new HintTargets
                {
                    Scale = TapScale, Alpha = 1f, Glow = 0.6f, BumperGlyphAlpha = 1f, FaceGlyphAlpha = 1f, RingVisible = true,
                    RingFill = (float)Math.Max(0d, Math.Min(1d, 1d - _tapProgress)), ShadowOffset = 3f
                };
            case SlotHintState.Dimmed:
                return new HintTargets { Scale = DimmedScale, Alpha = DimmedAlpha, Glow = 0f, BumperGlyphAlpha = 1f, FaceGlyphAlpha = 1f, ShadowOffset = 1f };
            default:
                return new HintTargets { Scale = 1f, Alpha = Clamp01(_idleOpacity), Glow = 0f, BumperGlyphAlpha = 1f, FaceGlyphAlpha = 1f, ShadowOffset = 2f };
        }
    }

    /// <summary>Frame-rate independent exponential easing of <paramref name="_current"/> towards <paramref name="_target"/>.</summary>
    public static float Approach(float _current, float _target, float _speed, float _deltaTime)
    {
        if (_deltaTime <= 0f || float.IsNaN(_current))
        {
            return float.IsNaN(_current) ? _target : _current;
        }
        float k = 1f - (float)Math.Exp(-_speed * _deltaTime);
        float next = _current + (_target - _current) * k;
        return Math.Abs(_target - next) < 0.0005f ? _target : next;
    }

    /// <summary>Extra scale of the selection pulse: a quick swell and settle over <see cref="PulseSeconds"/>.</summary>
    public static float PulseBump(double _age)
    {
        if (_age < 0d || _age >= PulseSeconds)
        {
            return 0f;
        }
        return PulseAmplitude * (float)Math.Sin(Math.PI * _age / PulseSeconds);
    }

    /// <summary>Opacity of the slot flash frame after a selection: full for a moment, then a quadratic fade.</summary>
    public static float FlashAlpha(double _age)
    {
        if (_age < 0d || _age >= FlashSeconds)
        {
            return 0f;
        }
        if (_age <= FlashHoldSeconds)
        {
            return 1f;
        }
        float t = 1f - (float)((_age - FlashHoldSeconds) / (FlashSeconds - FlashHoldSeconds));
        return t * t;
    }

    public static float Clamp01(float _value) => _value < 0f ? 0f : _value > 1f ? 1f : _value;

    /// <summary>Linear colour channel blend.</summary>
    public static float Lerp(float _a, float _b, float _t) => _a + (_b - _a) * Clamp01(_t);
}
