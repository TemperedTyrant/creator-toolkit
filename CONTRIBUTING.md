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

The repository is in checkpoint-gated milestone 1 implementation. Add
application source code only within the currently approved checkpoint.

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

## Local workflow

The baseline .NET commands are:

```sh
dotnet restore
dotnet build --no-restore --disable-build-servers -m:1
dotnet test --no-build --disable-build-servers -m:1
dotnet format --verify-no-changes --no-restore
docker compose config --quiet
```

Additional integration or multi-architecture checks may be required by the
affected area. The Compose command does not apply until container files exist.

The Setup capability-leakage test uses the pinned Playwright test package and a
real headless Chromium process. After the first build, install that matching
browser runtime once before `dotnet test`:

```sh
pwsh tests/TemperedTyrant.CreatorToolkit.IntegrationTests/bin/Debug/net10.0/playwright.ps1 install chromium
```

This browser is test tooling only and is not an application runtime dependency.

## Automated validation

The `Repository validation` workflow is configured for pull requests targeting
`main`, pushes to `main`, and manual runs. Its stable checks are:

- `dotnet-validation`: restore, warning-free build, matching Playwright Chromium,
  all unit/integration/browser tests, formatting, EF model drift, vulnerable
  packages, deprecated production packages, and whitespace;
- `container-validation`: Compose validation/native smoke testing plus complete
  Buildx application builds for `linux/amd64` and `linux/arm64`; and
- `dependency-review`: the pull-request dependency delta, failing for newly
  introduced high or critical known vulnerabilities.

Validation failures are required failures, not informational checks. Reproduce
the named command locally, correct the underlying code, formatting, migration,
dependency, or container problem, and rerun the complete affected job. Do not
hide failures with warning-only settings or unrelated generated changes.

The production-project deprecation audit deliberately excludes the test
projects. NuGet classifies the existing xUnit 2 packages as legacy; migration to
xUnit v3 remains deferred to a separate compatibility review. It is not combined
with milestone-one security hardening and does not weaken the production-package
deprecation audit.

Dependabot checks NuGet, the Dockerfile base images, and GitHub Actions weekly
with small pull-request limits and narrowly related groups. It uses Dependabot's
default `dependencies` and ecosystem labels, which GitHub creates when needed.
There is no auto-merge behavior. Dependabot alerts and security updates remain
repository settings rather than effects of `.github/dependabot.yml`.

Some operating-system distributions package .NET reference packs without
optional NuGet package-pruning metadata. `Directory.Build.props` enables
`AllowMissingPrunePackageData` solely as a build-environment compatibility
workaround for those SDK packages. It is not an application setting, does not
change runtime behavior, and must not be used to suppress dependency
vulnerability auditing.

`SQLitePCLRaw.lib.e_sqlite3` is also a deliberate direct dependency. EF Core's
SQLite package selects the managed bundle and provider transitively, while the
embedded native SQLite asset is pinned separately so its security-sensitive
version remains explicit and independently auditable. Do not remove or change
that pin without inspecting the complete transitive graph and rerunning both
the vulnerable-package and deprecated-package checks.

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
