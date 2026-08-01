# Security Policy

## Supported versions

TemperedTyrant Creator Toolkit has not published a release. During pre-release
development, security fixes target the default branch. After the first release,
this file will list supported release lines; until then, no released version is
supported.

## Reporting a vulnerability

Do not open a public issue, discussion, or pull request for a suspected
vulnerability.

Once this repository is publicly hosted on GitHub, use its private
**Security** → **Report a vulnerability** form, backed by GitHub Security
Advisories. Include:

- the affected version, revision, or configuration;
- the impact and realistic attack scenario;
- reproduction steps or a minimal proof of concept;
- any known mitigations;
- whether the report or details have been shared elsewhere.

If private vulnerability reporting is not yet available, do not publish the
details. Contact a repository maintainer through a private channel listed on the
repository owner's hosting profile and ask for a secure reporting channel. This
avoids inventing a project domain or committing a personal address.

## What to expect

Maintainers will make a reasonable effort to:

- acknowledge a complete report within seven days;
- confirm whether it is in scope;
- coordinate validation, remediation, and disclosure;
- credit the reporter if requested and appropriate.

Response timing may vary because this is a volunteer open-source project. Do not
access data that is not yours, disrupt services, or test installations without
their operator's permission.

## Security scope

Reports about authentication, authorization, first-run takeover, secret
exposure, server-side request forgery, webhook authenticity, duplicate
publishing, unsafe diagnostics, dependency compromise, and container or upgrade
behavior are in scope when they affect TemperedTyrant Creator Toolkit.

Provider policy disputes, unsupported browser automation, or vulnerabilities in
an operator's unrelated reverse proxy or host are not project vulnerabilities,
although documentation defects that cause an unsafe default are welcome.

The detailed design and threat model are in [docs/SECURITY.md](docs/SECURITY.md).

## GitHub repository security controls

Checkpoint-10 read-only inspection confirmed that private vulnerability
reporting and the dependency graph are enabled. The repository also has an
active `Protect main` ruleset requiring pull requests, preventing deletion and
non-fast-forward updates, and requiring review-thread resolution; it currently
requires zero approving reviews because the project has one maintainer.

The available GitHub credential could not read Dependabot alert/security-update
status, secret scanning, push protection, CodeQL default setup, or the default
Actions token policy. No advanced CodeQL workflow is committed because doing so
without knowing the default-setup state could duplicate code scanning.

After checkpoint 10 reaches GitHub, a maintainer should open **Settings →
Security → Advanced Security** and:

1. confirm **Dependency graph** remains enabled;
2. enable **Dependabot alerts** and **Dependabot security updates** if either is
   disabled;
3. enable **Secret scanning** and **Push protection** if available and disabled;
4. under **Code scanning**, choose **Set up → Default**, ensure C# is selected,
   review the default query suite, and enable CodeQL default setup if it is not
   already enabled; do not add a duplicate advanced workflow.

Then open **Settings → Actions → General → Workflow permissions**, select the
read-only default token option, and leave **Allow GitHub Actions to create and
approve pull requests** disabled. After one successful workflow run, open
**Settings → Rules → Rulesets → Protect main**, add required status checks for
`dotnet-validation`, `container-validation`, and `dependency-review`, and keep
the approving-review count at zero unless another maintainer joins.

SPDX-License-Identifier: AGPL-3.0-only
