using Intertwine.Services.DTOs.Questions;
using Intertwine.Services.Interfaces.Services;
using Intertwine.Worker.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Intertwine.Worker.Tests;

public class DailyQuestionJobTests
{
    [Theory]
    [InlineData("2026-09-15T11:59:59Z", "2026-09-14")]
    [InlineData("2026-09-15T12:00:00Z", "2026-09-15")]
    [InlineData("2026-01-01T00:00:00Z", "2025-12-31")]
    public async Task CalculatesCalendarDateAtFixedUtcMinusTwelve(string utc, string expected)
    {
        var service = new Mock<IDailyQuestionService>();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse(utc));
        using var provider = new ServiceCollection().AddSingleton(service.Object).BuildServiceProvider();
        using var job = new DailyQuestionJob(provider.GetRequiredService<IServiceScopeFactory>(), clock);

        await job.RunAsync();

        service.Verify(x => x.EnsureDailyQuestionsAsync(DateOnly.Parse(expected), 7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SerializesOverlappingRunsAndReleasesGateAfterFailure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = new Mock<IDailyQuestionService>();
        service.Setup(x => x.EnsureDailyQuestionsAsync(It.IsAny<DateOnly>(), 7, It.IsAny<CancellationToken>()))
            .Returns(async (DateOnly date, int days, CancellationToken ct) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.SetResult();
                    await release.Task;
                    throw new InvalidOperationException("Simulated database failure");
                }
                return new EnsureDailyQuestionsResult(date, date.AddDays(days), 0, 8);
            });
        using var provider = new ServiceCollection().AddSingleton(service.Object).BuildServiceProvider();
        using var job = new DailyQuestionJob(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        var first = job.RunAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = job.RunAsync();
        Assert.False(second.IsCompleted);
        Assert.Equal(1, calls);
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RunsOnStartupAndAtTwentyFourHoursEvenAfterFailure()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-15T12:00:00Z"));
        var firstRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = new Mock<IDailyQuestionService>();
        service.Setup(x => x.EnsureDailyQuestionsAsync(It.IsAny<DateOnly>(), 7, It.IsAny<CancellationToken>()))
            .Returns((DateOnly date, int days, CancellationToken ct) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstRun.SetResult();
                    throw new InvalidOperationException("Simulated transient failure");
                }
                secondRun.TrySetResult();
                return Task.FromResult(new EnsureDailyQuestionsResult(date, date.AddDays(days), 0, 8));
            });
        using var provider = new ServiceCollection().AddSingleton(service.Object).BuildServiceProvider();
        using var job = new DailyQuestionJob(provider.GetRequiredService<IServiceScopeFactory>(), clock);
        using var background = new DailyQuestionBackgroundService(job, clock, NullLogger<DailyQuestionBackgroundService>.Instance);
        await background.StartAsync(CancellationToken.None);
        await firstRun.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromHours(1));
        await secondRun.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await background.StopAsync(CancellationToken.None);
        Assert.Equal(2, calls);
    }
}
