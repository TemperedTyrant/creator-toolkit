# SocialCreator

SocialCreator is a planned free, open-source, self-hosted web application for
content creators. It will detect creator events—such as going live or publishing
a video—and reliably publish customized announcements to selected destinations.

> [!IMPORTANT]
> SocialCreator is currently in the documentation and architecture phase. There
> is no runnable application or container image yet.

SocialCreator is deliberately focused on creator announcements. It is not
intended to become an enterprise social-media management, analytics, engagement,
or advertising suite.

## Planned version 1

- One creator workspace per installation.
- Multiple local users with Owner, Admin, Editor, and Viewer roles.
- Multiple event sources, destination accounts, templates, and announcement
  workflows in that workspace.
- Guided first-run Owner setup protected by an operator-generated bootstrap
  token.
- Preview, platform-specific message variants, and optional Editor approval.
- Durable publishing, bounded retries, duplicate-event protection, and
  independent per-destination results.
- Connection tests, visible health and reconnect states, actionable errors, and
  sanitized diagnostics.
- A server-rendered, mobile-responsive interface with minimal JavaScript.

Initial free destinations are:

- Discord incoming webhooks;
- Bluesky using a dedicated app password and supported AT Protocol posting;
- generic outgoing HTTPS webhooks.

Planned trigger sources include a signed generic incoming webhook, Twitch, and
YouTube. Twitch and YouTube work will begin only after their supported free
setup paths are confirmed.

Reddit is deferred or experimental until a permitted, free, repeatable setup
path is verified. X will not be implemented. SocialCreator will not use browser
automation to bypass an API, price, authentication system, or platform policy.

## Technical direction

The proposed version 1 stack is:

- .NET 10 LTS;
- ASP.NET Core Razor Pages and ASP.NET Core Identity;
- Entity Framework Core with SQLite;
- ASP.NET Core hosted background services;
- a modular monolith in one application container;
- one named persistent volume;
- separate unit and integration test projects.

Docker Compose will be the default deployment method. Direct localhost use will
not require a public domain. A configured public URL will be needed only for
features whose providers call back into the installation.

See [Product](docs/PRODUCT.md), [Architecture](docs/ARCHITECTURE.md),
[Security design](docs/SECURITY.md), [Deployment](docs/DEPLOYMENT.md), and the
[Roadmap](docs/ROADMAP.md) for the current design.

## Project principles

- Reliable creator announcements over broad feature count.
- Open-source and self-hosted first.
- Portable configuration with no machine-specific assumptions.
- Provider-neutral core domain boundaries.
- Server-side authorization for every protected operation.
- No committed secrets and no secret retrieval after entry.
- User-safe errors in normal pages; technical details only in protected
  diagnostics and redacted structured logs.
- Independent destination delivery so one provider cannot block another.

## Contributing and security

The repository is preparing for public development. Read
[CONTRIBUTING.md](CONTRIBUTING.md) before proposing changes and follow the
[Code of Conduct](CODE_OF_CONDUCT.md).

Do not report vulnerabilities in public issues. Follow
[SECURITY.md](SECURITY.md) for private reporting instructions.

## License

Copyright notices for individual contributions remain with their contributors.
SocialCreator is licensed under the GNU Affero General Public License version 3
only. See [LICENSE](LICENSE).

SPDX-License-Identifier: AGPL-3.0-only

