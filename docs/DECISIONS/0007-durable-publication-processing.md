# ADR 0007: Process confirmed Discord publications durably in process

- Status: Accepted
- Decision date: 2026-08-01

## Context

A confirmed manual Discord publication must survive browser disconnection,
application restart, and transient provider failures. Uploaded images must also
survive while delivery is pending without becoming permanent attachment
storage. The deployment remains one application process, one SQLite database,
one Data Protection key ring, and one container.

## Decision

Final confirmation writes one provider-neutral Publication and one independently
leased PublicationDelivery per selected Discord destination to SQLite. A single
ASP.NET Core hosted worker in the web application claims due deliveries one at
a time. There is no separate worker service, general job framework, scheduler,
broker, or second container.

The exact reviewed Discord request and optional uploaded image are serialized as
a validated typed snapshot and encrypted with a Data Protection purpose unique
to the publication. The worker alone decrypts it. The snapshot is immutable;
later announcement edits do not alter queued content. Once every delivery is
Succeeded, FailedPermanent, or Cancelled, the protected payload is removed in
the same transaction as the terminal state. Safe content-free history and
attempt metadata remain.

Each delivery has a random process lease owner, bounded lease expiry, optimistic
revision, and stable Discord nonce. Expired leases recover after a crash. A
retry reuses the same nonce and fresh multipart image stream. Processing is
at-least-once: Discord nonce enforcement offers bounded duplicate suppression,
not permanent exactly-once delivery.

Only rate limits, Discord 5xx/unavailability, connection failures, and uncertain
timeouts retry automatically. The initial attempt may be followed by delays of
30 seconds, two minutes, and ten minutes. Valid Discord Retry-After guidance is
used up to ten minutes. Authentication, permission, missing destination,
validation, corrupted payload, and cancellation outcomes are permanent.

Cancellation prevents new claims and cancels queued or retrying deliveries.
An in-flight delivery finishes at a safe boundary; an already successful
Discord message is never represented as retracted. Scheduling and manual replay
remain deferred.

## Consequences

- confirmed work and encrypted images recover with SQLite and Data Protection
  key continuity;
- one destination failure or retry does not block other destinations;
- safe Publish History remains readable after sensitive payload cleanup;
- operators must back up and restore the database and Data Protection key ring
  together;
- an uncertain crash after Discord accepts a request can cause another attempt,
  so no exactly-once guarantee is made;
- only Discord is implemented, although the publication aggregate does not put
  Discord payload fields in Core.

## Alternatives considered

- **Foreground HTTP delivery:** rejected because browser cancellation and host
  restart could lose confirmed work.
- **A separate worker container or broker:** rejected because it expands the
  supported topology without need for the single-installation product.
- **A general job framework or scheduler:** rejected because checkpoint 14 needs
  only narrow destination delivery, leasing, and retries.
- **Plaintext payload or attachment files:** rejected because pending content,
  URLs, alt text, and image bytes are sensitive and must remain encrypted and
  coordinated with existing backup behavior.

SPDX-License-Identifier: AGPL-3.0-only
