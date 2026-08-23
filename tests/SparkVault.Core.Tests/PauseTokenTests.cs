using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class PauseTokenTests
{
    [Fact]
    public async Task WaitIfPausedAsync_NotPaused_CompletesImmediately()
    {
        var token = new PauseToken();
        await token.WaitIfPausedAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task WaitIfPausedAsync_Paused_BlocksUntilResume()
    {
        var token = new PauseToken();
        token.Pause();

        var wait = token.WaitIfPausedAsync(CancellationToken.None);

        await Task.Delay(50);
        Assert.False(wait.IsCompleted);

        token.Resume();
        await wait.WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.True(wait.IsCompleted);
    }

    [Fact]
    public async Task WaitIfPausedAsync_Cancelled_ThrowsOperationCanceled()
    {
        var token = new PauseToken();
        token.Pause();
        using var cts = new CancellationTokenSource();
        var wait = token.WaitIfPausedAsync(cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }
}
