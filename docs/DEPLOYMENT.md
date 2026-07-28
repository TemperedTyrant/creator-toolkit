# Deployment design

## Current status

SocialCreator does not yet ship an application image or Compose file. This
document defines the deployment contract that milestone 1 must implement.
Examples describe intended behavior and must not be treated as working commands
until that milestone is complete.

## Default shape

The supported version 1 deployment is:

- one application container;
- one named persistent volume;
- SQLite as the embedded database;
- no required external database, cache, queue, worker, or identity provider;
- Linux amd64 and arm64 images.

Normal startup should be close to:

```sh
docker compose up -d
```

The checked-in Compose file will use portable defaults and a named volume. It
will not contain a host-specific absolute path, domain, IP address, username, or
secret.

## Planned configuration contract

Milestone 1 should introduce project-prefixed environment settings equivalent
to:

| Purpose | Planned behavior |
| --- | --- |
| Data directory | In-container portable default, overridable without changing application code |
| Host port | Portable Compose default, overridable by the operator |
| Bind address | Safe direct-localhost default with an explicit option for proxy/LAN exposure |
| Public URL | Optional canonical external URL; required only for callbacks that need it |
| Trusted proxies | Explicit proxy addresses/networks; never trust every forwarded header |
| Log level | Structured console-log verbosity without enabling secret-bearing HTTP dumps |

Exact variable names will be finalized with the milestone 1 configuration
schema. A committable `.env.example` may contain non-secret examples and empty
placeholders. Real `.env` files and Compose overrides containing secrets remain
ignored.

Connector credentials should normally be entered through the authenticated
interface and encrypted in SQLite. Do not place provider credentials in the
Compose file.

## First startup

Starting an empty volume leaves the application locked. Visiting it shows safe
instructions but cannot create an Owner.

The operator invokes an explicit command, planned in this form:

```sh
docker compose exec app socialcreator bootstrap-owner
```

The command prints a cryptographically random, single-use bootstrap value only
to that terminal. It expires after 30 minutes. The operator supplies it to the
Owner setup page, creates the initial account, and completes workspace setup.
Afterward, the setup endpoint and bootstrap generation are permanently disabled.

Changing the port binding or adding a reverse proxy before setup does not weaken
this protection.

## Localhost use

Manual publishing, scheduling, Discord incoming-webhook delivery, generic
outgoing-webhook delivery, and Bluesky app-password delivery make outbound
connections only. They do not require:

- a public domain;
- inbound router configuration;
- a TLS certificate at the application;
- an OAuth callback.

A direct-localhost operator may keep the safe default bind. The browser should
still use the exact documented local origin so secure-cookie and antiforgery
behavior remain predictable.

## Reverse proxy use

A reverse proxy may provide TLS and external routing. Operators must:

1. configure the canonical public HTTPS URL;
2. set the container bind/port exposure deliberately;
3. configure only the actual proxy address or network as trusted;
4. forward the original scheme and host using supported headers;
5. preserve request-body limits and timeouts;
6. ensure the management interface is not unintentionally public before setup.

The application must not blindly trust `X-Forwarded-*` values from arbitrary
clients. OAuth or provider callback documentation must display both the
configured public URL and exact callback path, explain reachability, and test
configuration before redirecting the user.

Inbound generic, Twitch, YouTube, or future OAuth features may require a
publicly reachable HTTPS URL. That requirement belongs to the integration, not
to basic installation.

## Persistent data

The named volume contains:

- SQLite database files and migration state;
- ASP.NET Core Data Protection keys;
- supported local backup staging data, if any;
- other explicitly documented durable application state.

Temporary files, build artifacts, and logs are not durable application state.
The container runs as a non-root user and the volume must be writable only by
the application identity.

The data directory remains configurable so operators can use a compatible
volume layout without embedding a developer's host path in the project.

## Backups and restores

Copying a live SQLite file without coordination can produce an inconsistent
backup. The supported backup path will use SQLite's supported online backup
mechanism or briefly coordinate writes, then package the database and required
key material consistently.

Only the Owner may initiate backup or restore through supported application
operations. Backups:

- contain sensitive encrypted data and the keys needed to use it;
- must receive restrictive permissions and secure storage;
- need an integrity manifest and format/application version;
- must never be committed to Git;
- should be tested by restoring into a separate disposable installation.

Restore validates compatibility before replacing state, preserves the one-Owner
invariant, does not re-enable first-run bootstrap, and records the operation.
Exact backup/restore commands and recovery guarantees will be defined before the
first public beta.

## Owner recovery

An operator with access to the container runtime needs a recovery path even when
all browser sessions are unusable. The planned shape is:

```sh
docker compose exec app socialcreator reset-owner
```

It will issue a short-lived one-time recovery link, persist only a hash and
lifecycle metadata, invalidate any prior unused recovery link and existing Owner
sessions, and append an audit record. The raw link will appear only in that
command's terminal and never in routine logs. It will not reveal the current
password or any provider credential. Final syntax and confirmation safeguards
are milestone 1 work.

## Upgrades

The future release procedure will:

1. read the release notes and supported upgrade path;
2. create and verify a backup;
3. pull the new multi-architecture image;
4. restart with `docker compose up -d`;
5. apply compatible EF Core migrations under a startup lock;
6. report readiness only after migrations and key access succeed;
7. verify health, connection state, schedules, and job processing.

Destructive or non-reversible migrations require an ADR, explicit release-note
warning, and tested recovery path. Downgrades are not assumed safe.

## Health and operations

The future container will expose separate liveness and readiness checks without
credentials or sensitive detail:

- liveness confirms the process can respond;
- readiness confirms initialization, database access, migration compatibility,
  and job-runner readiness.

Provider connection health belongs in the authenticated interface, not the
unauthenticated container health response.

Logs go to stdout/stderr as structured records for Compose collection. Log
rotation is the container runtime's responsibility. Tokens, credentials,
webhook URLs, authorization headers, cookies, keys, and provider bodies are
never logged.

## Multi-architecture release expectations

Public images must support `linux/amd64` and `linux/arm64` from the same source
revision. CI must build and smoke-test both platforms, use an open-source build
toolchain, publish provenance/checksum information when feasible, and avoid a
paid hosted-service dependency.
