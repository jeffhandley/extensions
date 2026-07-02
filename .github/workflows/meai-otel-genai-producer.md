---
name: "MEAI: Otel GenAI Producer"
description: >-
  Produce or refresh the single draft pull request that integrates OpenTelemetry
  gen-ai semantic-conventions updates into Microsoft.Extensions.AI. Invoked per-target
  by the MEAI Otel GenAI Manager (workflow_call) or manually (workflow_dispatch).

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
  group: meai-otel-genai-producer

# Only run on a schedule for the canonical (non-fork) repository; allow manual
# dispatch anywhere (e.g. for testing in a fork).
if: ${{ github.event_name == 'workflow_dispatch' || !github.event.repository.fork }}

on:
  # Invoked per-target by the MEAI Otel GenAI Manager as a reusable
  # workflow so this runs in the manager's run context and inherits its actor,
  # satisfying gh-aw activation's role check.
  workflow_call:
    inputs:
      target:
        description: "Single discovery target (JSON) from meai-otel-genai-manager-discover.sh."
        required: true
        type: string
  # Manual dispatch for standalone testing: provide a target JSON, or just an
  # upstream_ref (the setup resolves the rest), or nothing (scan default-branch HEAD).
  workflow_dispatch:
    inputs:
      target:
        description: "Single discovery target (JSON). Leave empty to resolve from upstream_ref."
        required: false
        type: string
      upstream_ref:
        description: >-
          Optional git ref (branch, tag, or commit SHA) in
          open-telemetry/semantic-conventions-genai to scan instead of the
          default-branch HEAD. Leave empty to scan the default-branch HEAD.
        required: false
        type: string
  permissions: {}

# Before the agent runs, deterministically prepare the run: resolve the upstream scan
# target (from the manager's `target` input, or a standalone dispatch's upstream_ref /
# default HEAD), discover and classify the maintained draft PR (ours / blocked /
# none), and compute the recommended lifecycle action. The agent consumes target.json
# instead of discovering state itself. The file is uploaded in the agent artifact.
steps:
  - name: Set up the run context
    env:
      GH_TOKEN: ${{ github.token }}
      TARGET_JSON: ${{ inputs.target }}
      UPSTREAM_REF: ${{ inputs.upstream_ref }}
    run: bash .github/scripts/meai-otel-genai-producer-setup.sh

# After the agent runs, guard against manifest drift: every full-body pull-request write
# must carry the tracking block and a non-empty Upstream-Scan-Ref. A PR/update body that
# lost this identity means the run drifted off its contract, so fail before it can publish.
post-steps:
  - name: Validate agent output identity
    run: |
      set -euo pipefail
      out=/tmp/gh-aw/agent_output.json
      if [ ! -f "$out" ]; then
        echo "::notice::No agent output to validate"; exit 0
      fi
      # Only full-body PR writes carry the tracking block. Skip append/prepend updates
      # (partial bodies) and non-PR items (comments, no-op).
      idx=$(jq -r '(.items // []) | to_entries[]
        | select(.value.type=="create_pull_request"
            or (.value.type=="update_pull_request"
                and ((.value.operation // "replace")=="replace")))
        | select((.value.body // "") != "")
        | .key' "$out" 2>/dev/null || true)
      if [ -z "$idx" ]; then
        echo "::notice::No full-body PR-writing items -- nothing to validate"; exit 0
      fi
      rc=0
      while IFS= read -r i; do
        [ -n "$i" ] || continue
        typ=$(jq -r ".items[$i].type" "$out")
        body=$(jq -r ".items[$i].body" "$out")
        miss=""
        printf '%s' "$body" | grep -q 'otel-genai-tracking:begin' || miss="$miss begin-marker"
        printf '%s' "$body" | grep -q 'otel-genai-tracking:end'   || miss="$miss end-marker"
        printf '%s' "$body" | grep -Eq 'Upstream-Scan-Ref:[[:space:]]*[0-9a-fA-F]{7,}' || miss="$miss Upstream-Scan-Ref"
        if [ -n "$miss" ]; then
          echo "::error::$typ item #$i is missing required tracking identity:$miss"
          rc=1
        fi
      done <<< "$idx"
      [ "$rc" -eq 0 ] && echo "Agent output identity validated."
      exit $rc

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

## Step 0 -- Read the run context (authoritative)

Before you do anything, read the `target.json` file the host setup wrote under
`/tmp/gh-aw/agent/` (it is the authoritative input for this run -- do not
re-discover this state yourself):

- **`target.json`** -- the resolved scan target and PR discovery:
  - `upstream_repo`, `upstream_ref`, `upstream_sha` -- the exact upstream commit to
    scan (already resolved; use `upstream_sha` as `Upstream-Scan-Ref`, do not re-resolve).
  - `upstream_release` -- the latest published gen-ai release tag, or `none`.
  - `desired_branch` -- the evergreen PR branch name (`update-otel-genai-to-latest`).
  - `pr` -- the maintained PR number, or empty when none exists.
  - `pr_state`, `pr_is_draft`, `pr_recorded_sha`, `pr_recorded_release` -- the
    discovered PR's state and the `Upstream-Scan-Ref` / `Upstream-Release` it records.
  - `classification` -- one of:
    - `ours` -- an open `automation`+`area-ai` PR on `desired_branch` that already
      carries our `otel-genai-tracking` block. Maintain it.
    - `blocked` -- a PR occupies `desired_branch` but is **not** our automation PR (a
      human owns it). **Stand down**: emit a `noop` explaining a human-owned PR holds
      the branch, and make no other output.
    - `none` -- no PR on `desired_branch`; the fresh path applies.
  - `action` -- the setup's recommended lifecycle action (`produce` or `noop`). When
    it is `noop` **and** `classification` is `ours` (caught up on the SHA, no new
    release), you may **early-exit**: emit a single `noop` with the reason and do
    **not** run the expensive build. For every other case run the full analysis and let
    your Step 3 decision be authoritative -- `action` is only a hint.

If `classification` is `blocked`, stop now with a `noop`. If `action` is `noop` and
`classification` is `ours`, stop now with a `noop`. Otherwise continue.

## Step 1 -- Capture the upstream state

**Which upstream ref to scan.** The host setup already resolved this into
`target.json`: scan `upstream_sha` (the concrete commit for the manager's target, a
standalone `upstream_ref` dispatch, or the default-branch HEAD). Record `upstream_sha`
as `Upstream-Scan-Ref`; all downstream logic (the SHA comparison in Step 3, the
tracking block) uses that resolved SHA. Do **not** re-resolve the ref yourself.

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

## Step 3 -- Choose the action from the setup's discovery

The host setup already discovered the maintained PR and recorded it in `target.json`
(Step 0) -- do **not** re-list or bulk-fetch PR bodies yourself. Use its fields:

- `classification` = `none` -> no open PR on `desired_branch`: the **Fresh PR** path.
  For a nicer body you may do **one** lightweight lookup of a prior *closed/merged*
  `automation`+`area-ai` PR on `update-otel-genai-to-latest` to reference (query only
  number/state/mergedAt -- never bulk-fetch bodies); if the lookup is unavailable, skip
  the reference rather than failing.
- `classification` = `blocked` -> a human owns a PR on `desired_branch`: emit a `noop`
  saying so and make no other output.
- `classification` = `ours` -> compare `pr_recorded_sha` to `upstream_sha`
  and pick the row below.

First check the **release gate**: if `upstream_release` is a published tag (not `none`)
and the PR is an **open draft**, go straight to **Step 6** (validate the integration and
mark the PR Ready for Review) regardless of the SHA comparison -- a published release on
an already-integrated HEAD must still flip the draft to Ready, so it is never a no-op.

Otherwise compare `pr_recorded_sha` to `upstream_sha` and pick the matching action. The
SHA comparison is the primary decision; the PR's draft state only matters when behind.

| If the maintained PR is... | ...and it is | Action |
|---|---|---|
| caught up with the upstream SHA | open (draft or not) **or** merged | **No-op** -- no comment, report, issue, or PR; write the reason to the step summary (see no-op rules). (The release gate above already diverted the published-release + open-draft case to Step 6, so this row is reached only when no new release needs the mark-ready transition.) |
| **behind** the upstream SHA | open **draft** (`ours`) | **Incremental update.** Re-analyze against `main` plus what the branch already integrates; push one batch of commit(s) to the PR branch; refresh the PR body/tracking block; comment summarizing the delta. |
| **behind** the upstream SHA | open **non-draft** | **Advisory only -- do not implement.** Comment capturing the additional upstream changes to consider; note that re-marking the PR as draft lets the next scheduled run implement them, and that the workflow can be dispatched manually to run immediately. |
| `classification` = `none`, or a prior PR is **closed without merging** / **merged-but-behind** | absent, closed, or merged-but-behind | **Fresh PR** (Step 4). For a merged-but-behind PR you referenced, describe the updates layered on top. |

If the situation does not cleanly match a row, use judgment toward the overall goal:
**keep one draft PR continuously updated until the upstream release publishes.**

Once a row other than No-op is selected (Fresh PR, Incremental, or Advisory),
proceed to carry it out. Do **not** re-derive a no-op afterward from the CHANGELOG
state or from `Upstream-Release: none` -- the work decision is driven by the SHA
comparison and the zero-delta check from Step 2. A *published* release never causes a
no-op; it can only escalate a caught-up open **draft** PR into the Step 6 mark-ready
transition (handled by the release gate above).

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
`.github/workflows/meai-otel-genai-manager.*`, `.github/workflows/meai-otel-genai-producer.*`,
`.github/scripts/meai-otel-genai-*`, and `.github/aw/**`), `global.json`,
`NuGet.config`, `Directory.Packages.props`, any `*.sln`/`SDK.sln*` solution files,
or dependency lockfiles. If a build step generates such files (e.g. `SDK.sln`),
delete them before producing output so they cannot leak into the patch.

### How to produce the patch (critical -- avoids fork base-resolution failures)

The patch the safe output turns into a PR is generated by diffing your commits
against the **exact commit that was checked out** (`GITHUB_SHA`). This stays clean
only if `HEAD` never **switches** onto another branch. Do **not** run
`git checkout -b update-otel-genai-to-{target}` or `git switch -c ...`: switching
branches makes patch generation fall back to a `merge-base` against the remote
default branch, which on a fork sweeps the entire fork divergence into the patch
(hundreds of unrelated files) and fails the run. Creating a branch **ref** that
points at the current commit without switching to it is safe (and required -- see
below). So:

- **Fresh path** (merged-but-behind, closed, or no PR): stay on the commit that was
  checked out -- do **not** `git switch`/`git checkout` to another branch, and do
  **not** run `git fetch`, `git reset`, `git rebase`, or `git merge`. Make your edits,
  delete any generated solution/build artifacts (`SDK.sln*`, `artifacts/`) so they
  cannot leak, then stage **only** the in-scope paths and commit them as a single
  commit on the current `HEAD`:
  `git add src/Libraries/Microsoft.Extensions.AI* test/Libraries/Microsoft.Extensions.AI* docs samples && git commit -m "Update open-telemetry/semantic-conventions-genai to {target}"`.
  Now choose the PR branch name. The canonical name is `desired_branch` from
  `target.json` (`update-otel-genai-to-latest`). Before using it, check whether a branch
  with that name already exists on the remote -- a stale branch can linger from a
  previously closed PR:
  `git ls-remote --heads origin "$DESIRED_BRANCH"` (where `$DESIRED_BRANCH` is
  `target.json`'s `desired_branch`).
  - If the remote has **no** such branch, use `desired_branch`.
  - If the branch **already exists**, do **not** overwrite, delete, or force-recreate
    it. Instead suffix the name with the current workflow run id (the `$GITHUB_RUN_ID`
    environment variable) as `{desired_branch}_{run_id}`, and remember
    that you deviated so Step 5 can record it in the PR body.
  Call the chosen name `{branch}`. Create the local branch **ref** for `{branch}`
  pointing at that commit **without switching to it** -- the `create-pull-request`
  safe output pins this ref to build the bundle and fails with
  `Needed a single revision` if it is absent:
  `git branch {branch} HEAD`.
  Because `HEAD` never moves, the generated patch still contains **only** your commit.
  Then emit a `create-pull-request` safe output with:
  - branch `{branch}` (the ref you just created -- the safe output pushes it to the
    remote),
  - title `Update open-telemetry/semantic-conventions-genai to {target}`,
  - the body described in Step 5,
  - draft state (configured by the safe output).
- **Incremental path** (behind open draft, `ours`): the PR branch already
  exists on the remote (`target.json`'s `desired_branch`), so here you **do** fetch and
  check out that existing branch
  (`git fetch origin "$DESIRED_BRANCH" && git checkout "$DESIRED_BRANCH"`),
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
- If restore/build **cannot run** because the internal Azure DevOps feeds are
  unreachable (e.g. `pkgs.dev.azure.com` returns 401/403, or no `project.assets.json`
  is produced), do **not** fall back to a manual review and do **not** open or update a
  PR with code you could not compile. Treat it as a hard failure: emit a
  `report_incomplete` safe output whose `reason` states that the internal NuGet feeds
  were unreachable and the change could not be built or tested, and emit **no**
  `create-pull-request`, `push-to-pull-request-branch`, `update-pull-request`,
  `add-comment`, or `noop` output. The `report_incomplete` signal fails the workflow
  run so the outage is surfaced for investigation instead of shipping unvalidated code.
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

If you had to suffix the PR branch name with the run id because the canonical
`update-otel-genai-to-{target}` branch already existed on the remote (see Step 4's
fresh path), add a `> [!NOTE]` block near the top of the PR body stating that the
canonical branch name was already in use by a lingering branch, so this PR uses the
`update-otel-genai-to-{target}_{run_id}` branch instead. Omit the block entirely when
the canonical name was used.

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

On every incremental update, refresh `Upstream-Scan-Ref` and `Upstream-Scan-Date` to
the values from the current run.

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
