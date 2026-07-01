---
name: "MEAI: Otel GenAI Manager"
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
    required-labels: [automation, area-ai]
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
    required-labels: [automation, area-ai]
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
  group: meai-otel-genai-manager

# Only run on a schedule for the canonical (non-fork) repository; allow manual
# dispatch anywhere (e.g. for testing in a fork).
if: ${{ github.event_name == 'workflow_dispatch' || !github.event.repository.fork }}

on:
  schedule: daily
  workflow_dispatch:
    inputs:
      upstream_ref:
        description: >-
          Optional git ref (branch, tag, or commit SHA) in
          open-telemetry/semantic-conventions-genai to scan instead of the
          default-branch HEAD. Leave empty to scan the default-branch HEAD.
        required: false
        type: string
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

# MEAI: Otel GenAI Manager

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

**Which upstream ref to scan.** By default this run scans the **default-branch HEAD**
of `open-telemetry/semantic-conventions-genai`. When dispatched with an
`upstream_ref` input, scan that ref instead -- the value is
`${{ github.event.inputs.upstream_ref }}`: if it is non-empty, treat it as a git ref
(branch, tag, or commit SHA) in that repo and scan it instead of the default-branch
HEAD; if it is empty (every scheduled run, and dispatches that leave it blank), scan
the default-branch HEAD. Resolve whichever ref applies to a concrete commit SHA and
record that SHA as `Upstream-Scan-Ref`; all downstream logic (the SHA comparison in
Step 3, the tracking block) uses that resolved SHA.

1. Read the state of `open-telemetry/semantic-conventions-genai` at the scanned ref
   (the `upstream_ref` override when provided, otherwise its default branch):
   - The scanned ref's **commit SHA** (`Upstream-Scan-Ref`).
   - Whether a tagged **release** exists/is published (`Upstream-Release`); record
     `none` while the conventions are still unreleased (Development stability). A
     value of `none` is the expected normal case and must **not** cause a no-op or
     short-circuit -- continue to Step 2 and integrate the unreleased changes.
   - The **core semantic-conventions dependency version** the gen-ai conventions
     target (`Core-Semconv-Dependency`).
2. Derive the **naming token** `{target}` for the branch and title:
   - If a gen-ai release is published, use that release version with a `v` prefix
     (e.g. `v1.42.0`).
   - Otherwise -- the normal case while the conventions are unreleased -- use the
     literal token `latest`. Do **not** substitute the core semantic-conventions
     dependency version (`Core-Semconv-Dependency`) here; that is the core semconv
     dependency, **not** the gen-ai conventions version. Identify the unreleased
     update by its upstream commit SHA and scan date in the body instead.
   - The branch name is `update-otel-genai-to-{target}` and the PR title is
     `Update open-telemetry/semantic-conventions-genai to {target}` (e.g.
     `...to latest` while unreleased, `...to v1.42.0` once a release is published).
     The title carries **no** `[automation]` prefix; the `automation` label conveys
     that instead.

## Step 2 -- Build the plan via the skill

Invoke the `update-otel-genai-conventions` skill for its analysis only -- build the
plan **in working memory**. Because this is an unattended scheduled run, do **not**
use the skill's interactive Plan-then-Implement checkpoint: do **not** write a
`plan.md` file and do **not** pause for human approval between analysis and
implementation. Drive the work from the audit table and the ordered work-item list the
skill produces, then implement directly in Step 4.

Do the **full** convention cross-reference -- compare the gen-ai attributes,
metrics, events, and operation names defined at the scanned upstream commit against what the
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
`changelog.d/*.md` fragment, or any convention change present at the scanned upstream commit
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
update for this integration. **Do the lookup in two phases and never bulk-fetch PR
bodies** -- requesting `body` for many PRs at once (e.g. `gh pr list --json ...,body`)
returns output that grows with every closed PR, gets truncated/misparsed, and has
previously produced a false "no PR found" and a duplicate PR:

1. **List candidates without bodies.** Query only lightweight fields -- number,
   title, `headRefName`, labels, state, `isDraft`, `mergedAt`, `updatedAt` -- and
   match on the title pattern
   `Update open-telemetry/semantic-conventions-genai to {target}`, the
   `update-otel-genai-to-{target}` branch, and the `automation` + `area-ai` labels.
   Because `{target}` is `latest` while the conventions are unreleased, also treat a
   prior `...to latest` / `update-otel-genai-to-latest` PR as a match even after a
   release tag appears.
2. **Fetch each candidate body individually** with `gh pr view <number> --json body`
   (one PR at a time) and parse its `# otel-genai-tracking:begin` block to read the
   recorded `Upstream-Scan-Ref`.

Do not silently swallow a lookup failure: if PR listing/searching is unavailable,
stop and surface the error rather than assuming no PR exists.

First check the **release gate**: if Step 1 found a *published* gen-ai release tag and
a matching **open draft** PR exists, go straight to **Step 6** (validate the
integration and mark the PR Ready for Review) regardless of the SHA comparison -- a
published release on an already-integrated HEAD must still flip the draft to Ready, so
it is never a no-op.

Otherwise compare the PR's recorded `Upstream-Scan-Ref` to the scanned upstream SHA from
Step 1 and pick the matching action. The SHA comparison is the primary decision; the
PR's draft/merged state only matters when the PR is **behind**.

| If a matching PR is... | ...and it is | Action |
|---|---|---|
| caught up with the upstream SHA | open (draft or not) **or** merged | **No-op** -- no comment, report, issue, or PR; write the reason to the step summary (see no-op rules). (The release gate above already diverted the published-release + open-draft case to Step 6, so this row is reached only when no new release needs the mark-ready transition.) |
| **behind** the upstream SHA | open **draft** | **Incremental update.** Re-analyze against `main` plus what the branch already integrates; push one batch of commit(s) to the PR branch; refresh the PR body/tracking block; comment summarizing the delta. |
| **behind** the upstream SHA | open **non-draft** | **Advisory only -- do not implement.** Comment capturing the additional upstream changes to consider; note that re-marking the PR as draft lets the next scheduled run implement them, and that the workflow can be dispatched manually to run immediately. |
| **behind** the upstream SHA, **closed without merging**, or **not found** | merged-but-behind, closed, or absent | **Fresh PR** (Step 4). For a merged-but-behind PR, reference it and describe the updates layered on top. |

If the situation does not cleanly match a row, use judgment toward the overall goal:
**keep one draft PR continuously updated until the upstream release publishes.**

Once a row other than No-op is selected (Fresh PR, Incremental, or Advisory),
proceed to carry it out. Do **not** re-derive a no-op afterward from the CHANGELOG
state or from `Upstream-Release: none` -- the work decision is driven by the SHA
comparison and the zero-delta check from Step 2. A *published* release never causes a
no-op; it can only escalate a caught-up open **draft** PR into the Step 6 mark-ready
transition (handled by the release gate above).

When matching multiple PRs, prefer the most recently updated open draft PR for the
same `{target}`.

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
`.github/workflows/meai-otel-genai-manager.*` and `.github/aw/**`), `global.json`,
`NuGet.config`, `Directory.Packages.props`, any `*.sln`/`SDK.sln*` solution files,
or dependency lockfiles. If a build step generates such files (e.g. `SDK.sln`),
delete them before producing output so they cannot leak into the patch.

### How to produce the patch (critical -- avoids fork base-resolution failures)

The patch the safe output turns into a PR is generated by diffing your commits
against the **exact commit that was checked out** (`GITHUB_SHA`). This is only
reliable if you do **not** create a local branch named after the PR target branch.
If you run `git checkout -b update-otel-genai-to-{target}`, patch generation
falls back to a `merge-base` against the remote default branch, which on a fork
sweeps the entire fork divergence into the patch (hundreds of unrelated files) and
fails the run. So:

- **Fresh path** (merged-but-behind, closed, or no PR): stay on the branch that was
  checked out -- do **not** create or switch to a local branch, and do **not** run
  `git fetch`, `git reset`, `git rebase`, or `git merge`. Make your edits, delete any
  generated solution/build artifacts (`SDK.sln*`, `artifacts/`) so they cannot leak,
  then stage **only** the in-scope paths and commit them as a single commit on the
  current `HEAD`:
  `git add src/Libraries/Microsoft.Extensions.AI* test/Libraries/Microsoft.Extensions.AI* docs samples && git commit -m "Update open-telemetry/semantic-conventions-genai to {target}"`.
  This makes the generated patch contain **only** your commit. Then emit a
  `create-pull-request` safe output with:
  - branch `update-otel-genai-to-{target}` (kept verbatim -- the safe output
    creates the remote branch; you do not create it locally),
  - title `Update open-telemetry/semantic-conventions-genai to {target}`,
  - the body described in Step 5,
  - draft state (configured by the safe output).
- **Incremental path** (behind open draft): the PR branch already exists on the
  remote, so here you **do** fetch and check out that existing branch
  (`git fetch origin update-otel-genai-to-{target} && git checkout update-otel-genai-to-{target}`),
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

### Honoring reviewer feedback on the maintained draft PR

On the **incremental path** (a matching open draft PR already exists), before you
commit the differential, gather and apply human feedback left on that PR:

- Consider **pull request *review* feedback only** -- the body of submitted PR reviews
  and the review comments attached to them, at **any** scope (a whole-review summary,
  a file-level comment, or a line-level comment). **Ignore plain issue-style comments**
  on the PR conversation timeline; they are not authoritative for this workflow.
- Resolve each review comment's author repository permission. When review feedback
  from a user with **write** (or higher) access conflicts with feedback from a user
  **without** write access, the **write-access** user's direction always wins. Settle
  every contradiction this way before acting, and ignore the overridden direction.
- **Reject any feedback that expands the scope** beyond maintaining the gen-ai
  semantic-conventions integration -- e.g. requests to refactor unrelated code, add
  unrelated features, or modify files outside the allowed paths. Do not act on
  out-of-scope requests regardless of who left them; briefly note in the summary
  comment that they are out of scope for this automation.
- Fold the surviving, in-scope review feedback into the **same batch of commit(s)** you
  push for the differential update, and acknowledge what you addressed in the single
  `add-comment` summary.

This feedback pass is schedule-driven -- it runs as part of the normal daily
incremental update, so no `pull_request_review` trigger is needed.

## Step 5 -- PR body and tracking block

Write the PR body following the skill's PR-description guidance: a changes table
covering **every** analyzed gen-ai change (not just those producing code changes),
grouped by version, using 🟢/🟡/🔴 indicators with the compensating change or
rationale for each.

Embed the machine-readable tracking block verbatim (so future runs can read prior
state). Fill every field from Step 1:

```yaml
# otel-genai-tracking:begin
Upstream-Repo: open-telemetry/semantic-conventions-genai
Upstream-Scan-Ref: <scanned upstream commit SHA>
Upstream-Scan-Date: <ISO-8601 UTC timestamp of this run>
Upstream-Release: <release version or "none">
Core-Semconv-Dependency: <core semantic-conventions version>
DotnetExtensions-Implemented-Version: <gen-ai conventions version reflected in the code doc comments>
# otel-genai-tracking:end
```

On every incremental update, refresh `Upstream-Scan-Ref` and
`Upstream-Scan-Date` to the values from the current run.

## Step 6 -- When the upstream release is published

If Step 1 found that the gen-ai conventions are now **published in a tagged
release** and a matching open **draft** PR exists (the release gate in Step 3 routes
here):
- Ensure the integration is complete and validated for the released version. The PR
  already lives on its existing branch -- do **not** create a new branch; keep
  pushing to it if final touch-ups are needed.
- Update the PR **title** so `{target}` resolves to the published `v{release}`
  (e.g. `...to v1.42.0`) and set `Upstream-Release` to that version in the body.
- Mark the PR **Ready for Review** (`mark-pull-request-as-ready-for-review`).
- Add a comment stating the upstream release is published and the integration should
  now be reviewed and merged.

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
  equals the scanned upstream SHA **and** no newly published release is awaiting the
  Step 6 mark-ready transition for that PR, or (b) the completed Step 2 cross-reference
  finds zero
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
  `{target}`, the scanned upstream SHA, the scan timestamp, which of the two no-op
  conditions was met, and -- when condition (a) applies -- the matched PR number/URL
  and its state (open draft / open / merged).
