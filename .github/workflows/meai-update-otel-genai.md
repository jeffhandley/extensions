---
name: "MEAI: Update Otel Gen-AI"
description: >-
  Continuously integrate OpenTelemetry gen-ai semantic-conventions updates into
  Microsoft.Extensions.AI by maintaining a draft pull request against main.

permissions:
  contents: read
  pull-requests: read
  issues: read
  actions: read

safe-outputs:
  create-pull-request:
    draft: true
    labels: [automation, area-ai]
    base-branch: main
    preserve-branch-name: true
    if-no-changes: "ignore"
    allowed-files:
      - "src/Libraries/Microsoft.Extensions.AI*/**"
      - "test/Libraries/Microsoft.Extensions.AI*/**"
      - "docs/**"
      - "samples/**"
  push-to-pull-request-branch:
    target: "*"
    labels: [automation]
    if-no-changes: "ignore"
    allowed-files:
      - "src/Libraries/Microsoft.Extensions.AI*/**"
      - "test/Libraries/Microsoft.Extensions.AI*/**"
      - "docs/**"
      - "samples/**"
  update-pull-request:
    target: "*"
    title: true
    body: true
  add-comment:
    target: "*"
    max: 1
  mark-pull-request-as-ready-for-review:
    target: "*"
    required-labels: [automation]
  noop:
    report-as-issue: false
  report-failure-as-issue: false

network:
  allowed:
    - defaults
    - dotnet
    - github
    - opentelemetry.io
    - "*.opentelemetry.io"
    - "*.azureedge.net"

tools:
  github:
    mode: gh-proxy
    toolsets: [default]

runs-on: ubuntu-latest
timeout-minutes: 350

checkout:
  fetch: ["*"]
  fetch-depth: 0

concurrency:
  group: meai-update-otel-genai

# Only run on a schedule for the canonical (non-fork) repository; allow manual
# dispatch anywhere (e.g. for testing in a fork).
if: ${{ github.event_name == 'workflow_dispatch' || github.event.repository.fork == false }}

on:
  schedule: daily
  workflow_dispatch:
  permissions: {}

# ###############################################################
# Select a PAT from the pool and override COPILOT_GITHUB_TOKEN.
# Run agentic jobs in an isolated `copilot-pat-pool` environment.
#
# When org-level billing is available, this will be removed.
# See `shared/pat_pool.README.md` for more information.
# ###############################################################
imports:
  - uses: shared/pat_pool.md
    with:
      environment: copilot-pat-pool

environment: copilot-pat-pool

engine:
  id: copilot
  env:
    COPILOT_GITHUB_TOKEN: |
      ${{ case(
        needs.pat_pool.outputs.pat_number == '0', secrets.COPILOT_PAT_0,
        needs.pat_pool.outputs.pat_number == '1', secrets.COPILOT_PAT_1,
        needs.pat_pool.outputs.pat_number == '2', secrets.COPILOT_PAT_2,
        needs.pat_pool.outputs.pat_number == '3', secrets.COPILOT_PAT_3,
        needs.pat_pool.outputs.pat_number == '4', secrets.COPILOT_PAT_4,
        needs.pat_pool.outputs.pat_number == '5', secrets.COPILOT_PAT_5,
        needs.pat_pool.outputs.pat_number == '6', secrets.COPILOT_PAT_6,
        needs.pat_pool.outputs.pat_number == '7', secrets.COPILOT_PAT_7,
        needs.pat_pool.outputs.pat_number == '8', secrets.COPILOT_PAT_8,
        needs.pat_pool.outputs.pat_number == '9', secrets.COPILOT_PAT_9,
        'NO COPILOT PAT AVAILABLE')
      }}
---

# MEAI: Update Otel Gen-AI

## Goal

Keep `Microsoft.Extensions.AI` aligned with the OpenTelemetry gen-ai semantic
conventions by continuously maintaining a **single draft pull request** against
`main` in this repository. Each daily run compares the upstream
`open-telemetry/semantic-conventions-genai` repository against `main`, produces or
refreshes an integration plan, implements it, validates it, and keeps the PR up to
date until the upstream release is published -- at which point the PR is marked
**Ready for Review**.

**The gen-ai conventions being unreleased is the normal, expected steady state.**
These conventions live in `Development` stability and are integrated from merged
upstream commits *before* any tagged release exists. The draft PR is built and
maintained from those unreleased upstream changes -- the absence of a published
release is **never** a reason to skip work. Publishing a release only flips the
finished draft PR to Ready for Review (Step 6); it does not gate whether the
integration happens. Do **not** no-op merely because `Upstream-Release` is `none`.

The repository skill `update-otel-genai-conventions` (in `.github/skills/`) is the
authority for **how** to analyze conventions and implement changes. This workflow
governs **lifecycle/idempotency** (which PR to touch and what state to leave it in).

## Step 1 -- Capture the upstream state

1. Read the latest state of `open-telemetry/semantic-conventions-genai` on its
   default branch:
   - Current **HEAD commit SHA** (`Upstream-Scan-Ref`).
   - Whether a tagged **release** exists/is published (`Upstream-Release`); record
     `none` while the conventions are still unreleased (Development stability). A
     value of `none` is the expected normal case and must **not** cause a no-op or
     short-circuit -- continue to Step 2 and integrate the unreleased changes.
   - The **core semantic-conventions dependency version** the gen-ai conventions
     target (`Core-Semconv-Dependency`).
2. Derive the **target version** `{version}` for naming:
   - If a gen-ai release is published, use that release version.
   - Otherwise use the core semantic-conventions dependency version it targets
     (e.g. `v1.42.0`).
   - The branch name is `update-otel-genai-to-v{version}` and the PR title is
     `Update open-telemetry/semantic-conventions-genai to v{version}`.

## Step 2 -- Build the plan via the skill

Invoke the `update-otel-genai-conventions` skill in **Plan-then-Implement** mode.
Base the analysis on the current state of `main` in this repository compared to the
upstream HEAD captured in Step 1. Produce the changes audit table (with 🔴/🟡/🟢
classifications) and an ordered list of work items, exactly as the skill describes.

Do the **full** convention cross-reference -- compare the gen-ai attributes,
metrics, events, and operation names defined at the upstream HEAD against what the
code actually emits (the implemented version from the doc comments). Drive this from
the conventions themselves, not from the upstream CHANGELOG: a `Development`-stability
upstream commonly shows `Unreleased`/no tag while still carrying merged convention
changes that are ahead of the implemented version. An empty CHANGELOG section or the
lack of a release tag is **not** evidence that there is nothing to integrate -- only
a completed cross-reference that finds zero deltas vs. the implemented version is.

Note: the skill's own "existing PR preflight" tells it to stop when an open PR
exists. **Override that for this workflow** -- do not stop; instead follow the
PR-handling decision in Step 3. Use the skill purely for the analysis, change
classification, implementation patterns, testing guidance, and validation commands.

### What counts as work -- track merged upstream changes aggressively

This workflow tracks **merged upstream gen-ai convention changes regardless of
whether a release is published.** Each merged upstream PR (e.g. every
`changelog.d/*.md` fragment, or any convention change present at the upstream HEAD
but not yet reflected in the implemented version) is a tracked change. Treat the
set of such changes ahead of the implemented version as the work to integrate.

Classify each tracked change as **actionable now** or **deferred**, and do **not**
let deferral collapse the run into a no-op:

- **Actionable now (must be implemented in this run):** any change that touches a
  convention item the code **already emits** -- a type/unit change (e.g.
  `gen_ai.request.top_k` `double` -> `int`), a requiredness or scope change, a
  rename, a sampling-relevance change, a new well-known value for an
  already-emitted attribute (e.g. a new `gen_ai.provider.name` value), or a new
  attribute/metric that maps onto a capability M.E.AI already instruments. These
  are integrated immediately; they are never deferred.
- **Deferred (tracked, not coded yet):** a brand-new attribute/metric/event/span
  with **no** current emission site, or a capability M.E.AI does not yet instrument
  (e.g. memory, retrieval, workflow, agent-framework spans), or a documentation-only
  clarification with no code impact. The skill's "no-orphan-constants" rule means
  you do **not** add the constant yet -- but you **still** record the item in the
  PR's changes table (🟢) so the draft PR documents the full upstream delta.

The "defer" classification applies to **individual constants**, never to the run as
a whole. As long as **one or more** tracked changes exist ahead of the implemented
version, there is a non-zero delta: open or maintain the draft PR (Step 3/4),
implement every actionable-now item, and document every deferred item in the changes
table. A run only no-ops when there are genuinely **zero** unintegrated upstream
gen-ai changes, or the tracked PR is already caught up on the upstream SHA (Step 3).

## Step 3 -- Find the existing PR and choose the action

Search this repository's pull requests (open **and** closed/merged) for a prior
update for this `{version}`. Match on the title pattern
`Update open-telemetry/semantic-conventions-genai to v{version}`, the
`update-otel-genai-to-v{version}` branch, the `automation` + `area-ai` labels, and
the `<!-- otel-genai-tracking:begin -->` block in the body. From any matching PR,
parse the tracking block to read its recorded `Upstream-Scan-Ref`.

Compare the PR's recorded `Upstream-Scan-Ref` to the upstream HEAD SHA from Step 1
and pick the matching action. The SHA comparison is the primary decision; the PR's
draft/merged state only matters when the PR is **behind**.

| If a matching PR is... | ...and it is | Action |
|---|---|---|
| caught up with the upstream SHA | open (draft or not) **or** merged | **No-op** -- no comment, report, issue, or PR; write the reason to the step summary (see no-op rules). |
| **behind** the upstream SHA | open **draft** | **Incremental update.** Re-analyze against `main` plus what the branch already integrates; push one batch of commit(s) to the PR branch; refresh the PR body/tracking block; comment summarizing the delta. |
| **behind** the upstream SHA | open **non-draft** | **Advisory only -- do not implement.** Comment capturing the additional upstream changes to consider; note that re-marking the PR as draft lets the next scheduled run implement them, and that the workflow can be dispatched manually to run immediately. |
| **behind** the upstream SHA, **closed without merging**, or **not found** | merged-but-behind, closed, or absent | **Fresh PR** (Step 4). For a merged-but-behind PR, reference it and describe the updates layered on top. |

If the situation does not cleanly match a row, use judgment toward the overall goal:
**keep one draft PR continuously updated until the upstream release publishes.**

Once a row other than No-op is selected (Fresh PR, Incremental, or Advisory),
proceed to carry it out. Do **not** re-derive a no-op afterward from the release
status or CHANGELOG state -- the no-op decision is made solely by the SHA comparison
above (and the zero-delta check from Step 2), never by whether a release is published.

When matching multiple PRs, prefer the most recently updated open draft PR for the
same `{version}`.

## Step 4 -- Implement

Follow the skill's **Implementation Procedure** for each work item. Then validate.

### File scope -- what this workflow may change

The pull request may modify **only** files under these paths (the safe output
enforces this as an allow-list, and the run fails if the patch touches anything
else):
- `src/Libraries/Microsoft.Extensions.AI*/**`
- `test/Libraries/Microsoft.Extensions.AI*/**`
- `docs/**`
- `samples/**`

**Never** edit, stage, or commit anything outside that set. In particular do **not**
touch: this workflow and its generated lock (`.github/**`, including
`.github/workflows/meai-update-otel-genai.*` and `.github/aw/**`), `global.json`,
`NuGet.config`, `Directory.Packages.props`, any `*.sln`/`SDK.sln*` solution files,
or dependency lockfiles. If a build step generates such files (e.g. `SDK.sln`),
delete them before producing output so they cannot leak into the patch.

### How to produce the patch (critical -- avoids fork base-resolution failures)

The patch the safe output turns into a PR is generated by diffing your commits
against the **exact commit that was checked out** (`GITHUB_SHA`). This is only
reliable if you do **not** create a local branch named after the PR target branch.
If you run `git checkout -b update-otel-genai-to-v{version}`, patch generation
falls back to a `merge-base` against the remote default branch, which on a fork
sweeps the entire fork divergence into the patch (hundreds of unrelated files) and
fails the run. So:

- **Fresh path** (merged-but-behind, closed, or no PR): stay on the branch that was
  checked out -- do **not** create or switch to a local branch, and do **not** run
  `git fetch`, `git reset`, `git rebase`, or `git merge`. Make your edits, delete any
  generated solution/build artifacts (`SDK.sln*`, `artifacts/`) so they cannot leak,
  then stage **only** the in-scope paths and commit them as a single commit on the
  current `HEAD`:
  `git add src/Libraries/Microsoft.Extensions.AI* test/Libraries/Microsoft.Extensions.AI* docs samples && git commit -m "Update open-telemetry/semantic-conventions-genai to v{version}"`.
  This makes the generated patch contain **only** your commit. Then emit a
  `create-pull-request` safe output with:
  - branch `update-otel-genai-to-v{version}` (kept verbatim -- the safe output
    creates the remote branch; you do not create it locally),
  - title `Update open-telemetry/semantic-conventions-genai to v{version}`,
  - the body described in Step 5,
  - draft state (configured by the safe output).
- **Incremental path** (behind open draft): the PR branch already exists on the
  remote, so here you **do** fetch and check out that existing branch
  (`git fetch origin update-otel-genai-to-v{version} && git checkout update-otel-genai-to-v{version}`),
  apply only the differential work items on top of what is already integrated, stage
  the in-scope paths, and commit one batch of one or more commits. Then emit a
  `push-to-pull-request-branch` safe output targeting that PR, an `update-pull-request`
  to refresh the body (and the tracking block), and a single `add-comment`
  summarizing the delta.

When the skill clones or fetches the upstream `semantic-conventions` repository for
analysis, do it **outside** this repository's working tree (e.g. under `/tmp`), never
inside the checkout, so upstream files never enter the patch.

For **both** implementation paths:
- The build must remain clean (no new warnings) and tests must pass. Use the skill's
  build/test commands (Linux/macOS form). Run a full `./build.sh -vs AI` restore,
  then `./build.sh -build -test`; remove any stale `SDK.sln*` first.
- Ensure **sufficient test coverage** for every new attribute/metric/emission --
  augment existing tests where possible rather than adding parallel test methods.
- Update any affected **samples and docs** in the repo so they reflect the new
  conventions.
- If the public API surface changed, regenerate API baselines and keep only the
  baseline updates for the libraries actually changed.
- Review the result thoroughly against the skill's review checklist before emitting
  output.

## Step 5 -- PR body and tracking block

Write the PR body following the skill's PR-description guidance: a changes table
covering **every** analyzed gen-ai change (not just those producing code changes),
grouped by version, using 🟢/🟡/🔴 indicators with the compensating change or
rationale for each.

Embed the machine-readable tracking block verbatim (so future runs can read prior
state). Fill every field from Step 1:

```text
<!-- otel-genai-tracking:begin -->
```yaml
Upstream-Repo: open-telemetry/semantic-conventions-genai
Upstream-Scan-Ref: <upstream HEAD commit SHA>
Upstream-Scan-Date: <ISO-8601 UTC timestamp of this run>
Upstream-Release: <release version or "none">
Core-Semconv-Dependency: <core semantic-conventions version>
DotnetExtensions-Implemented-Version: <gen-ai conventions version reflected in the code doc comments>
```
<!-- otel-genai-tracking:end -->
```

On every incremental update, refresh `Upstream-Scan-Ref` and
`Upstream-Scan-Date` to the values from the current run.

## Step 6 -- When the upstream release is published

If Step 1 found that the gen-ai conventions are now **published in a tagged
release** and a matching open PR exists:
- Ensure the integration is complete and validated for that released version.
- Mark the PR **Ready for Review** (`mark-pull-request-as-ready-for-review`).
- Update the PR body (`Upstream-Release` set to the published version) and add a
  comment stating the upstream release is published and the integration should now
  be reviewed and merged.

## Safe outputs and no-op rules

- Use `create-pull-request` only on the fresh path.
- Use `push-to-pull-request-branch` only on the incremental path (behind open draft).
- Use `update-pull-request` to refresh an existing PR body/title.
- Use `add-comment` for incremental summaries, advisory notes on behind non-draft
  PRs, and the release-published note (Step 6). At most **one** comment per run.
- Use `mark-pull-request-as-ready-for-review` only once the release is published.
- When the matching PR is already caught up with the upstream SHA (or any run needs
  no visible change), do **not** post a no-op report, comment, issue, or PR. Emit the
  `noop` safe output, and **also** write a short explanation to the GitHub Actions
  **step summary** (see below). Do not create any repository-visible artifact.
- A no-op is valid in only two cases: (a) a matching PR's recorded `Upstream-Scan-Ref`
  equals the upstream HEAD SHA, or (b) the completed Step 2 cross-reference finds zero
  merged upstream gen-ai convention changes ahead of the implemented version. Case (b)
  means **nothing upstream is unintegrated** -- it is **not** satisfied when upstream
  carries merged changes that you classified as deferred (new attributes without an
  emission site, uninstrumented capabilities, or doc-only clarifications). Those
  deferred items are a non-zero delta: they require an open/maintained draft PR that
  documents them, even though no constant is added for them yet. An unreleased upstream
  (`Upstream-Release: none`), an `Unreleased` CHANGELOG section, or the absence of a
  release tag are **never**, on their own, valid reasons to no-op.
- **No-op step summary:** whenever the run no-ops, append a concise Markdown
  explanation to the file at `$GITHUB_STEP_SUMMARY` (for example
  `echo "..." >> "$GITHUB_STEP_SUMMARY"`). This summary is attached to the workflow
  run only -- it is **not** a repository-visible report. Include: the target
  `v{version}`, the upstream HEAD SHA, the scan timestamp, which of the two no-op
  conditions was met, and -- when condition (a) applies -- the matched PR number/URL
  and its state (open draft / open / merged).
