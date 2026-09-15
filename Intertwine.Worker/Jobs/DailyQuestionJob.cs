using Intertwine.Services.DTOs.Questions;
using Intertwine.Services.Interfaces.Services;

namespace Intertwine.Worker.Jobs;

/// <summary>Coordinates the timer and HTTP trigger within this host.</summary>
public sealed class DailyQuestionJob(IServiceScopeFactory scopeFactory, TimeProvider timeProvider) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<EnsureDailyQuestionsResult> RunAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Resolve both the date and scoped dependencies after waiting for any preceding run.
            var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().ToOffset(TimeSpan.FromHours(-12)).DateTime);
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IDailyQuestionService>();
            return await service.EnsureDailyQuestionsAsync(today, 7, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
