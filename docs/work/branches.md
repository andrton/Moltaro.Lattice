# Branches and releases

How the repository is branched, what `main` refuses, and how a version is released. The
steps of a sprint are in the [development process](process.md); this document says where
its commits go.

## In short

- **`main` is the only permanent branch.** It always builds and passes its tests, and
  every release is cut from it. There is no `develop` branch and no release branch.
- **All work happens on a short branch** and reaches `main` through a pull request.
  Nobody commits to `main` directly: not an agent, not the owner.
- **A release is a tag** `vX.Y.Z` on a commit of `main`. Pushing the tag publishes the
  packages.

`main` can be both the place where work is integrated and the place releases come from
because of how features are built here. A feature enters as contract: what is not
implemented throws `NotSupportedException` and its tests are skipped. A merged part of an
epic therefore never leaves `main` broken or half-working, and a sprint is merged as soon
as it closes instead of waiting for the whole epic.

## Branches

| Branch | Cut from | For | Lives | Merged by |
| --- | --- | --- | --- | --- |
| `main` | — | The integrated, green state; the source of every release | Always | — |
| `epic-<n>/sprint-<nn>-<name>` | `main` | One sprint of an epic, on the full track | Until the sprint closes | Pull request, merge commit |
| `fix/<name>`, `docs/<name>`, `chore/<name>` | `main` | The light track: a fix, a document, repository upkeep | A day or two | Pull request, squash |

Examples: `epic-1/sprint-03-index-search`, `fix/child-reconnect-timeout`,
`docs/branching`.

- **A sprint is merged with a merge commit**, not squashed: the series contract, tests,
  implementation, fixes stays visible in the history, and the merge commit names the
  sprint. **A light-track change is squashed** into one commit.
- **One sprint of an epic at a time.** The next sprint branch is cut from `main` after the
  previous one is merged. Light-track branches may exist next to it.
- **A branch that fell behind `main` is updated by merging `main` into it.** A branch with
  an open pull request is never force-pushed.
- **The branch is deleted after the merge.**

A maintenance branch `release/<major>.<minor>`, cut from a release tag to fix a released
version after `main` has moved on, is not needed yet and is not set up. It is added when
the first such fix is.

## Who does what

- **An agent** commits to the work branch of its task and stops there. It does not push,
  open a pull request or merge unless the owner tells it to.
- **The coordinator** creates the sprint branch and, when the sprint is closed, reports
  that it is ready to merge.
- **The owner** pushes the branch or has it pushed, opens the pull request and merges it.
  Merging is the owner's approval; there is no second reviewer on GitHub. The review
  itself happened in the sprint, before the pull request.

## What `main` refuses

Enforced by the repository ruleset in
[`.github/rulesets/main.json`](../../.github/rulesets/main.json):

| Refused | Why |
| --- | --- |
| A direct push | Every change is seen as a pull request and checked by CI before it lands |
| A pull request whose `build` check has not passed, or whose branch is behind `main` | `main` stays green: what was tested is what gets merged |
| A pull request with unresolved review conversations | A question asked on the change is answered before it lands |
| A force-push, or deleting the branch | History of `main` is never rewritten |
| A rebase-merge | Only the two merge methods above are used |

No approving review is required, because there is one owner. Nobody is exempt, the
repository administrator included: agents act with the owner's credentials, so an
exemption for the owner would be an exemption for every agent. In an emergency the owner
switches the ruleset off in the repository settings, and on again afterwards.

CI runs on every pull request and on every push to `main`, also for a change that touches
only documents: a required check that does not run would block the pull request.

## Releases

- **Versions** follow semantic versioning. While the engine is not there the version is
  `0.x.y` with a pre-release label, for example `0.1.0-alpha.1`.
- **A release is made by the owner**: with `main` green, tag the commit and push the tag.
  The [Publish](../../.github/workflows/publish.yml) workflow builds, tests and packs that
  commit and pushes the packages; it refuses a tag that is not on `main`.
- **A release tag is permanent.** The ruleset in
  [`.github/rulesets/release-tags.json`](../../.github/rulesets/release-tags.json) refuses
  moving or deleting a `v*` tag. A wrong release is followed by a new version, never by a
  re-tag.
- **An agent does not create a release tag.**

```bash
git tag v0.1.0-alpha.1
```

```bash
git push origin v0.1.0-alpha.1
```

## Setting it up on GitHub

The rules are files in the repository so that they can be reviewed, but GitHub does not
read them by itself. They are applied once, after the first push, and again whenever a
file changes. With the GitHub CLI signed in as the owner, from the repository root:

```bash
gh api repos/{owner}/{repo}/rulesets --input .github/rulesets/main.json
```

```bash
gh api repos/{owner}/{repo}/rulesets --input .github/rulesets/release-tags.json
```

```bash
gh api -X PATCH repos/{owner}/{repo} -F allow_merge_commit=true -F allow_squash_merge=true -F allow_rebase_merge=false -F delete_branch_on_merge=true
```

The same files can be imported by hand: repository **Settings → Rules → Rulesets → New
ruleset → Import a ruleset**.

GitHub enforces rulesets in a public repository on every plan, and in a private
repository only on a paid plan. While the repository is private on a free plan, the rules
hold by this document and by [AGENTS.md](../../AGENTS.md).

## The first commit

The repository starts with one commit made directly on `main`, before the rules are on.
Everything after it follows this document.
