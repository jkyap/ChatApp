# ChatApp — 1-to-1 messaging (full stack, .NET 10)

Take-home implementation of the Senior Full Stack Engineer assessment:
a self-hosted, private 1-to-1 messaging web app.

## Run

```bash
# local dev
dotnet run --project src/ChatApp.Web        # http://localhost:5169 (launch profile)

# docker compose (bind mount keeps the data across restarts)
docker compose up --build                   # http://localhost:8080

# plain docker
docker build -t chatapp .
docker run -d --name chatapp -p 8080:8080 -v chatappdata:/data chatapp
```

Open two browser windows, register two accounts, chat.

## Where the data lives

Two files: `chat.db` (accounts + messages) and `auth.key` (signs login
tickets). The folder is set by the environment variable `CHATAPP_DATA_DIR`
(the Docker image uses `/data`, mapped to a host folder in docker-compose);
without it, an `appdata` folder next to the app is used.

Run one container per data folder — the embedded database assumes one writer.

## How it works

The browser asks the server for updates every 2.5 seconds (polling). No
persistent connection, no push. The server database is the only source of
truth; correctness does not depend on timing.

Libraries used only for supporting roles: ASP.NET
Core, Microsoft.Data.Sqlite, built-in crypto. 
no external services — runs fully locally.

## Why messages are never lost or duplicated

| Problem | Solution |
|---|---|
| Request fails / laptop closed / server restarted | Each message has a number (1, 2, 3...) per conversation; the browser saves the highest it saw. Next poll asks "what's newer?" and catches up. |
| Duplicate on retry | Each send carries a browser-made unique id; the database stores any id at most once and returns the original. |
| Out of order | Numbers are assigned in one database transaction, so two sends can't claim the same one. |

## Known limits, stated not hidden

- **One server only.** Message numbering is in-process; the unique index
  prevents corruption but not a lost write if you ever run two.
- **Login tickets can't be revoked** for one user early (7-day expiry).
  Emergency lever: delete `auth.key`, everyone is logged out.
- **Anyone can message anyone** (no contact requests).
- **No rate limiting** on the API; add one before exposing it publicly.

## Layout

```
src/ChatApp.Web/
  Auth/        TokenProtector (login tickets), AuthEndpoints (the API)
  Data/        ChatDb (all storage)
  wwwroot/     the web page + client logic (app.js)
Dockerfile     two-stage build, runs as non-root user
docker-compose.yml
```
