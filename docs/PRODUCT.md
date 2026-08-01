# TemperedTyrant Creator Toolkit product definition

## Status

TemperedTyrant Creator Toolkit is in pre-alpha development. The milestone 1
foundation, Markdown-backed announcement draft authoring with persistent media, and durable Discord bot
publishing are implemented. Owners, Admins, and Editors can create, edit,
archive, restore, search, and permanently
delete drafts; Viewers have read-only access. Owners and Admins manage Discord
bot connections and channel destinations, and Editors may also queue Drafts.
Confirmed publications are queued durably, processed by the in-process worker,
and shown in Publish History with independent destination outcomes. Scheduling,
creator events, approvals, and other providers remain unimplemented.

**Self-hosted tools and automation for creators and their teams.**

The public product family and future release identity is **TemperedTyrant Creator
Toolkit**. The application interface may use **Creator Toolkit** with
**A TemperedTyrant project**. The GitHub repository is
`TemperedTyrant/creator-toolkit`, and the repository and machine-readable
identifier remains `creator-toolkit`.

## Purpose

TemperedTyrant Creator Toolkit is a free, open-source, modular self-hosted toolkit
for creators and their teams. Creator Announcements will be its first
implemented and released module. In version 1, the application turns creator
events and scheduled or manual actions into reliable announcements. It should
make the path from “I went live” or “I published a video” to a set of
destination-specific posts understandable and dependable.

The product optimizes for:

- reliable delivery;
- easy setup by a nontechnical creator;
- visible connection and delivery health;
- safe collaboration in one creator workspace;
- actionable errors and supportable diagnostics;
- provider-policy compliance.

Version 1 is not a general-purpose creator suite or an enterprise social-media
management suite. It will not add stream alerts, overlays, soundboards, generic
automation, campaign analytics, social inboxes, comment moderation, audience
CRM, advertising, billing, organization hierarchies, or SaaS tenancy.

## Scope commitments

### Committed

The ten version 1 milestones in the [roadmap](ROADMAP.md) commit to Creator
Announcements functionality: Identity and RBAC, approvals, Discord, generic
outgoing webhooks, Bluesky, scheduling, Twitch and YouTube event-source
research, health, diagnostics, and release preparation.

### Planned

Likely post-v1 work includes:

- stream alerts;
- authenticated OBS browser-source overlays;
- a shared asset library;
- Generalized trigger-condition-action workflow implementation using the
  creator-event/action seam established in ADR 0005.

See [ADR 0005](DECISIONS/0005-creator-event-action-seam.md) for the seam
established by the current architecture.

### Exploratory

Research-only possibilities include:

- local soundboard playback;
- global hotkeys;
- an optional local creator agent;
- OBS WebSocket control;
- Stream Deck integrations;
- chat bot and moderation utilities;
- donation and commerce integrations;
- desktop application integration.

Planned and Exploratory items are neither release commitments nor guarantees
and have no assigned dates.

## Intended users

One installation represents one creator workspace. A workspace may have several
local users and may contain multiple:

- event sources;
- destination accounts;
- reusable templates;
- platform-specific message variants;
- announcement workflows;
- schedules.

Version 1 does not support multiple isolated workspaces, organizations, public
registration, email invitations, billing, or SaaS multi-tenancy.

## Roles

### Owner

The sole Owner has full control, including user management, ownership transfer,
key-protection settings, backups, restores, and destructive actions. The sole
Owner cannot be deleted, disabled, or demoted outside an atomic ownership
transfer.

### Admin

An Admin may create and manage Editor and Viewer accounts and may access
implemented operational settings and sanitized diagnostics. An Admin cannot
manage Owner or Admin accounts, transfer ownership, restore backups, or perform
Owner-only destructive maintenance. Product-area administration remains planned
until those areas are implemented.

### Editor

An Editor can create, edit, archive, restore, search, and delete announcement
drafts and may queue a reviewed Discord-specific message for durable delivery.
Editors cannot manage bot credentials or use mass mentions. Scheduling,
other integrations, and approval workflows are not
implemented.

### Viewer

A Viewer has read-only access to announcements and user-safe errors. A Viewer
cannot mutate announcements or access technical Debug data. Viewers may read
safe publication history but cannot cancel work.

All permissions are enforced on the server. Interface visibility is not an
authorization boundary.

## Implemented announcement authoring

Announcements use a compact Markdown-backed composer with a required internal
title of at most 200 Unicode scalar values and required message content of at
most 10,000 Unicode scalar values. The title identifies the draft inside
Creator Toolkit and is never inserted into Discord automatically. Message
paragraphs and line breaks are preserved as encoded source; content is never
stored or interpreted as HTML.

Each draft may contain up to four JPEG, PNG, WebP, or GIF images with a combined
unencrypted limit of 8 MiB. Images, alt text, spoiler state, featured-image
selection, and deterministic order are edited within the composer. Image bytes
are encrypted in SQLite with a media-specific Data Protection purpose and are
served only through the authenticated, no-store announcement preview endpoint.
They persist across restart when the database and key ring remain together.

New announcements start as Draft. Archived announcements are read-only until
restored. Permanent deletion requires a dedicated confirmation page. Each edit,
archive, restore, and delete operation is bound to the revision the user read;
when another mutation wins first, the stale operation is rejected instead of
overwriting current state. Successful mutations create transactional audit
records containing safe identifiers and operation metadata, never title or body
content.

The announcement list supports bounded pagination, status filtering, and
search over title and body. Durable Discord publishing and content-free Publish
History are implemented; schedules, other providers, and approvals are not.

## Key user journeys

### First run

1. The operator starts the supplied Compose deployment.
2. The uninitialized application is ready to serve Setup but normal application
   access remains locked.
3. The operator invokes an explicit administrative CLI command to generate a
   cryptographically random, 30-minute bootstrap credential.
4. The command prints the credential only to its terminal.
5. The operator uses it once to create the initial Owner.
6. The bootstrap endpoint is permanently disabled for that installation.
7. The Owner signs in and can manage local users and announcement drafts.

A public URL is optional for the implemented local application. Destination
setup, provider callbacks, scheduling, and external publishing are future work.

### Add a local user

The Owner creates Admin, Editor, or Viewer accounts. An Admin may create Editor
or Viewer accounts. Creator Toolkit generates a 24-hour, single-use activation
link, shows it once, and stores only a hash of its opaque token. The authorized
creator shares the link out of band. The new user chooses their own password.
An authorized manager may regenerate an unused link, which revokes the prior
one. No email service or public registration is involved.

### Connect a Discord destination

An Owner or Admin enters and validates the bot token for a dedicated Discord
application. The token is encrypted, replacement-only, and never displayed.
Creator Toolkit generates a least-privilege bot installation link, discovers
servers available to the bot, calculates effective channel permissions, and
saves selected text or announcement channels. Editors and Viewers cannot
manage credentials.

Bluesky must request a dedicated app password, never the user's normal account
password. OAuth is a later enhancement, not a prerequisite for a local
installation.

### Prepare and publish an announcement

Draft authoring and durable Discord publication are implemented. The user
chooses one server and up to ten saved channels, reviews the announcement's
single message source and stored images, selects plain or optional rich-embed
presentation, explicitly selects any permitted mentions, and
queues an immutable encrypted snapshot. The browser is redirected immediately
to Publish History while the in-process worker delivers each channel
independently.

Each destination produces its own delivery result. A failure on one destination
does not prevent another from succeeding. Transient failures use at most three
automatic retries after the initial attempt. Queued and retrying work survives
restart through SQLite leases and stable Discord nonces. Users may cancel
remaining work, but cancellation cannot remove a message already accepted by
Discord.

Later edits or removal of draft media do not change an already queued snapshot.
Persistent draft media is suitable as the source for a future scheduling
snapshot, but scheduling is not implemented.

### Approval

This workflow is planned and is not implemented.

The workspace has one Editor publishing policy:

- **Direct publishing:** Editors may publish and schedule directly.
- **Approval required:** Editors submit to an Owner or Admin.

An Editor cannot approve their own submission. Rejection returns the
announcement to Draft with a user-safe reason. Editing pending or approved
content invalidates its approval. An announcement that reaches its scheduled
time without required approval remains held.

### Schedule in creator time

The workspace has one configurable IANA time zone. Users edit and view schedules
in that zone. Each schedule chooses how to handle downtime:

- skip missed occurrences;
- hold one missed occurrence for review;
- publish only the latest missed occurrence.

“Hold for review” is the default. Creator Toolkit never floods destinations
with several stale occurrences after recovery.

### Diagnose a failure

Normal pages show a concise explanation, recommended action, and diagnostic
reference. Owners and Admins may open a dedicated Debug page containing
sanitized technical details or create a sanitized diagnostic export. Secrets
are absent from all views, exports, audit records, and logs.

## Integration policy

Initial free destinations:

- Discord bot HTTP publishing;
- generic outgoing HTTPS webhooks;
- Bluesky using a dedicated app password and supported AT Protocol posting.

Planned event sources:

- a project-defined signed incoming webhook;
- Twitch, after a free supported EventSub transport is confirmed;
- YouTube, after a free and quota-safe push or polling design is confirmed.

Reddit remains deferred or experimental until maintainers confirm a permitted,
free, and repeatable application registration and posting path. X is excluded.
Browser automation must not bypass an API, authentication requirement, price,
rate limit, or platform policy.

## Version 1 experience requirements

- Guided and takeover-resistant first-run setup.
- Clear callback and public URL guidance.
- Mobile-responsive server-rendered pages with minimal JavaScript.
- Visible health and reconnect actions; no silent disconnections.
- Platform connection testing.
- Actionable, provider-safe errors.
- Default messages with optional platform-specific overrides.
- Preview before publishing.
- Optional Editor approval.
- Durable retries and duplicate-event protection.
- Independent per-destination delivery history.
- Sanitized diagnostic exports.
- Media compatibility and reachability checks when media support is introduced.

## Success criteria

Version 1 is successful when a nontechnical creator can:

1. start a supported image with a near-default Compose configuration;
2. securely claim the installation and add collaborators;
3. connect a supported free destination;
4. preview and publish or schedule an announcement;
5. understand whether each destination succeeded;
6. recover safely from a transient failure or disconnected credential;
7. back up the single persistent volume;
8. obtain useful sanitized diagnostics without exposing credentials.

Reliability, clarity, and safe operation take priority over integration count.

## Post-v1 extension mechanisms

The primary product remains a self-hosted web application. Two extension
mechanisms may support post-v1 modules.

### Browser-source runtime

Planned authenticated web pages may be designed for OBS browser sources,
including alerts, overlays, goals, labels, timers, and media presentation.
Their design must preserve server-side authorization, secret isolation, audit
behavior, and sanitized diagnostics.

### Optional local creator agent

An exploratory, separately installed, explicitly paired component may provide
capabilities that require access to a creator's computer:

- playback through a selected local audio device;
- global hotkeys;
- local file access;
- OBS WebSocket communication;
- desktop application integration;
- local device discovery.

The local creator agent is not part of version 1 and is not required for the
default server installation. Its future pairing, authorization, update, and
failure model requires a separate design review.
