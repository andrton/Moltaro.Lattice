# Public distribution boundary

This standalone repository owns the product contract, the engine design reference,
accepted decisions and synthetic experiments. Parent workspaces and other projects
are outside its source and publication boundary. A reader or contributor does not
need access to the previous design workspace.

## Included material

- Generic database requirements and clearly labelled unimplemented engine features.
- Accepted decisions, alternatives, protocols and the initial implementation baseline.
- Deterministic synthetic experiment sources with public SDK/package prerequisites.
- Aggregate synthetic measurements with units, methodology and limitations.
- The reference directory-data fixture and optimized managed comparison requirements.

The design transfer preserved the architecture's Markdown and HTML, SVG assets,
schema example, discussion records, evidence and all twelve synthetic prototype
projects. The former standalone README is retained as the [design overview](overview.md),
updated to distinguish this collection from the repository's existing facade.

The experiments require no employer repositories, private package feeds, operational
datasets, captured application traffic or environment configuration. Redacting names
does not make a private dataset or private source suitable for this repository.

## Excluded material

Credentials, connection files, raw datasets/results, application-specific comparison
tools, logs, build outputs and machine-local configuration were not transferred.
Their exclusion does not remove the summarized synthetic evidence in the maintained
documents. Ignore rules supplement review; they do not make embedded secrets safe.
Example credentials must remain unmistakable placeholders.

## Commands and dependencies

Product build commands belong to the [repository README](../../README.md).
Synthetic experiment commands run from `src`; each experiment documents its own
resource bounds. They use the .NET 10 SDK and preserve their measured package versions
through local Central Package Management and lockfiles where present. They are not
part of the product solution or release packages.

PostgreSQL-backed experiments require a local server and an ignored connection file
supplied by the operator. Memory-only checks need no database service. Raw experiment
output stays under ignored `src/.local`. Architecture HTML generation uses PowerShell
7 from the repository root:

```powershell
pwsh -NoProfile -File docs/design/Build-Documentation.ps1
```

## Publication

Review the exact staged file list before publication. Do not import unrelated parent
repository history, private fixtures or local connection files. A clean current tree
alone does not prove an existing remote history is clean. The documentation transfer
does not itself commit, push, create a release or change the repository's license.
