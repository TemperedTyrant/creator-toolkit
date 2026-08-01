# AGENTS.md

## Repository purpose

TemperedTyrant Creator Toolkit is a free, open-source, modular self-hosted toolkit
for creators and their teams.

`Self-hosted tools and automation for creators and their teams.`

Creator Announcements will be the first implemented and released module.
Version 1 detects creator events, prepares announcements, and publishes them
reliably to configured destinations. Do not expand version 1 into a
general-purpose creator suite or an enterprise social-media management product.

The project is licensed under `AGPL-3.0-only`.

## Current status

Milestone 1 application foundation is complete through security hardening.

The next planned product milestone is Creator Announcements authoring. Only
implement behavior explicitly authorized by the active checkpoint.

Do not begin destinations, external publishing, durable publishing jobs,
scheduling, event sources, or provider integrations until a later checkpoint
explicitly authorizes them.

## Repository layout

Preserve this high-level layout:

```text
.
├── src/
│   ├── TemperedTyrant.CreatorToolkit.Web/
│   ├── TemperedTyrant.CreatorToolkit.Core/
│   └── TemperedTyrant.CreatorToolkit.Infrastructure/
├── tests/
│   ├── TemperedTyrant.CreatorToolkit.UnitTests/
│   └── TemperedTyrant.CreatorToolkit.IntegrationTests/
├── docs/
│   └── DECISIONS/
├── compose.yaml
└── TemperedTyrant.CreatorToolkit.slnx
```

Use repository-relative paths in commands and documentation.

The application is a single deployable modular monolith. Multiple .NET
projects enforce logical boundaries; they do not represent separate
applications, services, processes, or containers.

The root namespace is `TemperedTyrant.CreatorToolkit`. The CLI and executable
identifier is `creator-toolkit`.

## Verification commands

Run applicable commands from the repository root:

```sh
dotnet restore

dotnet build \
  --no-restore \
  --disable-build-servers \
  -m:1

dotnet test \
  --no-build \
  --disable-build-servers \
  -m:1

dotnet format \
  --verify-no-changes \
  --no-restore

dotnet ef migrations has-pending-model-changes \
  --project src/TemperedTyrant.CreatorToolkit.Infrastructure \
  --startup-project src/TemperedTyrant.CreatorToolkit.Web

dotnet list TemperedTyrant.CreatorToolkit.slnx package \
  --vulnerable \
  --include-transitive

docker compose config --quiet
docker compose build
git diff --check
```

Run the repository's production-package deprecation check when production
dependencies change.

Actual `linux/amd64` and `linux/arm64` application builds are required through
GitHub Actions. Run equivalent Buildx validation locally when Buildx is
available. If a command is unavailable, report that fact rather than claiming
it passed or silently substituting a narrower check.

## Branch and dependency baseline

Before starting a new checkpoint:

1. Confirm the worktree is clean.
2. Switch to `main`.
3. Run `git pull --ff-only`.
4. Confirm local `main` matches `origin/main`.
5. Create a new feature branch from the updated `main`.

Treat `Directory.Packages.props`, `global.json`, and project files on the
updated `main` branch as the dependency and toolchain source of truth.

Do not downgrade, replace, or pin a dependency to an older version unless the
user explicitly requests it or a demonstrated compatibility defect requires
it. Stop and report before making any dependency downgrade.

Ignore generated dependency information under `bin/` and `obj/`. Clean and
restore generated outputs when dependency inspection produces stale results.

Do not mix unrelated dependency upgrades into a product checkpoint.

## Architecture and dependency rules

- Target .NET 10 LTS, ASP.NET Core Razor Pages, ASP.NET Core Identity,
  Entity Framework Core, SQLite, and ASP.NET Core hosted background services.
- Keep one application container, one named persistent volume, and one process
  boundary for version 1. Do not introduce PostgreSQL, Redis, Temporal, Kafka,
  or Kubernetes.
- Do not introduce a separate worker service, process, or container. When
  background business processing is implemented, run it through ASP.NET Core
  hosted services inside the application process.
- Preserve modular-monolith boundaries. Core domain and application code must
  not depend on Discord, Bluesky, or another provider.
- Keep hosting and Razor Pages in Web, provider-neutral domain and application
  behavior in Core, and persistence and provider adapters in Infrastructure.
- When provider behavior is introduced, keep it behind provider-neutral
  connector or trigger-source interfaces. Preserve the creator-event/action
  seam without introducing a generic workflow engine prematurely.
- When durable publishing jobs are introduced, use SQLite-backed leasing,
  bounded action-specific retries, crash recovery, idempotency safeguards, and
  graceful hosted-service shutdown.
- Use ASP.NET Core Identity for users, password hashing, recovery tokens,
  security stamps, roles, and cookie integration. Do not create custom password
  hashing, authentication cookies, or authentication token formats.
- Enforce authorization in server-side handlers and application services using
  centralized policies. Hiding a control in the UI is never sufficient.
- Prefer .NET and ASP.NET Core framework functionality over third-party
  packages. Add a dependency only when it has a clear need, a compatible
  open-source license, active maintenance, multi-architecture support where
  relevant, and no paid hosted-service requirement.
- Record architecture-significant changes as ADRs in `docs/DECISIONS/`.
- Preserve the creator-event/action seam in ADR 0005 without implementing a
  generic workflow engine in version 1.
- Do not implement X or policy-bypassing browser automation. Reddit remains
  deferred until a permitted, free, and repeatable setup is confirmed.

## Persistence and migrations

Add or modify persisted entities only when the active checkpoint explicitly
authorizes the schema change.

For every authorized EF model change:

- add a reviewed migration
- inspect generated SQL and migration operations
- test migration from the previous committed schema
- test fresh database creation
- test persistence across application restart
- update model and deployment documentation where relevant

Unexpected EF model drift is a blocker. Do not create an incidental migration
merely to make the drift check pass.

## Security constraints

- Never commit secrets. Keep `.env`, databases, key rings, backups, tokens, and
  local data out of Git.
- Never hard-code a username, host path, private domain, IP address, credential,
  token, developer-specific port, or installation-specific assumption. Runtime paths, bind
  addresses, ports, public URLs, and similar settings must be portable and
  configurable.
- Store connector credentials encrypted at rest. Never allow a stored secret to
  be retrieved through the UI or API after entry.
- A new installation must require a short-lived, single-use bootstrap token
  generated by an explicit administrative CLI command. Print it only to that
  command's terminal, never to routine or structured application logs.
- The sole Owner cannot be deleted, disabled, or demoted except through an
  atomic ownership transfer.
- Treat webhook delivery as an SSRF surface. Apply scheme, DNS, address,
  redirect, timeout, size, and private-network allowlist controls described in
  `docs/SECURITY.md`.
- Use parameterized database access through EF Core. Apply antiforgery,
  validation, output encoding, secure-cookie, login-throttling, and safe
  forwarded-header practices.
- Do not weaken a security invariant to simplify a connector or deployment.

## Logging, errors, and redaction

Normal pages may show only a clear status, concise user-facing error,
recommended corrective action, and diagnostic reference ID.

Technical details belong in structured console logs and the authenticated Debug
page. Use structured fields rather than interpolating whole request or response
objects. Never log or export:

- passwords, password reset values, bootstrap/setup/recovery tokens;
- OAuth access or refresh tokens, Bluesky app passwords, or API credentials;
- authorization or cookie headers;
- webhook URLs, query strings, or custom secret header values;
- Data Protection keys, encryption material, or database contents;
- provider payload fields not explicitly classified as safe.

Redact at the source. Do not rely on a later log processor to remove secrets.
Diagnostic export schemas must use allowlists, not blocklists.

## Change discipline

- Keep changes scoped, reviewable, and related to the requested behavior.
- Preserve unrelated user changes and do not reformat unrelated files.
- Update documentation and tests whenever behavior, configuration, security
  boundaries, provider behavior, or user-visible workflows change.
- Add positive and negative authorization tests for every protected operation.
- Add failure-isolation, retry, idempotency, redaction, and architecture tests
  when those concerns are relevant to the changed behavior.
- Do not describe planned functionality as implemented.
- If a request conflicts with the product hard requirements, security model,
  provider policy, or an accepted ADR, stop and report the conflict. Do not
  silently weaken the requirement.

## Verification before completion

Before declaring work complete:

1. Run the applicable restore, build, test, formatting, and Compose checks.
2. Verify new behavior with both success and failure cases.
3. Verify server-side authorization for every affected role.
4. Check logs, errors, Debug output, and exports for secret leakage.
5. When work affects idempotent actions, events, jobs, or publishing, confirm
   duplicate inputs cannot duplicate effects.
6. When work affects multiple destinations, confirm one destination failure
   cannot block or roll back another destination.
7. Search changed content for machine-specific paths, identities, addresses,
   credentials, and accidental secrets.
8. Confirm documentation, tests, and ADRs reflect the final behavior.
9. Report commands run, results, and any checks that could not be performed.

## GitHub pull-request workflow

When explicitly instructed to complete a checkpoint through GitHub, the agent
is authorized to:

- commit the reviewed checkpoint changes
- push the current feature branch
- create a draft pull request targeting `main`
- update the existing pull request for the current branch
- monitor pull-request checks
- inspect failed GitHub Actions logs
- make narrowly scoped corrections for failures caused by the checkpoint
- commit and push those corrections
- repeat validation until all required checks pass or a genuine blocker is
  identified

The agent must:

- use the authenticated GitHub CLI rather than asking the user to copy routine
  check output
- detect an existing pull request before creating another one
- create pull requests as drafts
- use the repository pull-request template
- preserve immutable GitHub Action pins
- report the PR URL, check results, corrections, and remaining risks
- stop for user review after all checks pass

The agent must never:

- merge a pull request
- enable auto-merge
- mark a draft pull request ready for review
- close a pull request
- force-push or rewrite published history
- delete local or remote branches
- modify GitHub repository settings, rulesets, permissions, secrets, variables,
  environments, security settings, or webhooks
- retrieve or print GitHub authentication tokens
- run `gh auth token`
- inspect GitHub CLI credential-storage files
- expose workflow secrets, credentials, cookies, capabilities, database files,
  Data Protection keys, or `.env` contents
- weaken or bypass a required validation check

## Pull request descriptions

Keep pull request descriptions very brief.

Use:

```markdown
## Changes

- Describe the first meaningful change.
- Describe the second meaningful change when applicable.
```

Include only what the pull request changes. Do not include test results,
verification commands, security-review details, implementation history,
deferred work, risk sections, or checklist boilerplate unless explicitly
requested by the user.
