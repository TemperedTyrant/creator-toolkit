# ADR 0001: Use a .NET modular monolith

- Status: Accepted
- Decision date: 2026-07-28

## Context

TemperedOps Creator Toolkit needs a web interface, local identity and
authorization, trigger normalization, durable announcement publishing,
scheduling, provider connections, health checks, and diagnostics. These
concerns need clear boundaries, but version 1 is intended for a nontechnical
creator to install and operate on one machine.

Splitting these concerns into independently deployed services would require
network contracts, distributed tracing, deployment coordination, and additional
state or messaging infrastructure before the product has demonstrated a need
for them.

At the same time, an unstructured monolith would let provider details leak into
the core announcement model and make testing, security review, and future
connector additions harder.

## Decision

Build TemperedOps Creator Toolkit as a modular monolith targeting .NET 10 LTS
with:

- ASP.NET Core Razor Pages for the server-rendered interface;
- ASP.NET Core Identity for local authentication and roles;
- Entity Framework Core and one SQLite database;
- ASP.NET Core hosted background services for durable-job execution;
- Web, Core, and Infrastructure .NET projects that form one application and
  preserve explicit logical boundaries;
- logical modules with explicit application contracts;
- provider adapters that depend on provider-neutral connector and source
  interfaces.

Modules may share one process and database transaction where useful, but each
module owns its behavior and persistence concepts. Core domain/application code
must not depend on Discord, Bluesky, Twitch, YouTube, or their DTOs.

Multiple projects do not create multiple deployable services.

No separate worker service, process, or container. Durable background jobs run through ASP.NET Core hosted services inside the application process.

Server-side authorization policies apply at page and application-service
boundaries. Publishing work is separated into independent per-destination
deliveries.

## Consequences

Benefits:

- one process is easy to install, debug, back up, and upgrade;
- local calls and database transactions keep workflows comprehensible;
- explicit module seams retain testability and provider independence;
- hosted services can process durable work without a second deployment.

Costs and constraints:

- module separation relies on code review and architecture tests rather than
  process isolation;
- one process and SQLite are not a high-availability design;
- a blocking or resource-heavy connector can affect the process unless timeout
  and concurrency limits are enforced;
- later service extraction would require carefully designed contracts and data
  ownership changes.

## Alternatives considered

- **Unstructured monolith:** operationally simple but rejected because provider
  coupling and implicit boundaries would make reliability and security harder.
- **Microservices:** rejected for version 1 because their operational and
  distributed-systems costs conflict with easy self-hosting.
- **External workflow engine or queue:** rejected because durable SQLite jobs
  and hosted services cover the initial scale without Temporal, Kafka, Redis, or
  another service.
- **Client-heavy SPA:** rejected because Razor Pages and minimal JavaScript
  better fit the setup and maintenance goals.

## Revisit when

Create a superseding ADR only if measured workloads cannot be handled safely by
one process, a provider workload needs independent isolation, or a supported
deployment model requires horizontal scale. Feature count alone is not enough.

SPDX-License-Identifier: AGPL-3.0-only
