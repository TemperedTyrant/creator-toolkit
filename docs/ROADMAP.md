# Roadmap

## How to use this roadmap

Each milestone should produce one or more focused, reviewable changes with tests
and documentation. A milestone's exit criteria must be met before later work
depends on it. Security, authorization, portability, redaction, and
failure-isolation requirements apply throughout rather than waiting for a final
hardening pass.

The roadmap is directional, not a promise of release dates.

## 1. Repository, application, identity, and security foundation

Deliver:

- .NET 10 solution with Razor Pages application, unit tests, and integration
  tests;
- module folders/boundaries, baseline configuration validation, EF Core,
  SQLite migrations, and a development Compose deployment;
- ASP.NET Core Identity with Owner, Admin, Editor, and Viewer roles;
- centralized server-side authorization policies and permission-matrix tests;
- locked uninitialized state, CLI-generated bootstrap credential, initial Owner
  wizard, and permanent setup closure;
- account setup links, security-stamp/session invalidation, sole-Owner
  invariants, atomic ownership transfer, and Owner recovery command;
- persisted Data Protection keys and an encrypted, replace-only secret store
  that never returns stored secret values;
- basic application-enforced append-only audit records for security-sensitive
  operations, explicitly without tamper evidence against direct data access;
- structured logging, diagnostic references, baseline redaction tests, and
  liveness/readiness endpoints;
- non-root, amd64/arm64-capable single-container build with one named volume.

Do not implement provider connectors.

Exit when a new installation cannot be claimed without an operator-generated
bootstrap credential, every placeholder protected operation is enforced
server-side for all roles, secret-store tests prove encryption and
non-retrievability, and application/database/container smoke tests pass.

## 2. Core announcements, approvals, and durable publishing

Deliver:

- normalized creator-event, template, platform-variant, workflow,
  announcement, approval, destination, delivery, attempt, and persistent-job
  models;
- workspace Editor publishing policy and revision-bound approval state machine;
- transactional event acceptance and work creation;
- unique idempotency constraints for events, commands, and deliveries;
- SQLite job leasing, crash recovery, bounded retry with jitter, terminal
  failure, and manual retry;
- one-job-per-destination failure isolation;
- previews through a fake connector and safe result classifications.

Exit when tests prove duplicate events do not duplicate deliveries, approval
cannot be bypassed, edits invalidate approval, crashes recover leased work, and
one destination failure does not block another.

## 3. Discord and generic outgoing-webhook destinations

Deliver Discord incoming-webhook:

- credential entry/replacement, connection testing, safe content validation,
  preview, publication, provider receipt handling, and rate-limit
  classification;
- safe mention defaults and independent delivery results.

Deliver generic outgoing HTTPS webhook:

- versioned request schema, credential replacement, connection test, and
  publication;
- HTTPS-only default, DNS/address validation, redirect revalidation, cloud
  metadata and private-address blocking, bounded sizes/timeouts/redirects;
- explicit advanced private hostname/CIDR allowlist;
- constrained encrypted custom secret headers and complete URL/header
  redaction.

Exit when connector contract tests, provider fakes, redaction tests, and SSRF
tests cover IPv4, IPv6, redirects, DNS changes, private allowlists, timeouts, and
oversized responses.

## 4. Bluesky destination

Deliver:

- a connection flow that explicitly requires a dedicated Bluesky app password
  and rejects guidance to enter a normal account password;
- supported AT Protocol authentication/session handling and record creation;
- handle/DID display, posting validation, preview, health, reconnect-required
  state, credential replacement, and safe provider errors;
- concurrency-safe credential/session refresh behavior where required.

OAuth is a later enhancement and is not required for localhost.

Exit when credentials cannot be retrieved or logged, tests cover expired and
replaced credentials, and posts use supported Bluesky mechanisms.

## 5. Manual and scheduled publishing

Deliver:

- complete draft, preview, approval, publish, cancel, and per-destination result
  interface;
- one configurable IANA workspace time zone;
- one-time and recurring schedules that preserve local intent and persist UTC
  execution instants;
- defined spring-gap and fall-overlap behavior;
- per-schedule Skip, HoldForReview, and PublishLatest missed-run policies, with
  HoldForReview as default;
- mobile-responsive schedule and history pages.

Exit when role tests cover direct and approval-required policies and time-zone
tests prove downtime never floods stale announcements.

## 6. Generic incoming trigger webhook

Deliver:

- event-source creation and replace-only source secret;
- versioned canonical HMAC request format;
- timestamp/skew validation, constant-time comparison, strict body limits,
  content-type validation, replay protection, and stable idempotency keys;
- durable acknowledgement, source health, actionable errors, and setup
  documentation.

Exit when forged, stale, replayed, duplicated, malformed, and oversized
requests cannot create duplicate or unauthorized work.

## 7. Twitch event source

First confirm the current supported, free, repeatable EventSub transport for a
self-hosted application, including authentication, subscription renewal,
revocation, reconnect, public URL implications, and rate/usage policies.

If the gate passes, deliver live-event normalization, duplicate protection,
health/reconnect behavior, and setup guidance. Do not use browser automation or
an unsupported transport.

Exit when a documented clean-account setup and simulated provider lifecycle are
repeatable without a paid service.

## 8. YouTube event source

First confirm a current supported and free approach—push or quota-safe
polling—for detecting published videos and relevant live events. Document API
project setup, quota behavior, renewal/polling, duplicate signals, callback
requirements, and degraded operation.

If the gate passes, deliver normalized events, idempotency, connection health,
and setup guidance.

Exit when the chosen design has repeatable free setup, bounded quota use, and
tests for duplicate and delayed notifications.

## 9. Setup, authorization, health, audit, and diagnostics hardening

Harden systems introduced in earlier milestones:

- complete guided setup, callback/public URL explanations, validation, and
  mobile/accessibility review;
- adversarial authorization and ownership-lifecycle review;
- account setup/recovery expiration and revocation UX;
- audit coverage and retention/export behavior;
- automatic connection checks, degraded/reconnect transitions, and no-silent-
  disconnect behavior;
- authenticated Debug page, sanitized diagnostic export, and seeded-secret
  redaction tests;
- backup/restore UI or CLI, compatibility checks, and disaster-recovery drill;
- media type, size, compatibility, and reachability checks if media support has
  entered scope.

This milestone does not introduce authentication, RBAC, first-run security, or
basic audit for the first time.

Exit after a security review, accessibility pass, restore drill, and
multi-provider failure exercise.

## 10. Public beta and GitHub release preparation

Deliver:

- reproducible linux/amd64 and linux/arm64 images from one revision;
- versioning, changelog, release notes, checksums/provenance where feasible, and
  source-code offer/link behavior required by AGPL;
- automated build, test, formatting, dependency, secret, license, migration,
  container, and smoke checks using open-source tools;
- issue and pull-request templates, private vulnerability reporting, support
  boundaries, and maintainer release checklist;
- clean-install, upgrade, rollback/recovery, proxy, localhost, and backup
  documentation validation;
- a public beta known-issues and provider-policy review.

Exit when a new user can follow only public documentation to install, claim,
configure, publish, diagnose, back up, and upgrade on both supported
architectures.

## Deferred and prohibited work

- Reddit remains deferred or explicitly experimental until a permitted, free,
  repeatable setup path is confirmed and documented.
- X is not implemented.
- Browser automation may not bypass platform APIs, authentication, pricing,
  rate limits, or policy.
- Multiple workspaces, SaaS tenancy, organizations, public registration, email
  invitations, billing, PostgreSQL, Redis, brokers, Kubernetes, and separate
  workers are outside version 1.
