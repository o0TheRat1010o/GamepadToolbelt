using GamepadToolbelt.Core;
using Platform;

namespace GamepadToolbelt.Input;

/// <summary>Result of one gate evaluation: open, or the first reason why the toolbelt is not active.</summary>
internal readonly struct GateResult
{
    public readonly bool Open;
    public readonly string Reason;
    public readonly EntityPlayerLocal Player;

    public GateResult(bool _open, string _reason, EntityPlayerLocal _player)
    {
        Open = _open;
        Reason = _reason;
        Player = _player;
    }

    public static GateResult Closed(string _reason, EntityPlayerLocal _player = null) => new GateResult(false, _reason, _player);
}

/// <summary>
/// "The toolbelt is active": the player plays on foot and nothing else uses the controller. Mirrors the conditions
/// under which vanilla's PlayerMoveController handles LB/RB (toolbelt step and radial) and adds the windows, radials
/// and editor modes in which the toolbelt must keep its vanilla behaviour. Evaluated once per input tick.
/// </summary>
internal static class ToolbeltGate
{
    public static GateResult Evaluate(ModSettings _settings, bool _forKeyboard = false)
    {
        if (!_settings.Enabled)
        {
            return GateResult.Closed("disabled");
        }
        GameManager game = GameManager.Instance;
        World world = game?.World;
        if (world == null)
        {
            return GateResult.Closed("no_world");
        }
        EntityPlayerLocal player = world.GetPrimaryPlayer();
        if (player == null)
        {
            return GateResult.Closed("no_player");
        }
        if (!player.Spawned)
        {
            return GateResult.Closed("not_spawned", player);
        }
        if (player.IsDead())
        {
            return GateResult.Closed("dead", player);
        }
        if (player.AttachedToEntity != null)
        {
            return GateResult.Closed("attached", player);
        }
        if (player.emodel != null && player.emodel.IsRagdollActive)
        {
            return GateResult.Closed("ragdoll", player);
        }
        if (game.gameStateManager == null || !game.gameStateManager.IsGameStarted() || GameStats.GetInt(EnumGameStats.GameState) != 1)
        {
            return GateResult.Closed("game_state", player);
        }
        if (game.IsPaused())
        {
            return GateResult.Closed("paused", player);
        }
        if (game.IsEditMode() || ToolbeltConfig.CurrentConfig != ToolbeltConfig.GameConfig)
        {
            return GateResult.Closed("editor", player);
        }
        if (GamePrefs.GetInt(EnumGamePrefs.SelectionOperationMode) != 0)
        {
            return GateResult.Closed("selection_mode", player);
        }

        PlayerActionsLocal actions = PlatformManager.NativePlatform?.Input?.PrimaryPlayer;
        if (actions == null)
        {
            return GateResult.Closed("no_input", player);
        }
        if (!actions.Enabled)
        {
            return GateResult.Closed("local_actions_off", player);
        }
        if (actions.GUIActions != null && actions.GUIActions.Enabled)
        {
            return GateResult.Closed("gui_actions", player);
        }
        if (actions.VehicleActions != null && actions.VehicleActions.Enabled)
        {
            return GateResult.Closed("vehicle_actions", player);
        }

        LocalPlayerUI ui = player.PlayerUI;
        GUIWindowManager windows = ui?.windowManager;
        if (windows == null)
        {
            return GateResult.Closed("no_ui", player);
        }
        if (windows.IsModalWindowOpen())
        {
            return GateResult.Closed("modal_window", player);
        }
        if (windows.IsCursorWindowOpen())
        {
            return GateResult.Closed("cursor_window", player);
        }
        if (windows.IsInputActive())
        {
            return GateResult.Closed("text_input", player);
        }
        if (windows.IsInputLocked)
        {
            return GateResult.Closed("input_locked", player);
        }
        if (windows.IsFullHUDDisabled())
        {
            return GateResult.Closed("hud_hidden", player);
        }
        XUiC_Radial radial = ui.xui?.RadialWindow;
        if (radial != null && (radial.isOpenRequested || windows.IsWindowOpen(radial.WindowGroup)))
        {
            return GateResult.Closed("radial", player);
        }

        PlayerMoveController move = player.MoveController;
        if (move == null || !move.bCanControlOverride)
        {
            return GateResult.Closed("no_control", player);
        }
        if (move.guiClosedUpdateCount < 3)
        {
            return GateResult.Closed("gui_just_closed", player);
        }
        if (!_forKeyboard && !_settings.AllowWhileAiming && player.AimingGun)
        {
            return GateResult.Closed("aiming", player);
        }
        return new GateResult(true, "open", player);
    }
}
