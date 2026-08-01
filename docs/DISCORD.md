# Discord bot setup and durable manual publishing

Creator Toolkit connects a Discord application and bot that you own. It does
not create a Discord account, convert a user account into a bot, provide a
shared hosted bot, use self-bots, or ask for a Discord username, password, user
token, or personal-account OAuth authorization. A dedicated Creator Toolkit bot
with only the permissions below is strongly recommended.

## Create and connect the bot

1. In the Discord Developer Portal, create a dedicated application and add its
   bot user.
2. Copy or reset the bot token in the portal. Treat it like a password; do not
   put it in `.env`, Compose, a ticket, source control, or logs.
3. As a Creator Toolkit Owner or Admin, open **Destinations**, select **Add
   Discord bot**, enter a local connection name and the bot token, and save.
4. Creator Toolkit validates the current bot user and application with Discord,
   then stores the token encrypted. It can only be replaced or deleted and is
   never displayed, exported, or returned.
5. Open the connection and select **Install bot in Discord**. A Discord member
   who is allowed to install applications chooses the server and approves the
   installation. Creator Toolkit needs no public callback URL.
6. Return to the connection, choose the installed server, and save one or more
   usable text or announcement channels.

The standard install link requests only:

- View Channels;
- Send Messages;
- Embed Links; and
- Attach Files.

It does not request Administrator, application commands, moderation, message
history, webhook management, or Mention Everyone. If the team intentionally
wants `@everyone`, `@here`, or otherwise unmentionable roles, a Discord server
administrator may separately grant Mention Everyone to the bot. Creator
Toolkit works normally without it.

The bot uses outbound Discord HTTP API v10 calls only. There is no Gateway
connection, slash command, incoming Discord event, or inbound public Discord
endpoint.

## Publish a Draft

Open a Draft announcement and select **Publish to Discord**. Owner, Admin, and
Editor users may choose one configured connection, one server, and up to ten
saved channels in that server. Confirmation durably queues the reviewed
publication in SQLite and redirects to Publish History. The publication worker
revalidates the bot installation, channel identity, enabled state, and
effective channel permissions before every delivery attempt.

The compact publication composer begins with the announcement's one stored
Markdown message. Its internal title is labeled as not sent and is never added
to a Discord message or embed. Choose either:

- a plain Discord message of at most 2,000 Unicode characters; or
- one rich embed using the same message as its description, with optional
  HTTPS title link, color, footer (2,048), HTTPS image, and HTTPS thumbnail. The
  total embed text limit is 6,000 characters.

The form supports Discord Markdown including emphasis, underline,
strikethrough, code, quotes, masked links, spoiler text, channel mentions,
timestamps, and custom emoji syntax. Creator Toolkit does not implement a
Markdown renderer and does not claim its source view exactly matches Discord.

Mentions are disabled by default. Every request includes an explicit
`allowed_mentions` object. Selected roles and users are allowlisted by ID;
mention-looking Markdown cannot enable broad parsing. Member search is bounded
to 25 results and is never persisted or logged. If Discord does not make member
search available, enter a Discord user ID; Creator Toolkit validates membership
before the user can be selected.

Only Owners and Admins can select `@everyone`, `@here`, or an otherwise
unmentionable role. They must confirm the high-impact action, and the bot must
have effective Mention Everyone permission in every selected channel. Editors
cannot use mass mentions.

## Images and delivery

Up to four JPEG, PNG, WebP, or GIF images totaling at most 8 MiB can be saved
with a draft. Creator Toolkit checks extension, media type, and file signature,
generates outbound filenames, supports alt text and spoilers, and permits one
featured image. Draft image bytes are encrypted in SQLite and survive restart;
they are never stored as plaintext files, session state, diagnostics, audit, or
logs. Confirmation copies the selected images into the separate immutable,
encrypted publication snapshot, so later draft edits cannot alter queued work.

The advanced publication options may instead provide one absolute HTTPS image
URL without credentials when no stored images are selected.
Creator Toolkit passes an accepted URL to Discord and never fetches it, so it
does not act as an image proxy or application-side SSRF fetcher. Remote images
cannot be marked as spoiler images.

One in-process hosted worker delivers queued channels independently. A browser
disconnect or application restart does not discard confirmed work. Transient
rate limits, Discord 5xx responses, connection failures, and timeouts receive
up to three automatic retries after the initial attempt using bounded delays.
Authentication, permission, validation, and missing-destination failures are
permanent. A stable per-channel Discord nonce is reused through retries and
crash recovery. This provides at-least-once processing with bounded Discord
nonce deduplication, not exactly-once delivery.

Publish History shows safe aggregate, destination, and attempt outcomes without
showing the protected message snapshot. Once every destination is terminal,
Creator Toolkit transactionally removes the encrypted payload and keeps only
safe history metadata. Owner, Admin, and Editor users can cancel remaining
queued work; cancellation cannot retract messages already accepted by Discord.
Back up and restore the SQLite database and Data Protection key ring together,
as described in the deployment guide, so pending protected work remains usable.

Scheduling, creator-event automation, other providers, manual replay,
Discord-message editing/deletion, forums, media channels, threads, DMs,
incoming events, and slash commands are not implemented.

Discord behavior and limits in this guide follow Discord's official
[OAuth2](https://docs.discord.com/developers/topics/oauth2),
[permissions](https://docs.discord.com/developers/topics/permissions),
[message](https://docs.discord.com/developers/resources/message), and
[rate-limit](https://docs.discord.com/developers/topics/rate-limits)
documentation.

SPDX-License-Identifier: AGPL-3.0-only
