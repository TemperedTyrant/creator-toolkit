# Docker Compose deployment

TemperedTyrant Creator Toolkit ships as one non-root application container with
one named volume. SQLite and ASP.NET Core Data Protection key files live together
in that volume. There is no database, cache, broker, worker, or reverse-proxy
service in the supplied Compose deployment.

The deployment remains pre-alpha. Review changes before using it with data that
matters.

## Start the application

Docker Engine with Docker Compose is required. From the repository root:

```sh
cp .env.example .env
docker compose up -d --build
docker compose ps
```

Compose builds `creator-toolkit:local` from the repository-relative Dockerfile
and build context, so this works from a clean clone without a registry image.
To build explicitly instead, run `docker build --tag creator-toolkit:local .`
before `docker compose up -d`.

The default host origin is `http://127.0.0.1:8080`. The application listens on
port `8080` inside the container, while Compose publishes it only on the host's
loopback interface. `CREATOR_TOOLKIT_HOST_PORT` in `.env` changes the host port.
A public URL is not required for a local installation.

The container healthcheck runs `creator-toolkit healthcheck`, which makes a
short, fixed request to the container-local `/health/live` endpoint. It does not
open SQLite, inspect migrations, initialize Data Protection, acquire the
long-running application-host lock, or mutate application data. Operational
readiness remains available separately at `/health/ready`.

View status and redacted application logs with:

```sh
docker compose ps
docker compose logs --follow creator-toolkit
```

## First Owner setup

An empty installation is ready to serve Setup but cannot create an Owner until
an operator issues a short-lived bootstrap capability:

```sh
docker compose exec creator-toolkit creator-toolkit bootstrap-owner
```

The command prints the capability only to that terminal. Open `/Setup` at the
configured local origin and enter the capability there. Do not place the output
in `.env`, Compose configuration, shell history, tickets, or logs. The capability
expires after 30 minutes and becomes permanently unavailable after installation
initialization.

Administrative commands use a separate, short coordination path and may run via
`docker compose exec` while the web process holds the application-host lock. For
example, Owner recovery remains available as documented by the command itself:

```sh
docker compose exec creator-toolkit creator-toolkit reset-owner
```

## Stop and restart

Compose gives the application 30 seconds to stop. The host retains its existing
15-second shutdown timeout and 10-second internal lifecycle bound. Stop or start
without deleting persistent data with:

```sh
docker compose stop
docker compose start
```

Remove the application container and network while retaining the named volume
with:

```sh
docker compose down
```

Do not use `docker compose down --volumes` unless permanent deletion of the
installation is intended. `restart: unless-stopped` restarts the application
after an unexpected exit or Docker daemon restart, except when an operator has
explicitly stopped it.

## Persistent volume, backup, and restore

The logical Compose volume `creator-toolkit-data` is mounted at `/app/data` and
contains both:

- the SQLite database and its related files; and
- the ASP.NET Core Data Protection key ring.

Those files are one recovery unit. Losing the key ring can make protected data
and authentication state unusable even if the database survives.

The supported checkpoint-9 backup expectation is a stopped-container volume
backup. Stop the application, use the operator's Docker volume backup tooling to
copy the entire volume, then restart it. Do not copy only the SQLite database and
do not copy a live database file without a SQLite-aware, coordinated procedure.
No custom online backup system is included.

Restore only while the application is stopped. Restore the database and Data
Protection keys together from the same backup, retain their restrictive
permissions and ownership by container UID `1654`, and then start the application
and verify health and sign-in. Test backups by restoring them to an isolated,
disposable installation.

Announcement drafts and their encrypted image media are stored in the same
SQLite database. Media remains decryptable only when that database and the Data
Protection key ring are backed up and restored together. On upgrade, normal
application startup applies the reviewed announcement-media migration before
readiness succeeds. Stop the application and take a coordinated backup before
upgrading; do not live-copy individual SQLite files. The migration adds only
the announcement-media table and index without rewriting existing announcement,
publication, Identity, audit, diagnostic, or protected-secret data.

Pending Discord publications, including their encrypted immutable payloads and
uploaded image bytes, are also stored in SQLite. Data Protection keys are
required to resume them after restart. Always stop the application and back up
or restore the database and key ring together. A restored worker recovers due
or expired-leased deliveries automatically. Terminal publications retain only
safe history metadata because their protected payload ciphertext is removed.

## Configuration

`.env.example` contains safe defaults and commented placeholders. Copy it to the
ignored `.env` file before making local changes.

| Variable | Default | Purpose |
| --- | --- | --- |
| `CREATOR_TOOLKIT_HOST_BIND` | `127.0.0.1` | Host interface used by the Compose port publication. |
| `CREATOR_TOOLKIT_HOST_PORT` | `8080` | Host TCP port mapped to container port `8080`. |
| `CREATOR_TOOLKIT_PUBLIC_URL` | unset | Optional canonical external URL; HTTPS is required except for loopback. |
| `CREATOR_TOOLKIT_TRUSTED_PROXIES` | unset | Optional comma-separated proxy IP addresses. |
| `CREATOR_TOOLKIT_TRUSTED_NETWORKS` | unset | Optional comma-separated proxy networks in CIDR notation. |

The Compose file fixes the in-container data directory and port to `/app/data`
and `8080`. Provider credentials, bootstrap capabilities, recovery capabilities,
and other secrets do not belong in `.env` or Compose configuration.

Discord bot tokens are entered only through the authenticated Destinations UI
and are encrypted using the Data Protection keys in this same volume. Back up
and restore the database and key ring together or configured Discord
connections may become unusable. See [Discord setup](DISCORD.md).

## Reverse-proxy boundary

The supplied Compose file intentionally does not run a reverse proxy and does
not expose the application publicly by default. An operator adding an external
proxy is responsible for TLS, DNS, access controls, request limits, timeouts, and
network attachment.

For a reverse proxy deployment:

1. deliberately change `CREATOR_TOOLKIT_HOST_BIND` from the loopback default only
   as far as the proxy topology requires;
2. set `CREATOR_TOOLKIT_PUBLIC_URL` to the canonical external HTTPS origin;
3. configure only the actual proxy address or network in
   `CREATOR_TOOLKIT_TRUSTED_PROXIES` or `CREATOR_TOOLKIT_TRUSTED_NETWORKS`;
4. forward the original scheme and client address using `X-Forwarded-Proto` and
   `X-Forwarded-For`; and
5. keep the management and first-run Setup surface from becoming unintentionally
   public.

The application accepts one symmetrical forwarded-header hop and never trusts
all proxies by default. Proxy or public-network deployment changes require an
operator security review outside the supplied one-service Compose boundary.

## Image design

The Dockerfile restores and publishes with the official .NET SDK
`10.0.302-noble` image and runs on the official ASP.NET Core
`10.0.10-noble-chiseled-extra` image. `global.json` requests `10.0.100` with
`feature` roll-forward. That deliberately accepts the repository host's
`10.0.110` SDK and the container's `10.0.302` SDK, but only within .NET 10.0; it
cannot select .NET 11. Local SDK feature bands may therefore differ and local
builds are not claimed to be exactly reproducible. The digest-pinned builder is
the reviewed deterministic container build environment. The repository
validation workflow is configured to verify that reviewed SDK/container build
combination without publishing an image.

Both base-image tags are pinned to reviewed immutable multi-platform
manifest-list digests, and those manifests include `linux/amd64` and
`linux/arm64` children. The Dockerfile retains architecture-neutral application
build behavior. Base-manifest inspection alone is not treated as proof: the
`container-validation` GitHub Actions job performs a complete no-push Buildx
build for both architectures. ARM64 application verification remains pending
until the first post-push run of that job succeeds.

Only published application output and the readable `LICENSE` and
`THIRD_PARTY_NOTICES.md` files enter the final image. The latter accompanies the
embedded SecLists common-password resource and records its source, digest,
copyright, and MIT license. The Chiseled runtime has no shell or package manager,
and the final image contains no SDK, source, test packages, build tools,
Playwright browsers, `curl`, or `wget`. The application runs as numeric non-root
UID/GID `1654:1654`; application binaries and notices remain root-owned and are
not writable by that identity. Compose drops all Linux capabilities and enables
`no-new-privileges`. The named volume is seeded with narrowly scoped ownership
for the runtime identity; incompatible volume permissions cause startup to fail
rather than fall back to root or a different data location.
