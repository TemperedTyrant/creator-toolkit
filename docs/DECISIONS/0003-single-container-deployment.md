# ADR 0003: Use a single application container

- Status: Accepted
- Decision date: 2026-07-28

## Context

The primary TemperedTyrant Creator Toolkit operator is a content creator who may
have limited infrastructure experience. The normal installation should be close
to `docker compose up -d` and should work on a laptop or small server without
Kubernetes or a public domain.

The application still needs a web server and durable background work, but the
modular-monolith design allows both to run safely in one process.

## Decision

Ship version 1 as one Linux application container plus one named persistent
volume.

- Run the Razor Pages application and hosted job services in one process.
- Store SQLite, Data Protection keys, and documented durable state in a
  configurable in-container data directory backed by the named volume.
- Publish compatible linux/amd64 and linux/arm64 images.
- Run as a non-root user.
- Use structured stdout/stderr logs and unauthenticated, nonsensitive liveness
  and readiness checks.
- Provide portable, overridable bind, port, public URL, data path, and trusted
  proxy configuration.
- Support direct localhost and reverse-proxy deployment.

Do not require a separate database, Redis, queue, scheduler, or Kubernetes
deployment in version 1.

No separate worker service, process, or container. Durable background jobs run through ASP.NET Core hosted services inside the application process.

## Consequences

Benefits:

- minimal setup and a small failure surface;
- one image/version to upgrade;
- one volume to back up through a supported procedure;
- no cross-service authentication or deployment ordering;
- outbound publishing works without a public domain.

Costs and constraints:

- web and background workloads share CPU and memory;
- application restart pauses job processing;
- the design is single-instance, not highly available;
- a complete volume contains both encrypted data and local encryption keys;
- scale-up and concurrency limits must protect interactive pages.

## Alternatives considered

- **Separate web and worker containers:** rejected because it complicates
  SQLite ownership, setup, health, and upgrades without an initial need.
- **Compose with PostgreSQL/Redis:** rejected because it violates the easy,
  single-volume default.
- **Native host packages:** may be considered later, but a Compose-first
  deployment is more portable for version 1.
- **Kubernetes charts:** rejected as a version 1 requirement; community
  packaging must not distort the supported simple architecture.

## Revisit when

Reconsider if measured publishing workloads harm interactive use despite
bounded concurrency, or if a supported external database and multi-instance
architecture are accepted through separate ADRs. Preserve a one-command default
even if advanced deployment options are eventually added.

SPDX-License-Identifier: AGPL-3.0-only
