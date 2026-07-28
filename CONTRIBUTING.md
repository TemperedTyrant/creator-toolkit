# Contributing to TemperedTyrant Creator Toolkit

Thank you for helping build TemperedTyrant Creator Toolkit. The project welcomes
focused bug reports, design discussion, documentation improvements, tests, and
code once an implementation milestone is active.

By participating, you agree to follow the
[Code of Conduct](CODE_OF_CONDUCT.md).

## Before starting

1. Read the [product definition](docs/PRODUCT.md), the
   [architecture](docs/ARCHITECTURE.md), and the [roadmap](docs/ROADMAP.md).
2. Review accepted [architecture decisions](docs/DECISIONS/README.md).
3. Check existing issues and pull requests before starting overlapping work.
4. For a substantial feature or architecture change, open a design discussion
   before implementation.

The current repository phase is documentation and planning. Do not add
application source code until maintainers explicitly begin an implementation
milestone.

## Development principles

- Keep each change narrow enough to understand and review.
- Use repository-relative paths. Never commit personal paths, usernames,
  domains, IP addresses, tokens, or environment-specific configuration.
- Do not commit `.env` files, databases, key rings, backups, logs, or secrets.
- Prefer standard .NET and ASP.NET Core functionality over new dependencies.
- Use only open-source dependencies and build tools that do not require a paid
  hosted service.
- Preserve the one-container, one-volume, SQLite modular-monolith design unless
  an accepted ADR changes it.
- Preserve the Web, Core, and Infrastructure project boundaries inside the
  single application and deployable container.
- Keep provider-specific behavior behind provider-neutral interfaces.
- Keep version 1 focused on Creator Announcements. Planned and exploratory
  creator-toolkit modules do not belong in the ten version 1 milestones.
- Never bypass a provider's supported API, authentication, price, or policy.

## Future local workflow

Once milestone 1 creates the solution, the baseline commands will be:

```sh
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
docker compose config --quiet
```

Additional integration or multi-architecture checks may be required by the
affected area. Until those files exist, documentation changes should validate
Markdown structure, relative links, terminology, and portability.

## Tests and documentation

Behavior changes must include corresponding tests and documentation.

In particular:

- protected operations need allowed and denied role tests;
- authentication changes need security-stamp and stale-cookie tests;
- trigger handling needs duplicate-event tests;
- publishing needs retry and destination-failure-isolation tests;
- diagnostics and logging need redaction tests;
- webhook changes need SSRF, redirect, timeout, and size-limit tests;
- scheduling needs time-zone, daylight-saving, and missed-run tests.

Do not claim a feature is implemented until its tests and documentation describe
the shipped behavior.

## Pull requests

A pull request should:

- explain the user or maintainer problem;
- summarize the chosen approach and important tradeoffs;
- identify security or migration effects;
- link any relevant issue or ADR;
- list verification commands and results;
- call out checks that could not be run;
- avoid unrelated cleanup or formatting.

Architecture-significant changes require an ADR. Amend an accepted ADR only to
correct or clarify its record; use a new ADR to supersede a prior decision.

## Licensing contributions

By submitting a contribution, you agree that it is licensed under
`AGPL-3.0-only`, the repository's license. Add the SPDX identifier to future
source files where the language and project conventions support file headers:

```text
SPDX-License-Identifier: AGPL-3.0-only
```

Do not submit code or assets that cannot be distributed under compatible terms.

## Reporting security issues

Do not disclose a suspected vulnerability in an issue, discussion, or pull
request. Follow [SECURITY.md](SECURITY.md).
