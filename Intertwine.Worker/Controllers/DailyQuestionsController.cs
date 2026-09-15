using Intertwine.Services.DTOs.Questions;
using Intertwine.Worker.Jobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Intertwine.Worker.Controllers;

[ApiController]
[Route("api/daily-questions")]
public sealed class DailyQuestionsController(DailyQuestionJob job) : ControllerBase
{
    /// <summary>Fills missing assignments from UTC-12 today through seven days ahead.</summary>
    [HttpPost("ensure")]
    [AllowAnonymous]
    [EnableRateLimiting("daily-question-ensure")]
    [ProducesResponseType<EnsureDailyQuestionsResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public Task<EnsureDailyQuestionsResult> Ensure(CancellationToken cancellationToken) =>
        job.RunAsync(cancellationToken);
}
