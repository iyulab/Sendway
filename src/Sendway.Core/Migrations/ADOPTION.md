# Migrations adoption — one-time production cutover (human-executed, not automated)

## Status

**Done (2026-08-25)**: steps 1–3 below are complete. The live `sendway-pg` schema was diffed against
`InitialCreate.cs` (exact match — 3 tables, same columns/PK/unique index), `__EFMigrationsHistory` was
created and seeded with `20260824143840_InitialCreate`/`8.0.11`, and `ChannelCredentialSeeder.StartAsync`
now branches on `Sendway:DatabaseProvider` (`Sqlite` → `EnsureCreatedAsync`, everything else →
`MigrateAsync`) so the Npgsql-generated migration never runs against the Sqlite test provider.
**Not yet in production** — this code change is a local commit only; it takes effect on the next deploy
(tracked with the pending `origin` push, see `claudedocs/HANDOFF.md`). Until that deploy, the running
service still calls `EnsureCreatedAsync()`, which is a safe no-op against the now-marked-baseline schema.

## What this is

`InitialCreate` (`20260824143840_InitialCreate.cs`) is a **baseline** migration generated from the
current `SendwayDbContext` model — it recreates exactly the three tables (`Tenants`,
`ChannelCredentials`, `MessageRecords`) that `ChannelCredentialSeeder`'s `EnsureCreatedAsync()` call
already produces today. Adding EF Core Migrations tooling and this baseline file changes nothing
about how the running service behaves — `ChannelCredentialSeeder` still calls `EnsureCreatedAsync()`,
not `MigrateAsync()`. This file only prepares the ground.

## Why this exists

`EnsureCreatedAsync()` only creates a schema when the target database has **no tables at all**. Once
a database exists with those tables already in it — true for the live `sendway-pg` deployment since
2026-08-24 — calling it again is a silent no-op. It does **not** compare the current model against
the database and does **not** apply any difference. Any future change to `SendwayDbContext` (a new
column, a new table — e.g. a durable idempotency-key store or a durable retry queue) will build and
run locally against a fresh SQLite test database without complaint, but will **not** reach the
already-existing production database on deploy — the app will start, then fail at runtime the first
time it queries the missing column/table. There is currently no tooling gap warning for this; it
would surface only as a production incident.

## The cutover (do this once, deliberately — not part of any automated deploy)

1. **Confirm the baseline matches production exactly.** It was generated from the current model, so
   it should already match — but before touching production, diff `InitialCreate.cs`'s `Up()` against
   the actual live schema (`\d` per table in `psql`, or `az postgres flexible-server`... connect and
   inspect) to be sure nothing has drifted.
2. **Mark the baseline as already-applied, without running it** — the tables already exist, so running
   `Up()` as-is would try to `CREATE TABLE` on top of existing tables and fail. Against the production
   database:
   ```sql
   CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
       "MigrationId" character varying(150) NOT NULL,
       "ProductVersion" character varying(32) NOT NULL,
       CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
   );

   INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
   VALUES ('20260824143840_InitialCreate', '8.0.11')
   ON CONFLICT DO NOTHING;
   ```
3. **Only after step 2 has landed**, change `ChannelCredentialSeeder.StartAsync`
   (`src/Sendway.Service/ChannelCredentialSeeder.cs`) from `db.Database.EnsureCreatedAsync(...)` to
   `db.Database.MigrateAsync(...)`, and deploy. From that point on, any future `dotnet ef migrations
   add` runs automatically on the next deploy — no more manual steps.

Doing step 3 before step 2 has landed would make the very next deploy try to `CREATE TABLE` on tables
that already exist and crash the service on startup — the order above is not optional.

## Why this isn't done automatically

Step 2 runs a one-time write against the live production database outside of any deploy pipeline —
that's a real-infrastructure action a human should execute and verify directly, the same way a
`git push` to `origin/main` on this repo needs separate confirmation each time (see
`claudedocs/HANDOFF.md`). This document is the handoff for that action; nothing in the codebase
performs it on its own.
