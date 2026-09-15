# Intertwine Worker API

Open `Intertwine.Worker.slnx` to work on the independently runnable worker API. It references the same Domain, Identity, Services, and Repositories projects as the main solution. `DailyQuestionService` owns assignment selection; `DailyQuestionRepository` owns database reads and inserts. Both the scheduled job and the manual endpoint call that service through one coordinator.

The worker runs immediately on startup, then every 24 hours while the process is running. Each run calculates today's calendar date at the fixed offset **UTC-12** and fills today through today + 7 days, inclusive (**eight dates**). For example, at `2026-09-15 11:59 UTC`, the range is September 14–21; at `12:00 UTC`, it becomes September 15–22. API clients continue requesting the question for their own local calendar date.

Dates are processed in order. Existing assignments are preserved. Selection follows these rules:

1. Only active questions can be selected. Questions never assigned as a Daily Question take priority.
2. Among categories with unused questions, choose the category with the fewest Daily Question assignments. Every linked category receives one count for each assignment; historical, future, and repeated assignments all count. Ties use `CategoryId`, then `QuestionId`.
3. If only uncategorized unused questions remain, select one by `QuestionId` before reusing any question.
4. After all active questions have been used, choose the question whose **most recent** assignment date is oldest, breaking ties by `QuestionId`. Insert a new assignment and preserve its older rows.
5. Recalculate selection after each insert. With no active questions available, the run fails and logs the error rather than inventing an assignment.

The unique index on `DailyQuestions.Date` prevents duplicate dates. A concurrent insert for an already-filled date is treated as existing. A shared in-process gate serializes the timer and manual calls. Deploy one worker instance; the date constraint protects duplicate dates across instances, but the local gate and rate limits are per process.

Each ensured date is evicted from Redis so a manually deleted and recreated assignment is visible through the main API immediately. An unsuccessful run can have committed earlier dates; rerunning fills the remaining gaps and retries cache eviction. A scheduled failure is logged and the next 24-hour tick retries; the manual endpoint can retry sooner. Restarting the process triggers an immediate run and starts a new timer.

## Folder layout

Keep the two solution folders beside each other:

```text
Codes/
├── Intertwine/
│   ├── Intertwine.slnx
│   └── Intertwine.Repositories/ (and the other shared projects)
└── Intertwine.Worker/
    ├── Intertwine.Worker.slnx
    ├── Intertwine.Worker/
    └── Intertwine.Worker.Tests/
```

The worker uses relative source project references into `../Intertwine`. Run the commands below from this worker solution folder. Clone/check out both folders with these names on another machine or in CI.

## Configure and run

Configure the worker's `ConnectionStrings:DefaultConnection` and `ConnectionStrings:RedisConnection` with the **same SQL Server database and Redis database** used by the main API. The SQL connection is intentionally blank in source control. For a macOS/Linux terminal:

```bash
export ConnectionStrings__DefaultConnection='YOUR_EXISTING_SQL_SERVER_CONNECTION_STRING'
export ConnectionStrings__RedisConnection='localhost:6379'
dotnet tool restore
dotnet ef database update --project ../Intertwine/Intertwine.Repositories --startup-project Intertwine.Worker
dotnet run --project Intertwine.Worker --launch-profile http
```

In PowerShell, set the environment variables with `$env:ConnectionStrings__DefaultConnection = '...'` and `$env:ConnectionStrings__RedisConnection = 'localhost:6379'`, then use the same `dotnet` commands.

Apply the migration before starting the worker. It adds `IX_DailyQuestions_Date`; it does not delete existing data. If legacy duplicate dates exist, review them before applying the migration:

```sql
SELECT [Date], COUNT(*) AS AssignmentCount
FROM DailyQuestions
GROUP BY [Date]
HAVING COUNT(*) > 1;
```

The worker listens at `http://localhost:5290`; the main API uses `http://localhost:5288`. Its development OpenAPI document is at `http://localhost:5290/openapi/v1.json`. Set `DailyQuestions__ScheduleEnabled=false` to run only the manual endpoint during a controlled demo; scheduling defaults to enabled.

## Manual demo endpoint

No bearer token or request body is required:

```bash
curl -i -X POST http://localhost:5290/api/daily-questions/ensure
```

Example successful response:

```json
{
  "startDate": "2026-09-15",
  "endDate": "2026-09-22",
  "created": 3,
  "alreadyExisted": 5
}
```

Run it once, delete a chosen assignment within the reported range in your demo database, and call it again. Only missing dates are filled; dates outside the current range are untouched. Inspect the result through `GET http://localhost:5288/api/questions/daily?localDate=YYYY-MM-DD`.

The endpoint permits **two requests per minute per client IP**, using a fixed window with no request queue. Further calls return `429 Too Many Requests` and a `Retry-After` header. Scheduled runs do not consume this allowance. The server calculates the date window; callers cannot request arbitrary dates or longer runs. GET requests cannot trigger assignment.

The limiter uses the connection's remote IP. When deploying behind a reverse proxy, configure forwarded headers for that known proxy before rate limiting so the limiter can distinguish client IPs; untrusted forwarded headers are not accepted by this project. Rate limiting bounds request frequency, while the endpoint intentionally remains anonymous and publicly callable.

## Verification

```bash
dotnet test Intertwine.Worker.slnx
```

This runs the shared Intertwine unit tests and the worker tests. Worker tests cover UTC-12 boundaries, startup and 24-hour scheduling, failure recovery, overlapping calls, repository queries, date uniqueness, and the anonymous endpoint's per-IP rate limit. HTTP/repository tests use temporary SQLite storage and a mocked cache. SQL Server-specific duplicate-key handling requires validation against SQL Server when deploying.
