using KatStreamToolkit.Chat;
using KatStreamToolkit.Models;

namespace KatStreamToolkit.Services;

// Automatic OBS scene changes on alert events: a raid/sub/follow that arrives
// through the alert feed switches OBS to the configured scene, holds it for a
// bit, then returns to whatever was on before. The transport is the same
// obs-websocket link the go-live buttons and the !commands runner use - this
// only decides WHEN to switch.
public sealed class ObsAutoSwitchService : IDisposable
{
    private readonly AppConfig _config;
    private readonly Func<Task<string?>> _getCurrentScene;
    private readonly Func<string, Task> _switchScene;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _holdCts;

    // State of the temporary switch: the scene we switched TO and the scene
    // that was on program before the first switch of a chain (so raid ->
    // follow chains return to the original scene, not to each other).
    private string? _activeTarget;
    private string? _returnScene;

    // True while a hold-0 switch is in force ("staying - switch back
    // yourself"): further alerts must not continue that chain (BUG-67).
    private bool _stayMode;

    // One-line status for the Alerts tab (marshaled to the UI by the subscriber).
    public event Action<string>? Status;

    public ObsAutoSwitchService(AppConfig config,
        Func<Task<string?>> getCurrentScene, Func<string, Task> switchScene)
    {
        _config = config;
        _getCurrentScene = getCurrentScene;
        _switchScene = switchScene;
    }

    // Called for every alert (real or test - the test buttons ride the same
    // pipeline, so what Kat verifies is exactly what a real raid does).
    // Never throws: a scene switch must not break the alert pipeline.
    public async Task HandleAlertAsync(AlertEvent alert)
    {
        try
        {
            await HandleAsync(alert);
        }
        catch (Exception ex)
        {
            Note($"OBS auto-switch failed: {Short(ex.Message)}");
        }
    }

    private async Task HandleAsync(AlertEvent alert)
    {
        var cfg = _config.ObsAutoSwitch;
        if (!cfg.Enabled) return;

        (string Scene, int Hold)? mapping = alert.Type switch
        {
            AlertType.Raid => NonEmpty(cfg.RaidScene, cfg.RaidHoldSeconds),
            AlertType.Sub => NonEmpty(cfg.SubScene, cfg.SubHoldSeconds),
            AlertType.Follow => NonEmpty(cfg.FollowScene, cfg.FollowHoldSeconds),
            _ => null,
        };
        if (mapping is not { } target) return;
        string label = alert.Type switch
        {
            AlertType.Raid => "raid",
            AlertType.Sub => "sub",
            _ => "follow",
        };

        await _gate.WaitAsync();
        try
        {
            // Same target again (a follow burst): refresh the hold timer
            // instead of stacking another one.
            if (string.Equals(_activeTarget, target.Scene, StringComparison.OrdinalIgnoreCase))
            {
                if (target.Hold > 0) StartHold(target.Scene, target.Hold);
                return;
            }

            // BUG-67: hold 0 promised "staying - switch back yourself", but a
            // later different alert kept the chain alive: it switched away and,
            // at ITS return, yanked program back to the scene from BEFORE the
            // hold-0 switch - contradicting the status line. While the hold-0
            // target is still on program, further alerts are ignored; once the
            // user has switched somewhere by hand, the state is stale - clear
            // it and treat this alert as a fresh chain.
            if (_stayMode)
            {
                string? current = null;
                try { current = await _getCurrentScene(); } catch { }
                if (string.Equals(current, _activeTarget, StringComparison.OrdinalIgnoreCase))
                {
                    Note($"{label} ignored - staying on '{_activeTarget}' (hold 0) until you switch back in OBS");
                    return;
                }
                if (current != null)
                {
                    _activeTarget = null;
                    _returnScene = null;
                    _stayMode = false;
                }
                // current == null (OBS unreachable): keep the stay - neither the
                // switch nor the return could be verified right now.
            }

            // First switch of a chain: remember what is on program now.
            if (_activeTarget == null)
            {
                try { _returnScene = await _getCurrentScene(); }
                catch { _returnScene = null; }
            }

            try
            {
                await _switchScene(target.Scene);
            }
            catch (Exception ex)
            {
                Note($"{label}: could not switch OBS to '{target.Scene}' ({Short(ex.Message)})");
                return;
            }
            _activeTarget = target.Scene;
            _stayMode = target.Hold == 0;
            Note(target.Hold > 0
                ? $"{label} -> OBS scene '{target.Scene}' (back in {target.Hold}s" +
                  (_returnScene is { Length: > 0 } r ? $", then '{r}')" : ")")
                : $"{label} -> OBS scene '{target.Scene}' (staying - switch back yourself)");

            if (target.Hold > 0) StartHold(target.Scene, target.Hold);
        }
        finally
        {
            _gate.Release();
        }
    }

    private (string Scene, int Hold)? NonEmpty(string scene, int hold) =>
        string.IsNullOrWhiteSpace(scene) ? null : (scene.Trim(), Math.Clamp(hold, 0, 600));

    // Cancels any pending return and starts a fresh one. A completed return
    // only actually switches back when the user has not changed scenes by
    // hand in the meantime - a manual scene change is respected and disarms it.
    private void StartHold(string scene, int holdSeconds)
    {
        _holdCts?.Cancel();
        _holdCts?.Dispose();
        var cts = new CancellationTokenSource();
        _holdCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(holdSeconds), cts.Token);
                await _gate.WaitAsync(cts.Token);
                try
                {
                    string? current = null;
                    try { current = await _getCurrentScene(); } catch { }
                    _activeTarget = null;
                    _stayMode = false;
                    if (current != null &&
                        !string.Equals(current, scene, StringComparison.OrdinalIgnoreCase))
                        return; // the user is somewhere else on purpose - leave it
                    string? back = _returnScene;
                    _returnScene = null;
                    if (string.IsNullOrWhiteSpace(back) ||
                        string.Equals(back, scene, StringComparison.OrdinalIgnoreCase))
                        return;
                    try
                    {
                        await _switchScene(back);
                        Note($"OBS back to '{back}'");
                    }
                    catch (Exception ex)
                    {
                        Note($"could not switch back to '{back}' ({Short(ex.Message)}) - stay on '{scene}'");
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Note($"OBS auto-switch return failed: {Short(ex.Message)}");
            }
        });
    }

    private void Note(string text) => Status?.Invoke($"[{DateTime.Now:HH:mm:ss}] {text}");

    private static string Short(string s)
    {
        s = s.ReplaceLineEndings(" ");
        return s.Length <= 90 ? s : s[..90] + "...";
    }

    public void Dispose()
    {
        _holdCts?.Cancel();
        _holdCts?.Dispose();
        _holdCts = null;
    }
}
