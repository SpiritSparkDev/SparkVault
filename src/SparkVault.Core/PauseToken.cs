namespace SparkVault.Core;

public sealed class PauseToken
{
    private TaskCompletionSource<bool>? _pauseGate;

    public bool IsPaused => _pauseGate is not null;

    public void Pause() => _pauseGate ??= new TaskCompletionSource<bool>();

    public void Resume()
    {
        var gate = _pauseGate;
        _pauseGate = null;
        gate?.TrySetResult(true);
    }

    public Task WaitIfPausedAsync(CancellationToken ct) =>
        _pauseGate is { } gate ? gate.Task.WaitAsync(ct) : Task.CompletedTask;
}
