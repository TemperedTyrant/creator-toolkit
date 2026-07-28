# Architecture decision records

Architecture decision records (ADRs) capture decisions that materially constrain
TemperedOps Creator Toolkit's structure, operation, security, licensing, or
compatibility. They explain why a decision was made so later contributors can
change it deliberately rather than accidentally.

## Status values

- **Proposed:** under discussion and not yet binding.
- **Accepted:** the current project direction.
- **Deprecated:** retained for history but no longer recommended.
- **Superseded:** replaced by a newer ADR, which must be linked.
- **Rejected:** considered but not adopted.

## Format

New ADRs use the next four-digit number and a short kebab-case title:

```text
NNNN-short-decision-title.md
```

Each record contains:

1. title;
2. status and decision date;
3. context;
4. decision;
5. consequences, including drawbacks;
6. alternatives considered;
7. conditions that would justify revisiting it.

Do not rewrite an accepted decision to make history match a new direction.
Correct factual or typographical errors in place; otherwise add a new ADR that
supersedes the old one and update this index.

## Index

| ADR | Status | Decision |
| --- | --- | --- |
| [0001](0001-modular-monolith.md) | Accepted | Use a .NET modular monolith |
| [0002](0002-sqlite-as-default-database.md) | Accepted | Use SQLite as the version 1 default database |
| [0003](0003-single-container-deployment.md) | Accepted | Ship one application container and one named volume |
| [0004](0004-agpl-3.0-only-license.md) | Accepted | License the project under AGPL-3.0-only |
| [0005](0005-creator-event-action-seam.md) | Accepted | Establish a reusable creator-event/action seam without a premature generic workflow engine |

SPDX-License-Identifier: AGPL-3.0-only
