# Product definition

## Status

This document defines the intended version 1 product. SocialCreator is still in
the documentation and architecture phase; none of the described application
behavior is implemented yet.

## Purpose

SocialCreator is a free, open-source, self-hosted application that turns creator
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

SocialCreator is not an enterprise social-media management suite. Version 1
will not add campaign analytics, social inboxes, comment moderation, audience
CRM, advertising, billing, organization hierarchies, or SaaS tenancy.

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

An Admin manages integrations, credentials, event sources, templates,
announcements, workflows, schedules, operational settings, approvals, and
technical diagnostics. An Admin cannot manage users, transfer ownership, restore
backups, or perform Owner-only destructive maintenance.

### Editor

An Editor creates and edits drafts, previews rendered posts, and submits or
publishes announcements according to the workspace approval policy. An Editor
cannot administer integrations or approve their own work.

### Viewer

A Viewer has read-only access to announcements, publishing history, connection
status, and user-safe errors. A Viewer cannot access technical Debug data.

All permissions are enforced on the server. Interface visibility is not an
authorization boundary.

## Key user journeys

### First run

1. The operator starts the future Compose deployment.
2. The uninitialized application remains locked.
3. The operator invokes an explicit administrative CLI command to generate a
   cryptographically random, 30-minute bootstrap credential.
4. The command prints the credential only to its terminal.
5. The operator uses it once to create the initial Owner.
6. The bootstrap endpoint is permanently disabled for that installation.
7. The wizard guides the Owner through workspace time zone, public URL guidance,
   and an initial destination.

The public URL is optional for local manual, scheduled, Discord, Bluesky, and
generic outgoing-webhook publishing. The wizard explains when an inbound
provider callback requires a reachable public HTTPS URL.

### Add a local user

The Owner creates the account and role. SocialCreator generates a 24-hour,
single-use setup link, shows it once, and stores only a hash of its opaque token.
The Owner shares the link out of band. The new user chooses their own password.
The Owner may revoke or regenerate an unused link. No email service or public
registration is involved.

### Connect a destination

An Owner or Admin chooses a provider, enters its credentials, and runs a
connection test. The interface reports Healthy, Degraded, Reconnect required,
Misconfigured, or Unknown, with a recommended next action and a diagnostic
reference when needed. Stored credentials can be replaced but never retrieved.

Bluesky must request a dedicated app password, never the user's normal account
password. OAuth is a later enhancement, not a prerequisite for a local
installation.

### Prepare and publish an announcement

An authorized user chooses a template or begins a draft, reviews the default
message and any destination override, and previews the actual provider-specific
rendering before publishing or scheduling.

Each destination produces its own delivery result. A failure on one destination
does not prevent another from succeeding. Retryable failures are retried
durably; permanent or exhausted failures show an actionable next step.

### Approval

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

“Hold for review” is the default. SocialCreator never floods destinations with
several stale occurrences after recovery.

### Diagnose a failure

Normal pages show a concise explanation, recommended action, and diagnostic
reference. Owners and Admins may open a dedicated Debug page containing
sanitized technical details or create a sanitized diagnostic export. Secrets
are absent from all views, exports, audit records, and logs.

## Integration policy

Initial free destinations:

- Discord incoming webhooks;
- generic outgoing HTTPS webhooks;
- Bluesky using a dedicated app password and supported AT Protocol posting.

Planned event sources:

- a SocialCreator-defined signed incoming webhook;
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
