using System.Net;
using System.Net.Http.Json;
using Intertwine.Domain.Entities;
using Intertwine.Repositories.Data;
using Intertwine.Services.DTOs.Questions;
using Intertwine.Services.Interfaces.Repositories;
using Intertwine.Worker.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Intertwine.Worker.Tests;

public class WorkerEndpointTests
{
    [Fact]
    public async Task AnonymousPostCreatesEightDatesAndRecreatesOnlyDeletedAssignments()
    {
        await using var factory = new WorkerFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsync("/api/daily-questions/ensure", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<EnsureDailyQuestionsResult>();
        Assert.Equal(new EnsureDailyQuestionsResult(new(2026, 9, 14), new(2026, 9, 21), 8, 0), result);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IntertwineDbContext>();
            await db.DailyQuestions.Where(x => x.Date == new DateOnly(2026, 9, 17)).ExecuteDeleteAsync();
        }

        var retry = await client.PostAsync("/api/daily-questions/ensure", null);
        var repaired = await retry.Content.ReadFromJsonAsync<EnsureDailyQuestionsResult>();
        Assert.Equal(1, repaired!.Created);
        Assert.Equal(7, repaired.AlreadyExisted);
        factory.Cache.Verify(x => x.RemoveAsync(new DateOnly(2026, 9, 17), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ThirdPostIsRateLimitedButAnotherIpHasItsOwnAllowance()
    {
        await using var factory = new WorkerFactory();
        using var client = factory.CreateClient();
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/daily-questions/ensure", null)).StatusCode);

        var rejected = await client.PostAsync("/api/daily-questions/ensure", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
        client.DefaultRequestHeaders.Add("Test-Client-IP", "192.0.2.2");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/daily-questions/ensure", null)).StatusCode);
    }

    [Fact]
    public async Task EndpointDoesNotAllowGetToTriggerWrites()
    {
        await using var factory = new WorkerFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.MethodNotAllowed,
            (await client.GetAsync("/api/daily-questions/ensure")).StatusCode);
    }

    private sealed class WorkerFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        public Mock<IDailyQuestionCache> Cache { get; } = new();

        public WorkerFactory()
        {
            _connection.Open();
            _connection.CreateFunction("GETUTCDATE", () => "2026-09-15 11:59:00");
            using var db = CreateContext();
            db.Database.EnsureCreated();
            db.Questions.AddRange(
                new Question { QuestionId = 1, IsActive = true, QuestionTitle = "One", FullQuestion = "One?" },
                new Question { QuestionId = 2, IsActive = true, QuestionTitle = "Two", FullQuestion = "Two?" });
            db.SaveChanges();
        }

        private IntertwineDbContext CreateContext() => new(
            new DbContextOptionsBuilder<IntertwineDbContext>().UseSqlite(_connection).Options);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                var scheduler = services.Single(x => x.ServiceType == typeof(IHostedService)
                    && x.ImplementationType == typeof(DailyQuestionBackgroundService));
                services.Remove(scheduler);
                services.RemoveAll<IntertwineDbContext>();
                services.AddScoped(_ => CreateContext());
                services.RemoveAll<IDailyQuestionCache>();
                services.AddSingleton(Cache.Object);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(
                    new DateTimeOffset(2026, 9, 15, 11, 59, 0, TimeSpan.Zero)));
                services.AddSingleton<IStartupFilter, TestClientAddress>();
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    // This header is interpreted only by the test host; production trusts the connection address.
    private sealed class TestClientAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(
                    context.Request.Headers["Test-Client-IP"].FirstOrDefault() ?? "127.0.0.1");
                await nextMiddleware();
            });
            next(app);
        };
    }
}
