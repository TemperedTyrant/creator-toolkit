# ADR 0006: Use a server-installed Discord bot over HTTP

- Status: Accepted
- Decision date: 2026-08-01

## Context

Creator Toolkit must discover Discord servers, channels, roles, and members;
calculate the bot's effective channel permissions; and let an authorized user
send one foreground announcement to several saved channels in one server.
Incoming webhooks are scoped to individual channels and do not provide the
guild-wide discovery needed by this setup flow.

## Decision

Use a Discord application and bot user owned by the installation operator. The
application calls the fixed Discord HTTP API v10 host with the bot token,
stores that token through the purpose-isolated encrypted secret store, and
never reveals it after entry.

The standard installation link requests only View Channels, Send Messages,
Embed Links, and Attach Files. Mention Everyone is optional and is not included
in the standard link. Creator Toolkit never requests a Discord username,
password, user token, or OAuth authorization to act as a person's account.

Checkpoint 13 has no Gateway connection, slash commands, interactions, incoming
Discord events, user-account token, self-bot behavior, public callback, or
inbound Discord endpoint. Publications execute synchronously through the Create
Message HTTP endpoint. No durable delivery record, queue, worker, schedule, or
background retry is introduced.

## Consequences

Benefits:

- one bot installation can discover and serve several channels and servers;
- live IDs and effective channel permissions can be revalidated before sends;
- the application needs no public callback URL or continuously connected
  Gateway client;
- explicit `allowed_mentions` payloads keep notification authority separate
  from user-entered Markdown.

Costs and constraints:

- an administrator must create and install a Discord application and bot;
- member search can depend on Discord application configuration, so a validated
  manual user-ID fallback remains available;
- foreground delivery has a bounded request lifetime and no durable recovery;
- the operator must deliberately grant Mention Everyone before authorized mass
  mentions can work.

## Alternatives considered

- **Incoming webhooks as the primary transport:** rejected because each channel
  needs a separate credential and webhooks cannot provide the required guild,
  channel, role, member, and effective-permission discovery.
- **A full Discord client framework and Gateway connection:** rejected because
  outbound HTTP is sufficient and a persistent event connection would expand
  runtime and security scope.
- **Discord user tokens or self-bots:** rejected because Creator Toolkit acts
  only as the operator's dedicated bot and must not automate a personal account.
- **Discord OAuth user login:** rejected because no user-account authorization
  is required for the bot-installation flow.

A future webhook-only destination may be added as a separate, deliberately
scoped transport if a channel-specific use case justifies it. It must not weaken
the fixed-host bot boundary or turn webhook URLs into a general outbound URL.

## Revisit when

Revisit this decision only if an explicitly authorized feature requires
incoming Discord events, interactions, or a separate webhook-only destination.
Those features require their own authentication, public-endpoint, lifecycle,
and abuse analysis.

SPDX-License-Identifier: AGPL-3.0-only
