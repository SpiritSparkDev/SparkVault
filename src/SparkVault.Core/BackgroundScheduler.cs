namespace SparkVault.Core;

public sealed class BackgroundScheduler : IAsyncDisposable
{
    private readonly JobRepository _jobRepository;
    private readonly RunRepository _runRepository;
    private readonly BackupRunner _runner;
    private readonly Func<BackupJob, IBackupTarget> _targetFactory;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    public BackgroundScheduler(
        JobRepository jobRepository,
        RunRepository runRepository,
        BackupRunner runner,
        Func<BackupJob, IBackupTarget> targetFactory,
        TimeSpan pollInterval)
    {
        _jobRepository = jobRepository;
        _runRepository = runRepository;
        _runner = runner;
        _targetFactory = targetFactory;
        _timer = new PeriodicTimer(pollInterval);
        _loopTask = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token))
            {
                foreach (var job in _jobRepository.GetAll())
                {
                    var lastRun = _runRepository.GetLatestByJobId(job.Id);
                    if (ScheduleCalculator.IsDue(job, lastRun?.StartedAt, DateTime.UtcNow))
                    {
                        await _runner.RunAsync(job, _targetFactory(job), progress: null, _cts.Token);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on disposal
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _timer.Dispose();
        try
        {
            await _loopTask;
        }
        catch (OperationCanceledException)
        {
        }
        _cts.Dispose();
    }
}
