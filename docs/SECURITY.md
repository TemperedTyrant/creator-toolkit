# TemperedTyrant Creator Toolkit security design

## Scope

This document defines the intended version 1 security boundary. It supplements
the public vulnerability-reporting policy in the repository root.

TemperedTyrant Creator Toolkit protects one creator workspace from unauthenticated
visitors and from local users exceeding their assigned role. It protects stored
connector credentials from routine database inspection and prevents them from
appearing in supported interfaces, logs, diagnostics, or exports.

An operator with unrestricted access to the container host or persistent volume
is trusted. Such an operator can modify the application or database and can
access both encrypted data and the local Data Protection key ring.

## Principal threats

- A visitor claims an uninitialized installation.
- A local user invokes an operation their role cannot perform.
- A stale authentication cookie retains permissions after an account change.
- A credential leaks through logging, errors, previews, diagnostics, or backup
  handling.
- A webhook causes server-side requests to local services or cloud metadata.
- An attacker forges or replays an incoming event.
- A duplicate event or retry creates duplicate public posts.
- A provider response or user-authored template injects unsafe output.
- An untrusted reverse proxy header changes generated callback URLs or client
  identity.
- A malicious dependency or container layer compromises the application.

## Initialization and takeover resistance

A new database starts in a locked, uninitialized state. No browser-only race may
claim it.

An operator explicitly invokes an administrative CLI command inside the
container. It creates a value using a cryptographically secure random generator,
persists only its cryptographic hash and metadata, and prints the raw value only
to the invoking terminal. It must not use normal application logging.

The bootstrap credential:

- is scoped only to initial Owner creation;
- expires 30 minutes after creation;
- is invalidated when replaced;
- is consumed atomically with Owner and workspace creation;
- is permanently unavailable after successful initialization.

The setup handler returns an unavailable/not-found response after initialization
and must not reveal whether a guessed token was close or previously valid.

## Authentication and account lifecycle

Use ASP.NET Core Identity for local user storage, password hashing, password and
recovery token primitives, security stamps, lockout behavior, roles, and secure
cookie integration. Do not build custom password hashing, login cookies, bearer
tokens, or authentication token formats.

Authentication requirements include:

- HTTPS-aware Secure cookies, HttpOnly, and an appropriate SameSite mode;
- antiforgery validation on state-changing browser requests;
- login throttling/lockout without account enumeration;
- generic login and recovery responses;
- regular security-stamp validation;
- session invalidation after password reset, disablement, role change, or
  ownership transfer;
- rotation of framework Data Protection keys according to supported defaults.

Only the Owner creates users. A new user receives a random opaque setup
capability that expires after 24 hours and is single-use. Only a hash is stored.
The raw setup link is displayed once to the Owner and is never logged. Revoking
or regenerating it invalidates the prior value. The user chooses their own
password.

An operator with container access can invoke an Owner recovery command. Recovery
must create a cryptographically random, short-lived, one-time opaque capability,
persist only its hash and lifecycle metadata, invalidate any prior unused
recovery capability and existing Owner sessions, and append an audit record. The
raw recovery value or link is printed only to the command's terminal and never
sent through normal application logging. The recovery handler uses generic
responses for invalid, expired, used, or revoked values. Recovery must not print
an existing password or connector credential.

## Authorization

Fixed roles are Owner, Admin, Editor, and Viewer. Central named authorization
policies represent capabilities; role checks scattered through UI markup are
not the security model.

Every protected Razor Page handler and application-service method validates its
policy. Background jobs revalidate state relevant to publishing, including
approval and cancellation, before external delivery.

There is exactly one workspace and one active Owner. Database constraints and a
transactional ownership-transfer service preserve the sole-Owner invariant.
The current sole Owner cannot be disabled, deleted, or demoted through another
path.

Tests must cover both allowed and denied operations for every role, forged
requests to hidden actions, stale cookies, disabled users, role transitions,
and ownership transfer failure rollback.

## Secret storage

Connector credentials are encrypted with ASP.NET Core Data Protection before
database persistence. Data Protection keys persist in the named volume with
restrictive file permissions. Secrets are separated from display configuration.

A secret may be:

- accepted over a protected form;
- used by its connector;
- replaced by an authorized user;
- deleted as part of an authorized connection deletion.

It may never be retrieved, redisplayed, included in HTML source, placed in a
hidden form field, returned by an API, or copied to a diagnostic export.

Bluesky accepts only a dedicated app password. Labels and help text must
explicitly reject the user's normal account password.

Because the database and key ring share one operator-managed volume, encryption
does not protect against theft or compromise of that complete volume. A future
external key-encryption option would require a separate design.

## Logging and redaction

Use structured console logging and generate a random diagnostic reference for
technical failures. Redact before a value reaches the logger.

Never log:

- passwords or password-equivalent values;
- bootstrap, account setup, recovery, OAuth, access, or refresh tokens;
- Bluesky app passwords;
- authorization and cookie headers;
- full webhook URLs, user-info, paths, or query strings that may be credentials;
- custom secret header names paired with values;
- Data Protection keys or encryption material;
- request/response bodies unless an explicit safe schema selects individual
  nonsensitive fields.

HTTP client logging must not automatically capture headers or bodies. Exception
logging must avoid attaching raw provider requests or response content.

## Normal errors, Debug, and exports

Normal pages show a safe status, short explanation, recommended action, and
diagnostic reference. Stack traces, HTTP status/body detail, internal record
identifiers, job leases, attempt internals, and worker state belong only on the
Owner/Admin Debug page or in structured logs.

Debug data and diagnostic exports use explicit field allowlists. Export replaces
internal record identifiers with diagnostic references where practical and
contains configuration presence rather than values. Redaction tests seed
credential-shaped markers and assert that none appear in rendered HTML, logs,
errors, audit records, or exports.

## Generic outgoing webhook SSRF controls

Generic outgoing webhooks are a deliberate server-side request surface.

Default policy:

- accept only `https` URLs;
- reject URL user-info and malformed, noncanonical, or unsupported hostnames;
- resolve DNS before connecting and validate every IPv4 and IPv6 result;
- connect only to an address that passed validation, reducing DNS-rebinding
  opportunities;
- block loopback, unspecified, link-local, multicast, private-use, carrier-grade
  NAT, documentation, reserved, and known cloud-metadata destinations;
- reapply scheme, hostname, DNS, address, and port policy on every redirect;
- allow only a small fixed number of redirects;
- enforce connect, request, and overall timeouts;
- cap request and response body sizes;
- apply the identical policy to connection tests and real deliveries.

Private-network delivery is disabled by default. An advanced setting may allow
specific normalized hostnames or CIDR ranges. Broad wildcards and “allow all
private networks” are not acceptable defaults. Resolution must still match the
allowlist.

Custom headers are not unrestricted. The connector must deny hop-by-hop and
security-sensitive headers, separately encrypt supported secret header values,
and guarantee name/value redaction. Logs identify a destination by internal
diagnostic reference and safe display label, never its full URL.

## Incoming webhook security

The generic incoming trigger uses a project-defined signed protocol:

- a per-source random secret;
- HMAC over a versioned canonical request representation;
- timestamp and narrow clock-skew validation;
- a stable sender idempotency key;
- constant-time signature comparison;
- strict content type and request-body limits;
- replay rejection using persisted idempotency data;
- prompt response after durable acceptance.

The source secret can be replaced but never retrieved. Invalid authentication
returns a generic response and does not create jobs.

Provider callbacks must follow their official signature and replay guidance.
Browser automation is not an alternative.

Branding changes do not alter this protocol's security requirements. Any
versioned protocol label or header identifier introduced during implementation
must use the `creator-toolkit` machine-readable identity without weakening HMAC
authentication, timestamp validation, replay protection, body limits,
per-source secrets, or idempotency.

## Idempotency and outbound safety

Database unique constraints are the final duplicate guard for source/provider
event keys, scheduled occurrence identities, and delivery identities. Accepting
an event and enqueuing its work occur in one transaction.

Jobs use expiring atomic leases. Provider requests have bounded timeouts.
Retries use bounded backoff and must not blindly retry permanent authentication,
validation, or policy failures. Where a provider supports an official
idempotency mechanism, connectors use it in addition to local guards.

## Web application and proxy safety

- Use Razor encoding defaults and sanitize only where intentionally rendering a
  restricted rich-text format.
- Validate and limit templates, provider metadata, uploads, and URL fields.
- Use antiforgery protection and authorization on every mutation.
- Do not trust all forwarded headers. Operators explicitly configure trusted
  proxies or networks.
- Use the configured canonical public URL for callbacks; do not derive
  security-sensitive URLs from arbitrary Host or forwarding headers.
- Set security headers appropriate to a server-rendered application, including
  a restrictive Content Security Policy.
- Media support must check type, size, provider compatibility, and remote
  reachability without weakening SSRF policy.

## Audit trail

Audit records are append-only through supported application operations. They are
not tamper-evident against an operator with direct database or volume access.

Audit authentication security events, user/role changes, ownership and recovery,
setup-link lifecycle, integration credential replacement, source/destination
changes, approval decisions, publishing actions, backup/restore, and destructive
operations.

Records contain actor, action, target category/reference, outcome, timestamp,
and diagnostic reference. They exclude raw capabilities, secrets, webhook URLs,
request bodies, and sensitive provider data.

## Future action and local-capability safety

The creator-event/action seam does not weaken version 1 authorization,
idempotency, audit, redaction, or diagnostic requirements. Every future action
execution needs independent authorization, execution state, an execution or
idempotency key, duplicate-suppression semantics where possible, failure
classification, an action-specific retry policy, audit behavior, and diagnostic
references.

Retry policy must distinguish automatically retryable, non-retryable, retryable
only when non-execution is known, and manual-retry-only actions. Sound playback,
OBS scene changes, alerts, recording controls, and other local side effects
must not be retried automatically unless the implementation establishes that
doing so is safe. Ambiguous completion must not cause duplicate side effects.

Planned authenticated OBS browser-source pages remain protected web
application surfaces. The Exploratory local creator agent would require
explicit pairing and a separate threat model before implementation. It is not
part of version 1 and is not trusted or required by the default deployment.

## Backups, updates, and dependencies

Backups contain sensitive encrypted data and local key material. Only the Owner
may initiate supported backup or restore operations. Documentation must require
secure storage, restrictive permissions, integrity checking, and an offline
copy. Restore must validate compatibility and must not create a second active
Owner or re-enable bootstrap.

Pin container base images by supported version and make updates reviewable.
Generate dependency inventories and address known vulnerabilities before public
release. Dependencies must be open source, actively maintained, license
compatible, and unnecessary packages should be removed.

Multi-architecture images receive equivalent tests. The application must run as
a non-root container user and write only to its configured data directory and
necessary temporary paths.

## Accepted residual risks

- A hostile host or volume operator can read or modify all application state.
- A destination provider can change policies, limits, or response behavior.
- SQLite and a single process provide recovery, not high availability.
- An explicitly configured private-network webhook allowlist grants intentional
  access to those targets and must be treated as sensitive configuration.
- A user can copy content they are authorized to view; application controls do
  not provide digital-rights management.
