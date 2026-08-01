# Discord bot setup and manual publishing

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
saved channels in that server. The form revalidates the bot installation,
channel identity, enabled state, and effective channel permissions immediately
before sending.

Choose either:

- a plain Discord message of at most 2,000 Unicode characters; or
- one rich embed with optional message text, title (256), description (4,096),
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

One optional JPEG, PNG, WebP, or GIF of at most 8 MiB can be uploaded for the
current publication. Creator Toolkit checks the extension, media type, and file
signature, generates the outbound filename, supports alt text, and can mark an
uploaded attachment as a Discord spoiler. Uploaded bytes remain in bounded
memory for the current request and are never written to SQLite, `/app/data`, a
temporary file, session state, diagnostics, audit, or logs.

Alternatively, provide one absolute HTTPS image URL without credentials.
Creator Toolkit passes an accepted URL to Discord and never fetches it, so it
does not act as an image proxy or application-side SSRF fetcher. Remote images
cannot be marked as spoiler images.

Delivery occurs immediately and sequentially in the foreground. Each channel
gets an independent safe result. One channel failure does not prevent later
channels from being attempted. A short Discord rate limit may be honored once
within the overall bound; authentication, permission, validation, and missing-
destination failures are not retried. A stable per-channel Discord nonce limits
accidental duplicate confirmation posts within Discord's supported nonce
window, but this is not durable exactly-once delivery.

Scheduling, durable publishing jobs, automatic background retries, publishing
history, Discord-message editing/deletion, forums, media channels, threads,
DMs, incoming events, and slash commands are not implemented.

Discord behavior and limits in this guide follow Discord's official
[OAuth2](https://docs.discord.com/developers/topics/oauth2),
[permissions](https://docs.discord.com/developers/topics/permissions),
[message](https://docs.discord.com/developers/resources/message), and
[rate-limit](https://docs.discord.com/developers/topics/rate-limits)
documentation.

SPDX-License-Identifier: AGPL-3.0-only
