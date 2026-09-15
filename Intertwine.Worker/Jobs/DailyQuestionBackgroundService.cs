namespace Intertwine.Worker.Jobs;

public sealed class DailyQuestionBackgroundService(
    DailyQuestionJob job,
    TimeProvider timeProvider,
    ILogger<DailyQuestionBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24), timeProvider);
        do
        {
            try
            {
                var result = await job.RunAsync(stoppingToken);
                logger.LogInformation(
                    "Daily Questions ensured for {StartDate} through {EndDate}: {Created} created, {AlreadyExisted} existing.",
                    result.StartDate, result.EndDate, result.Created, result.AlreadyExisted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Daily Question assignment failed. The next scheduled run or a manual request can retry.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
