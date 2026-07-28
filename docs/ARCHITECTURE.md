# Architecture

## Status and goals

This document describes the intended version 1 architecture. No application is
implemented yet.

SocialCreator will be a modular monolith built with .NET 10 LTS, ASP.NET Core
Razor Pages, ASP.NET Core Identity, Entity Framework Core, SQLite, and hosted
background services. It will produce one deployable application container and
use one named persistent volume.

The design prioritizes:

- one-process operational simplicity;
- provider-neutral domain logic;
- durable and idempotent publishing;
- independent destination failures;
- centralized server-side authorization;
- strict separation between user-safe status and technical diagnostics.

## Runtime topology

```text
Browser / provider callback
            |
            v
  ASP.NET Core application
  ├── Razor Pages
  ├── Identity and authorization
  ├── Application/domain modules
  ├── Connector HTTP clients
  └── Hosted durable-job runner
            |
            v
    SQLite + Data Protection keys
       in one named volume
```

There is no separate worker, broker, cache, or database service. The web and
background paths coordinate through durable SQLite records.

## Module boundaries

The initial modules are logical boundaries within the application:

| Module | Responsibility |
| --- | --- |
| Setup | Initialization state, bootstrap claims, workspace defaults |
| Identity and Authorization | Local users, roles, policies, ownership invariants, account setup and recovery |
| Events and Sources | Source configuration, normalized creator events, duplicate detection |
| Announcements and Templates | Drafts, reusable content, provider variants, previews |
| Approval | Submission, approval, rejection, invalidation, workspace policy |
| Destinations and Connections | Provider-neutral connection lifecycle and health |
| Publishing | Per-destination delivery planning, attempts, outcomes |
| Scheduling and Jobs | Recurrence calculation, missed-run policy, durable leases and retries |
| Secrets | Encrypted credential storage and replacement |
| Audit | Application-enforced append-only records for security-sensitive supported operations; no tamper evidence against direct data access |
| Diagnostics | Reference IDs, Debug data, sanitized export, log policy |

Modules expose narrow application contracts. EF Core may use one SQLite
database and one application `DbContext`, but persistence tables and queries
must respect module ownership. Cross-module behavior is coordinated through
application services and domain identifiers, not provider-specific shortcuts.

Provider adapters depend on application contracts. Domain and application code
must not reference Discord, Bluesky, Twitch, YouTube, or provider DTOs.

## Identity and authorization

Use ASP.NET Core Identity for user persistence, password hashing, password
recovery primitives, roles, security stamps, and cookie integration. Do not
invent password hashing, authentication cookies, or authentication token
formats.

There is exactly one workspace and exactly one active Owner. Owner, Admin,
Editor, and Viewer are fixed system roles. Authorization policies describe
capabilities such as `ManageUsers`, `ManageIntegrations`, `ApproveAnnouncement`,
`PublishAnnouncement`, `ViewDebug`, `RestoreBackup`, and
`TransferOwnership`.

Razor Page handlers and application services both enforce the applicable
policy. Application-service enforcement protects future non-page entry points,
background actions, and tests from relying on UI state.

Role changes, password resets, user disablement, and ownership transfer update
the affected Identity security stamp or equivalent security version. Cookie
validation checks that version so an already-issued cookie becomes invalid.
Ownership transfer is a single transaction that preserves the one-Owner
invariant. The current sole Owner cannot otherwise be disabled, deleted, or
demoted.

Opaque bootstrap, account-setup, and Owner-recovery credentials are capability
tokens, not login sessions. Their random values contain no embedded claims. Only
a cryptographic hash, purpose, subject where applicable, creation time, expiry,
use time, and revocation time are persisted.

## Conceptual model

Names may be refined during implementation, but these concepts and invariants
should remain.

- **Workspace:** singleton configuration, including IANA time zone and Editor
  publishing policy.
- **LocalUser:** ASP.NET Core Identity user and one system role.
- **EventSource:** configured origin of normalized creator events.
- **CreatorEvent:** immutable normalized fact with source, event type,
  occurrence time, stable external key, and sanitized metadata.
- **Template:** reusable default content and optional destination variants.
- **AnnouncementWorkflow:** mapping from a source/event condition to template,
  destinations, and approval behavior inherited from workspace policy.
- **Announcement:** draft or generated content, revision, state, source/schedule
  context, and intended destinations.
- **ApprovalDecision:** submission revision, submitter, reviewer, decision,
  timestamp, and safe comment.
- **DestinationConnection:** connector type, display label, encrypted credential
  reference, configuration, and health state.
- **Delivery:** one announcement revision destined for one connection, with
  independent state and stable idempotency identity.
- **PublishingAttempt:** immutable attempt outcome, timing, classification,
  provider-safe metadata, and diagnostic reference.
- **PersistentJob:** due time, type, payload reference, attempt count, lease,
  next attempt, and terminal state.
- **Schedule:** local recurrence intent, IANA time zone, next UTC occurrence, and
  missed-run policy.
- **ConnectionHealth:** last check, state, safe reason, next action, and
  reconnect requirement.
- **AuditRecord:** actor, action, target category/reference, outcome, time, and
  diagnostic reference.

Raw provider credentials and unsanitized payloads do not belong in domain
objects, jobs, audit rows, or diagnostic records.

## Connector contracts

Destination connectors will implement a provider-neutral contract equivalent
to:

- describe supported capabilities and configuration fields;
- validate non-secret configuration;
- test a connection and return a normalized health result;
- validate and render a destination-specific preview;
- publish a prepared immutable delivery;
- classify the result as success, retryable failure, permanent failure, or
  reconnect required.

The contract must accept cancellation and bounded timeouts. A connector result
contains a safe user message, recommended action, optional safe provider
receipt, and diagnostic reference. It must not expose HTTP bodies, authorization
details, or secrets to the domain or normal interface.

Trigger sources similarly normalize provider input into a creator event. Source
adapters authenticate input before normalization and must provide a stable
external event key.

## Event idempotency and delivery isolation

For provider events, a unique database constraint on the source identity and
provider event key is the final duplicate guard. Authentication and parsing may
occur before the transaction, but accepting the event, creating its initial
announcement/work items, and committing durable jobs occur atomically.

Manual publication uses a stable command/idempotency value. Scheduled
occurrences derive a deterministic identity from the schedule and intended
local occurrence. A delivery has a stable identity for one announcement
revision and destination.

Each destination receives its own Delivery and PersistentJob. Publishing is
never wrapped in a cross-destination transaction. One connector's exception,
rate limit, invalid credential, or permanent failure cannot cancel or delay
another connector's already-due work.

Provider-side idempotency features should be used when officially supported,
but the local delivery state and unique constraints remain authoritative.

## Durable jobs and retries

The hosted job runner reads due jobs from SQLite. Claiming a job uses an atomic
conditional update with a random lease owner and expiry. A crashed process
leaves an expired lease that a later runner may recover.

For each execution:

1. claim one due job;
2. load immutable delivery inputs;
3. verify the current authorization/workflow state still permits execution;
4. call only that destination connector with a bounded timeout;
5. persist the attempt and resulting delivery/job state;
6. release or complete the lease.

Retryable failures use bounded exponential backoff with jitter and honor safe
provider retry guidance. A maximum attempt count or maximum age moves the job
to a terminal failed state. Permanent and reconnect-required failures do not
retry automatically. An authorized manual retry creates a new auditable
execution without erasing attempt history.

Only one application instance is supported in version 1. Leases provide crash
recovery and duplicate resistance, not a promise of multi-node scheduling.

## Announcement and approval state

Expected states include Draft, PendingApproval, Approved, Scheduled, Queued,
PartiallyDelivered, Delivered, Failed, Rejected, and Cancelled. Implementation
may separate content and delivery aggregate states, but it must preserve these
behaviors:

- preview is computed for the exact revision to publish;
- approval refers to an exact revision;
- editing pending or approved content invalidates that approval;
- an Editor cannot approve their own submission;
- direct Editor publishing is allowed only when the current workspace policy
  permits it;
- missing approval at a scheduled due time holds the item;
- Owner and Admin publication does not require approval.

Every transition is validated and authorized in the application layer, not
inferred from a submitted UI state.

## Scheduling and time zones

The workspace stores one valid IANA time-zone identifier. All persisted instants
use UTC, while recurring schedules also retain their intended local time,
recurrence definition, and time-zone identifier.

Future occurrences are calculated from local calendar intent:

- a local time in a daylight-saving spring gap moves to the next valid time;
- an ambiguous fall-back time executes once at the earlier valid offset;
- changing the workspace time zone affects newly calculated future occurrences,
  not history or completed deliveries.

Each schedule chooses Skip, HoldForReview, or PublishLatest for missed runs.
HoldForReview is the default. Recovery creates at most one held or latest item
per schedule and never floods several stale posts.

## Configuration and secrets

Non-secret boot configuration comes from standard ASP.NET Core configuration,
using a project-specific environment prefix when variables are introduced.
Portable defaults may describe the in-container data directory and HTTP port;
host paths, public URLs, bind addresses, and ports remain configurable.

Connector secrets are encrypted with ASP.NET Core Data Protection and stored
separately from display configuration. The Data Protection key ring persists in
the named volume. A credential may be entered or replaced but never retrieved.

The public URL is optional until an inbound integration or OAuth callback needs
it. URL generation must use configured proxy and forwarded-header rules rather
than trusting arbitrary forwarding headers.

## Health, errors, and diagnostics

Normal pages receive only:

- clear status;
- concise user-safe error;
- recommended corrective action;
- random diagnostic reference ID.

Structured console logs contain technical context keyed by the same reference.
An Owner/Admin-only Debug page exposes allowlisted technical fields such as
application version, migration state, job counts, sanitized attempt categories,
connection check timing, and configuration presence. Sanitized export uses the
same or a stricter allowlist.

Secrets, authorization data, webhook URLs, internal payloads, and encryption
keys never enter user errors, diagnostics, exports, or logs.

## Deployment and evolution

The default deployment remains one application container and one named volume,
compatible with Linux amd64 and arm64. SQLite backups must coordinate with the
application so the database and related state are consistent.

New destinations add adapters and provider-specific tests without changing core
announcement semantics. A need for another process, database, queue, workspace,
or tenancy model requires a new ADR and is outside version 1.

See [the initial ADRs](DECISIONS/README.md) for the decisions behind this shape.
