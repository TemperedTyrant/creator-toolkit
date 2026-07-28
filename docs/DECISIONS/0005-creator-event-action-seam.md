# ADR 0005: Establish a reusable creator-event and action seam

- Status: Accepted
- Decision date: 2026-07-28

## Context

TemperedOps Creator Toolkit is a modular self-hosted toolkit for creators and
their teams. Creator Announcements will be the first implemented and released
module, and the ten version 1 milestones remain focused on reliable
announcement preparation and delivery.

The provider-neutral core should not make social publishing the permanent
definition of every creator utility. Future modules may respond to a creator
event by displaying an OBS alert, playing a sound, controlling OBS, invoking a
webhook, or publishing to one or more social destinations. At the same time,
designing a generalized workflow engine before the announcement module exists
would add abstractions, authorization paths, and failure semantics that version
1 does not need.

## Decision

Establish a reusable creator-event/action architectural seam.

- Trigger sources normalize signals into provider-neutral creator events.
- Social publishing is one action category, not the only possible action type.
- Future conditions, actions, action executions, connections, assets, and
  workflows may build on the seam.
- Version 1 continues to use concrete announcement, approval, destination,
  delivery, attempt, schedule, and persistent-job concepts.
- A generalized trigger-condition-action workflow implementation is Planned
  post-v1 work, not part of version 1.

Every future action execution has:

- independent authorization;
- execution state;
- an execution or idempotency key;
- duplicate-suppression semantics where possible;
- failure classification;
- an action-specific retry policy;
- audit behavior;
- diagnostic references.

An action-specific retry policy classifies the execution as:

- automatically retryable;
- non-retryable;
- retryable only when non-execution is known;
- manual-retry only.

Sound playback, OBS scene changes, alerts, recording controls, and other local
side effects must not be retried automatically unless their implementation can
establish that doing so is safe. Ambiguous completion must not cause duplicate
side effects.

## Consequences

Benefits:

- creator events and shared operational capabilities can support future modules
  without coupling the core to social providers;
- independent action-execution guarantees preserve authorization, failure
  isolation, auditability, and diagnostics across action categories;
- version 1 remains understandable in announcement language;
- future generalized workflow work has an intentional seam rather than an
  accidental publishing-only model.

Costs and constraints:

- contributors must preserve the seam without prematurely generalizing version
  1 models;
- action categories with physical or local side effects need stricter retry and
  ambiguous-completion handling than announcement delivery;
- future conditions, assets, workflows, browser-source pages, and local-agent
  pairing still require their own implementation and security designs.

## Alternatives considered

- **Define the whole product as announcement publishing:** rejected because it
  would make later creator utilities depend on social-post semantics.
- **Build a generic workflow engine in version 1:** rejected because it would
  expand the first release and obscure clear announcement concepts.
- **Require automatic retry for every action:** rejected because ambiguous
  completion could repeat sounds, alerts, scene changes, recording controls, or
  other side effects.
- **Create separate services for future action categories:** rejected as a
  default direction because the accepted modular-monolith and single-container
  decisions remain sufficient.

## Revisit when

Create a superseding ADR if generalized workflow implementation demonstrates
that the seam cannot express required conditions or action lifecycles, or if a
specific action category requires a different deployment boundary. Feature
count alone does not justify replacing the modular monolith.

ADR 0005 complements and does not supersede ADRs 0001–0004.

SPDX-License-Identifier: AGPL-3.0-only
