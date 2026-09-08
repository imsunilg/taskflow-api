# TaskFlow API

ASP.NET Core 9 Web API, Clean Architecture (Api / Application / Domain / Infrastructure),
EF Core 9 + PostgreSQL, MediatR CQRS, FluentValidation, JWT auth, SignalR, Hangfire.

## Run locally

```bash
# PostgreSQL must be reachable (see ConnectionStrings:Postgres in appsettings.json)
dotnet run --project src/TaskManager.Api      # http://localhost:5095
```

On startup (Development) the DB is migrated and seeded with a demo user:

| Email | Password |
|---|---|
| `demo@taskflow.app` | `password123` |

- Swagger UI: `/swagger` (JWT "Authorize" button — paste the token from `/api/auth/login`)
- Health: `/health` · Hangfire dashboard (dev only): `/hangfire`

**CORS:** every configured origin (`ALLOWED_ORIGIN`) plus any `http(s)://localhost:<port>`
/ `127.0.0.1` origin is allowed (credentials enabled). Non-localhost origins are rejected.

## Migrations

```bash
dotnet ef migrations add <Name> --project src/TaskManager.Infrastructure --startup-project src/TaskManager.Api --output-dir Persistence/Migrations
dotnet ef database update       --project src/TaskManager.Infrastructure --startup-project src/TaskManager.Api
```

## Tests

```bash
dotnet test tests/TaskManager.UnitTests          # 32 unit tests (handlers, behaviors, extractor, recurrence, envelope)
dotnet test tests/TaskManager.IntegrationTests   # 5 API tests via WebApplicationFactory (needs local PostgreSQL; auto-skips if absent)
```

Performance: `k6 run tests/performance/tasks-load-test.js` (see the file header for the seed stage).

## Config / secrets

`appsettings.json` + environment variables. `JWT_SECRET`, `ALLOWED_ORIGIN`,
`ConnectionStrings__Postgres`, `SpeechToText__Provider`
(`Stub` default, or `Cloud` + `SpeechToText__Endpoint` / `SpeechToText__ApiKey`).

**Caching:** the category/tag lookups use the built-in **in-process (system) memory
cache** by default — no Redis required. Redis is opt-in: set
`ConnectionStrings__Redis` and it switches automatically (startup logs
`Lookup cache backend: InMemory | Redis`, and `/health` gains a `redis` check).

See `api-contract.md` for the full endpoint reference.
