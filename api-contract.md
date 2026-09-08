# TaskFlow — REST API Contract

**Base:** `/api` · JSON · ISO-8601 UTC timestamps · Bearer auth on every route except `POST /api/auth/login`.

## Standard envelope

```jsonc
{
  "success": true,
  "message": "Task created successfully",
  "data": {},                // T | null
  "errors": [{ "field": "title", "message": "Title is required." }],
  "pagination": { "page": 1, "pageSize": 25, "total": 42, "totalPages": 2 } // or null
}
```

## Global rules

- List endpoints paginate: `page` (default 1), `pageSize` (default 25, **max 100**).
- Rate limiting: 60 req/min general; 10 req/min on `POST /api/voice/transcribe`.
- Audio upload: max 10 MB; MIME `audio/webm`, `audio/mp4`, `audio/wav`.
- `DELETE` is a soft delete (`is_deleted = true`).
- Refresh token is set/read as the `tf_refresh` httpOnly cookie.

## Auth

| Method | Path | Body | Notes |
|---|---|---|---|
| POST | `/api/auth/login` | `{ email, password }` | 200 → `{ accessToken, refreshToken, user }`; 401 on bad credentials |
| POST | `/api/auth/refresh` | – (cookie) | 200 → new token pair; 401 if cookie missing/expired |
| POST | `/api/auth/logout` | – | clears the refresh cookie |

## Tasks

| Method | Path | Notes |
|---|---|---|
| GET | `/api/tasks` | query: `page,pageSize,status,priority,categoryId,tagIds,dueDate,search,sort,groupBy` → `PagedResult<TaskResponse>` |
| POST | `/api/tasks` | `CreateTaskRequest` → 201 `TaskResponse`; 400 `errors[]` when `title` missing |
| GET | `/api/tasks/{id}` | full graph (subtasks, tags, reminder, recurrence, history); 404 if not owner |
| PUT | `/api/tasks/{id}` | `UpdateTaskRequest` (patch semantics); appends history per changed field |
| DELETE | `/api/tasks/{id}` | soft delete + history entry |
| POST | `/api/tasks/{id}/complete` | recurring tasks spawn the next occurrence |
| POST | `/api/tasks/{id}/reopen` | |
| POST | `/api/tasks/{id}/subtasks` | `{ text }` |
| PUT | `/api/tasks/{id}/subtasks/{subtaskId}` | `{ text?, isDone?, sortOrder? }` |
| DELETE | `/api/tasks/{id}/subtasks/{subtaskId}` | |

`CreateTaskRequest`: `title` (req, ≤500), `description` (≤5000), `dueDate`, `dueTime`,
`priority` (`Low|Medium|High|Urgent`), `categoryId`, `tagNames[]`, `subtaskTexts[]`,
`reminderOffsetMinutes`, `recurrenceFrequency` (`Daily|Weekdays|Weekly|Monthly|Yearly|Custom`),
`recurrenceInterval`, `notes`. Validation: `dueTime` requires `dueDate`; `reminderOffsetMinutes` requires both.

## Categories / Tags / Reminders

| Method | Path |
|---|---|
| GET/POST | `/api/categories`, PUT/DELETE `/api/categories/{id}` |
| GET/POST | `/api/tags`, DELETE `/api/tags/{id}` |
| GET/POST | `/api/reminders`, PUT/DELETE `/api/reminders/{id}` |

Category name is unique per user (400 otherwise). Reminder `firesAt` must be in the future.

## Calendar / Search / Notifications / Voice

| Method | Path | Notes |
|---|---|---|
| GET | `/api/calendar?from=YYYY-MM-DD&to=YYYY-MM-DD` | `TaskResponse[]` ordered by due date/time |
| GET | `/api/search?q=…&page=&pageSize=` | ILIKE over title/description/notes/tag names → `PagedResult<TaskResponse>`; `q` < 2 chars → empty |
| GET | `/api/notifications?page=&pageSize=` | `PagedResult<NotificationResponse>` |
| POST | `/api/notifications/{id}/read` | |
| POST | `/api/notifications/read-all` | |
| POST | `/api/voice/transcribe` | `multipart/form-data`: `audio` (file) **or** `text` (string, re-extract path) → `{ rawText, extractedFields }` |

## Realtime

- SignalR hub at `/hubs/notifications` (JWT via `access_token` query param).
- Server → client event: `ReceiveNotification(NotificationResponse)`.

## Errors

| Status | When |
|---|---|
| 400 | validation failure (`errors[]` populated) or domain rule violation |
| 401 | missing/invalid/expired access token, or bad login |
| 403 | authenticated but not the resource owner |
| 404 | resource missing or owned by another user |
| 429 | rate limit exceeded |
| 500 | unhandled — generic message in production, no stack trace |
