using System.Globalization;
using System.Threading.RateLimiting;
using Intertwine.Repositories.Caching;
using Intertwine.Repositories.Data;
using Intertwine.Repositories.Repositories;
using Intertwine.Services.Interfaces.Repositories;
using Intertwine.Services.Interfaces.Services;
using Intertwine.Services.Services;
using Intertwine.Worker.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<IntertwineDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Configure ConnectionStrings:DefaultConnection for the worker.")));
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("RedisConnection")
        ?? throw new InvalidOperationException("Configure ConnectionStrings:RedisConnection for the worker.")));
builder.Services.AddScoped<IDailyQuestionRepository, DailyQuestionRepository>();
builder.Services.AddScoped<IDailyQuestionCache, RedisDailyQuestionCache>();
builder.Services.AddScoped<IDailyQuestionService, DailyQuestionService>();
builder.Services.AddSingleton<DailyQuestionJob>();

if (builder.Configuration.GetValue("DailyQuestions:ScheduleEnabled", true))
    builder.Services.AddHostedService<DailyQuestionBackgroundService>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("daily-question-ensure", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 2,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter =
                Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);

        await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many requests",
            Detail = "Daily Question assignment allows two requests per minute per client IP."
        }, cancellationToken);
    };
});

var app = builder.Build();
app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
    app.MapOpenApi();
app.UseRouting();
app.UseRateLimiter();
app.MapControllers();
app.Run();

public partial class Program;
