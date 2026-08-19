using Serilog;

namespace SparkVault.Core;

public sealed class BackgroundScheduler : IAsyncDisposable
{
    private readonly JobRepository _jobRepository;
    private readonly RunRepository _runRepository;
    private readonly BackupRunner _runner;
    private readonly Func<BackupJob, IBackupTarget> _targetFactory;
    private readonly ILogger _logger;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    public BackgroundScheduler(
        JobRepository jobRepository,
        RunRepository runRepository,
        BackupRunner runner,
        Func<BackupJob, IBackupTarget> targetFactory,
        TimeSpan pollInterval,
        ILogger logger)
    {
        _jobRepository = jobRepository;
        _runRepository = runRepository;
        _runner = runner;
        _targetFactory = targetFactory;
        _logger = logger;
        _timer = new PeriodicTimer(pollInterval);
        _loopTask = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token))
            {
                foreach (var job in GetJobsSafely())
                {
                    try
                    {
                        var lastRun = _runRepository.GetLatestByJobId(job.Id);
                        // Both arguments must live in the same time frame; runs are stored as UTC.
                        if (ScheduleCalculator.IsDue(job, lastRun?.StartedAt.ToLocalTime(), DateTime.Now))
                        {
                            await _runner.RunAsync(job, _targetFactory(job), progress: null, _cts.Token);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.Error(ex, "Scheduler tick failed for job {JobId}", job.Id);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on disposal
        }
    }

    private IReadOnlyList<BackupJob> GetJobsSafely()
    {
        try
        {
            return _jobRepository.GetAll();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Scheduler could not load jobs");
            return Array.Empty<BackupJob>();
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
