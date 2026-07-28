# Security Policy

## Supported versions

TemperedOps Creator Toolkit has not published a release. During pre-release
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
behavior are in scope when they affect TemperedOps Creator Toolkit.

Provider policy disputes, unsupported browser automation, or vulnerabilities in
an operator's unrelated reverse proxy or host are not project vulnerabilities,
although documentation defects that cause an unsafe default are welcome.

The detailed design and threat model are in [docs/SECURITY.md](docs/SECURITY.md).

SPDX-License-Identifier: AGPL-3.0-only
