# Documentation

Product guidance, durable design references and active work are kept apart.

| Folder | What it holds | How it changes |
| --- | --- | --- |
| [`docs/design/`](design/README.md) | **Engine design reference**: physical baseline, architecture, protocols, acceptance targets, measured evidence and historical discussions | Keep accepted requirements distinct from implementation and measured results; preserve decision history |
| [`docs/decisions/`](decisions/README.md) | **Decision records**: accepted guarantees, rationale and alternatives | Update the owning decision explicitly when a guarantee changes |
| [`docs/work/`](work/README.md) | **Working documents**: what is being built now. Epics, their goals, requirements and open questions. | Often, while the work is in progress. A working document describes intent, not delivered behavior. |

## Rules

- **A document lives next to the code it changes.** Documentation of this database and of
  the packages in this repository belongs here. The plan and rationale for using the
  database inside another product belong to that product's repository.
- **This repository is going to be public.** Nothing specific to a private product goes
  into it. A requirement that comes from a particular consumer is written here in general
  terms: what a consumer needs, not how that consumer is built.
- **A decision is not an implementation.** A document states plainly what exists and what
  is only planned.

## Contents

Start here:

- [Planning handoff](design/planning-handoff.md): the brief for planning the first production milestone and technical sprints.
- [Milestone 1 engine contract](design/milestone-1-engine-contract.md): physical mechanisms, component responsibilities, initial settings and verification gates.
- [Architecture](design/architecture.md): what the database is, on one page. The same
  text with diagrams is [design/index.html](design/index.html); open it from a checkout,
  because GitHub shows an HTML file as source.
- [Decision register](design/questions.md) and [decision records](decisions/README.md): current status and the reasoning behind it.
- [Performance requirements](design/performance-requirements.md) and [prototype evidence](design/prototype-evidence.md): targets versus measurements.
- [Synthetic prototype sources](../src/Prototypes/README.md): reproducible experiments and their limitations, outside the product solution.

The design transfer was completed on 14 November 2026. This repository now owns these
references; another workspace is not required to read the design or build the synthetic
experiments. Older discussion notes are retained for rationale. Their former open
questions are interpreted through the current register and implementation contract.
The working epics were brought in line with this baseline on 14 November 2026. They
remain planning inputs, not an override of accepted engine guarantees.

Working documents:

- [Current work and epics](work/README.md)
- [Development process](work/process.md): roles, sprints, review and severity of findings
