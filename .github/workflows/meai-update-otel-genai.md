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
    title-prefix: "[automation] "
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
    title-prefix: "[automation] "
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
    max: 3
  mark-pull-request-as-ready-for-review:
    target: "*"
    required-title-prefix: "[automation] "
    required-labels: [automation]
  noop:
    report-as-issue: false

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

The repository skill `update-otel-genai-conventions` (in `.github/skills/`) is the
authority for **how** to analyze conventions and implement changes. This workflow
governs **lifecycle/idempotency** (which PR to touch and what state to leave it in).

## Step 1 -- Capture the upstream state

1. Read the latest state of `open-telemetry/semantic-conventions-genai` on its
   default branch:
   - Current **HEAD commit SHA** (`Upstream-Scan-Ref`).
   - Whether a tagged **release** exists/is published (`Upstream-Release`); record
     `none` while the conventions are still unreleased (Development stability).
   - The **core semantic-conventions dependency version** the gen-ai conventions
     target (`Core-Semconv-Dependency`).
2. Derive the **target version** `{version}` for naming:
   - If a gen-ai release is published, use that release version.
   - Otherwise use the core semantic-conventions dependency version it targets
     (e.g. `v1.42.0`).
   - The branch name is `update-otel-genai-to-v{version}` and the PR title is
     `[automation] Update open-telemetry/semantic-conventions-genai to v{version}`
     (the `[automation] ` prefix is applied automatically as a title prefix -- do
     not type it into the title you provide to safe outputs).

## Step 2 -- Build the plan via the skill

Invoke the `update-otel-genai-conventions` skill in **Plan-then-Implement** mode.
Base the analysis on the current state of `main` in this repository compared to the
upstream HEAD captured in Step 1. Produce the changes audit table (with 🔴/🟡/🟢
classifications) and an ordered list of work items, exactly as the skill describes.

Note: the skill's own "existing PR preflight" tells it to stop when an open PR
exists. **Override that for this workflow** -- do not stop; instead follow the
PR-handling decision in Step 3. Use the skill purely for the analysis, change
classification, implementation patterns, testing guidance, and validation commands.

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
| caught up with the upstream SHA | open (draft or not) **or** merged | **No-op** -- emit nothing: no comment, report, or issue. |
| **behind** the upstream SHA | open **draft** | **Incremental update.** Re-analyze against `main` plus what the branch already integrates; push one batch of commit(s) to the PR branch; refresh the PR body/tracking block; comment summarizing the delta. |
| **behind** the upstream SHA | open **non-draft** | **Advisory only -- do not implement.** Comment capturing the additional upstream changes to consider; note that re-marking the PR as draft lets the next scheduled run implement them, and that the workflow can be dispatched manually to run immediately. |
| **behind** the upstream SHA, **closed without merging**, or **not found** | merged-but-behind, closed, or absent | **Fresh PR** (Step 4). For a merged-but-behind PR, reference it and describe the updates layered on top. |

If the situation does not cleanly match a row, use judgment toward the overall goal:
**keep one draft PR continuously updated until the upstream release publishes.**

When matching multiple PRs, prefer the most recently updated open draft PR for the
same `{version}`.

## Step 4 -- Implement

Follow the skill's **Implementation Procedure** for each work item. Then validate.

**Fresh path** (merged-but-behind, closed, or no PR): make the changes on a clean
tree from `main`, then emit a `create-pull-request` safe output with:
- branch `update-otel-genai-to-v{version}` (kept verbatim),
- title `Update open-telemetry/semantic-conventions-genai to v{version}`,
- the body described in Step 5,
- draft state (configured by the safe output).

**Incremental path** (behind open draft): fetch and check out the existing PR branch, apply
only the differential work items on top of what is already integrated, and emit a
`push-to-pull-request-branch` safe output targeting that PR. Group the work into a
single batch of one or more commits. Then emit an `update-pull-request` to refresh
the body (and the tracking block) and an `add-comment` summarizing the delta.

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
  PRs, and the release-published note (Step 6).
- Use `mark-pull-request-as-ready-for-review` only once the release is published.
- When the matching PR is already caught up with the upstream SHA (or any run needs
  no visible change), **produce no output at all** -- do not post a no-op report,
  comment, or issue. Simply finish.
