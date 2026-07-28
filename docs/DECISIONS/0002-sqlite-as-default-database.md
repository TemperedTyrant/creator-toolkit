# ADR 0002: Use SQLite as the default database

- Status: Accepted
- Decision date: 2026-07-28

## Context

TemperedOps Creator Toolkit version 1 stores one workspace's users,
configuration, encrypted credentials, events, announcements, approvals,
schedules, delivery attempts, jobs, connection health, and audit records. It
needs transactions, unique constraints, schema migrations, and durable recovery,
but it should start without a separate database service.

The expected workload is modest and write concurrency can be bounded within one
application process.

## Decision

Use SQLite as the only supported version 1 database through Entity Framework
Core.

- Keep the database in the configured persistent data directory.
- Enable and test appropriate SQLite durability and busy-timeout settings.
- Use database transactions and unique constraints for event, occurrence, and
  delivery idempotency.
- Claim durable jobs with atomic conditional updates and expiring leases.
- Keep transactions short and never hold one open across a provider HTTP call.
- Coordinate supported backups with SQLite rather than copying an active file
  blindly.
- Treat the application as a single-instance deployment.

Do not require PostgreSQL or another database for version 1, and do not add a
partially supported database abstraction merely for hypothetical portability.

## Consequences

Benefits:

- no database server, credentials, or network setup;
- one volume contains the installation's durable state;
- transactional event acceptance and job creation remain straightforward;
- backup and local development can be approachable.

Costs and constraints:

- SQLite serializes writes and can surface lock contention;
- it is unsuitable for shared-volume multi-instance deployment;
- database size and job concurrency need operational limits;
- backup, migrations, and restore require SQLite-aware coordination;
- database and Data Protection keys in one volume share a compromise boundary.

## Alternatives considered

- **PostgreSQL:** robust concurrency and operations, but rejected because it
  adds a required service, credentials, backup procedure, and setup burden.
- **Embedded key-value or document store:** rejected because relational
  constraints and transactions directly support idempotency and workflows.
- **Support SQLite and PostgreSQL immediately:** rejected because provider-
  specific SQL, migrations, concurrency semantics, and tests would expand
  version 1 without demonstrated need.
- **In-memory jobs:** rejected because announcements and retries must survive
  restarts.

## Revisit when

Reconsider if supported workloads repeatedly exceed SQLite write capacity,
operators need multiple application instances, or data size/retention makes
SQLite operations impractical. A migration design must preserve idempotency,
audit history, encrypted credentials, and simple defaults.

SPDX-License-Identifier: AGPL-3.0-only
