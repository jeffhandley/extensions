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
    required-labels: [automation]
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
  threat-detection: false

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
timeout-minutes: 120

checkout:
  fetch: ["*"]
  fetch-depth: 0

concurrency:
  group: meai-update-otel-genai

# Schedule runs only on the canonical (non-fork) repository; manual dispatch runs
# anywhere (e.g. testing in a fork). A `pull_request_review` auto-responds only on the
# canonical repo and only while the maintained PR is still a **draft** -- once it is
# Ready for Review, reviews no longer trigger the workflow. (gh-aw also refuses PR
# checkout in a forked runtime, so review-triggered runs only function on the canonical
# repo; forks use manual dispatch for testing.)
if: ${{ github.event_name == 'workflow_dispatch' || (github.event_name == 'schedule' && github.event.repository.fork == false) || (github.event_name == 'pull_request_review' && github.event.pull_request.draft && github.event.repository.fork == false) }}

on:
  schedule: daily
  pull_request_review:
    types: [submitted]
  roles: [admin, maintainer, write]
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
2. Let the **skill** decide the target token and PR title -- do not derive or
   restate them here. Per the skill's `references/pr-description.md`, the target is
   a concrete `v{version}` only when a gen-ai release or published schema URL exists;
   while the conventions are unreleased (the normal case) the target token is
   `latest`. Throughout this workflow, `{target}` means that skill-determined token
   (`latest` or `v{version}`); the skill owns the title text and the
   `latest`-vs-version choice. Do **not** identify this workflow's PR by a fixed
   branch name -- a closed PR can leave its branch behind, and reusing the name
   collides on the next fresh creation. Instead, a fresh PR uses a **unique,
   run-scoped** branch `update-otel-genai-to-{target}-{run-id}` (where `{run-id}` is
   the `$GITHUB_RUN_ID` env var), and the workflow's PR is always found by **content**
   (Step 3), never by branch.

## Step 2 -- Build the plan via the skill

Invoke the `update-otel-genai-conventions` skill in **Plan-then-Implement** mode.
Base the analysis on the current state of `main` in this repository compared to the
scanned upstream commit captured in Step 1. Produce the changes audit table (with 🔴/🟡/🟢
classifications) and an ordered list of work items, exactly as the skill describes.

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
whether a release is published.** Each merged upstream change present at the
scanned upstream commit but not yet reflected in the implemented version (e.g. every
`changelog.d/*.md` fragment) is a tracked change. Treat the set of such changes
ahead of the implemented version as the work to integrate.

Use the **skill** to classify each tracked change -- its
`references/change-classification.md` is the authority for what is **actionable
now** (🔴 code change / 🟡 minor action on an item the code already emits) versus
**deferred** (🟢 *Constant not yet emitted*, *No client exists*, *Server-side only*,
or *Documentation only*). Do not restate that taxonomy here; rely on the skill.

The one rule this workflow adds on top of the skill's classification is a
**lifecycle invariant**: deferral applies to **individual constants, never to the
run as a whole**, and must not collapse the run into a no-op. The skill's
"no-orphan-constants" rule means a deferred item adds no constant yet -- but you
**still** record it (🟢) in the PR's changes table so the draft PR documents the
full upstream delta. As long as **one or more** tracked changes exist ahead of the
implemented version, there is a non-zero delta: open or maintain the draft PR
(Step 3/4), implement every actionable-now item, and document every deferred item.
A run only no-ops when there are genuinely **zero** unintegrated upstream gen-ai
changes, or the tracked PR is already caught up on the upstream SHA (Step 3).

## Step 3 -- Find the existing PR and choose the action

**If this run was triggered by a `pull_request_review`** (a write-permission reviewer
submitted a review), the reviewed PR `#${{ github.event.pull_request.number }}` is the
candidate maintained PR. Confirm it is the workflow's maintained tracking PR (the labels
+ sentinel below) and still a **draft**, then proceed with an incremental revision
(Step 4) that incorporates the review feedback. The `if` gate already blocks review
events once the PR is Ready for Review, so a non-draft review never reaches here.

Search this repository's pull requests (open **and** closed/merged) for a prior
update maintained by this workflow, matching by **PR content, not branch name**:
the `automation` + `area-ai` labels together with the
`# otel-genai-tracking:begin` sentinel inside the `## Tracking state` yaml block (the skill-owned title, which
reads `latest` until a version publishes, is a secondary signal). Matching by content
is deliberate: a closed PR can leave its branch behind, so a branch's mere existence
says nothing about whether an active PR exists.

**Use this exact search -- do not improvise the label filter.** Pass each label as a
**separate** `--label` flag; a single comma-joined `--label "automation,area-ai"`
matches one nonexistent combined label and returns nothing:

```
gh pr list --repo <owner/repo> --state all --label automation --label area-ai \
  --json number,title,state,isDraft,headRefName,body
```

Then select the maintained PR **locally** as the one whose **body contains the literal
string `otel-genai-tracking:begin`** -- do **not** rely on
`gh ... --search "otel-genai-tracking"`, which does not reliably match text inside a
code fence. From any matching PR, parse the
tracking block to read its recorded `Upstream-Scan-Ref`, and note the PR's **state**
(open draft / open non-draft / merged / closed-without-merge) and its actual head
branch.

**Never create a duplicate.** If an **open** PR carrying those labels and that sentinel
exists, it **is** the maintained PR -- act on it per the table below (incremental
update, advisory, or version correction) and do **not** emit `create-pull-request`.
Only the closed / merged-but-behind / not-found rows below may create a fresh PR.

Compare the PR's recorded `Upstream-Scan-Ref` to the scanned upstream SHA from Step 1
and pick the action using **both** the PR's state and the SHA comparison. A
**closed-without-merge** PR is never a no-op -- it always means start fresh, caught
up or not: a closed PR was abandoned or superseded, so its recorded SHA being current
does **not** mean the integration shipped.

| If a matching PR is... | ...and it is | Action |
|---|---|---|
| **closed without merging** | caught up **or** behind | **Fresh PR** (Step 4). The closed PR did not ship; start a new draft. Reference it and describe what carries over. |
| caught up with the upstream SHA | open (draft or non-draft) **or** merged | **No-op** -- no comment, report, issue, or PR; write the reason to the step summary (see no-op rules). |
| **behind** the upstream SHA | open **draft** | **Incremental update.** Re-analyze against `main` plus what the branch already integrates; push one batch of commit(s) to the PR's existing head branch; refresh the PR body/tracking block; comment per the comment rules below (no separate summary comment on a run that pushes commits -- gh-aw already notifies). |
| **behind** the upstream SHA | open **non-draft** | **Advisory only -- do not implement.** Comment capturing the additional upstream changes to consider; note that re-marking the PR as draft lets the next scheduled run implement them, and that the workflow can be dispatched manually to run immediately. |
| **behind** the upstream SHA, or **not found** | merged-but-behind or absent | **Fresh PR** (Step 4). For a merged-but-behind PR, reference it and describe the updates layered on top. |

If the situation does not cleanly match a row, use judgment toward the overall goal:
**keep one draft PR continuously updated until the upstream release publishes.**

Once a row other than No-op is selected (Fresh PR, Incremental, or Advisory),
proceed to carry it out. Do **not** re-derive a no-op afterward from the release
status or CHANGELOG state -- the no-op decision is made solely by the SHA comparison
above (and the zero-delta check from Step 2), never by whether a release is published.

When matching multiple PRs, prefer the most recently updated open draft PR (matched
by content).

### Correct the title/body when the skill's target changes

Whenever you act on a PR (incremental update, advisory, or release-published
handling), re-derive the target token and title from the skill and check that the
PR's **title and body still match**. The common case: a PR opened while unreleased
reads `latest`, and the skill now resolves a concrete `v{version}` because a release
or schema URL published -- retitle and update the body via `update-pull-request`.
Likewise correct any genuinely wrong version reference. This correction never
applies to a PR you are intentionally leaving untouched under the next rule.

### A prior published-version PR is still open and unmerged

A separate case from the table above: a PR targets an **earlier** gen-ai version that
has since been **published**, that PR is **still open and not merged** in this repo,
and the current run finds upstream changes **after** that published version (a newer
version, or additional post-release changes). In that case:

- **Do not update that PR** -- it is the deliverable for its own published version and
  should be reviewed and merged as-is. Do not fold the newer changes into it, and do
  not correct/rewrite it for the newer version.
- **Do not open a competing PR** for the newer changes while that published-version PR
  is still awaiting merge (this preserves the one-PR-at-a-time goal). The newer changes
  are picked up as a fresh PR on a later run once the published-version PR merges (the
  merged-but-behind row of the table).
- Instead, leave a **single advisory comment** on the open PR indicating that the next
  version is already accumulating changes. Use the **same comment shape and content**
  as the behind-non-draft advisory: capture the additional upstream changes to
  consider, and state they will be addressed in a follow-up update after this PR is
  merged, and that the workflow can be dispatched manually to run immediately.

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
If you run `git checkout -b update-otel-genai-to-{target}-{run-id}`, patch generation
falls back to a `merge-base` against the remote default branch, which on a fork
sweeps the entire fork divergence into the patch (hundreds of unrelated files) and
fails the run. So:

- **Fresh path** (merged-but-behind, closed-without-merge, or no PR): stay on the
  branch that was checked out -- do **not** create or switch to a local branch, and
  do **not** run `git fetch`, `git reset`, `git rebase`, or `git merge`. Make your
  edits, delete any generated solution/build artifacts (`SDK.sln*`, `artifacts/`) so
  they cannot leak, then stage **only** the in-scope paths and commit them as a single
  commit on the current `HEAD` (use the skill's PR title as the commit subject):
  `git add src/Libraries/Microsoft.Extensions.AI* test/Libraries/Microsoft.Extensions.AI* docs samples && git commit -m "<skill PR title>"`.
  This makes the generated patch contain **only** your commit. Then emit a
  `create-pull-request` safe output with:
  - branch `update-otel-genai-to-{target}-{run-id}` -- a **unique, run-scoped** name
    (`{run-id}` = `$GITHUB_RUN_ID`) so it never collides with a branch a closed PR
    left behind; kept verbatim (the safe output creates the remote branch; you do not
    create it locally),
  - title from the skill's `references/pr-description.md` (`...to {target}`),
  - the body described in Step 5,
  - draft state (configured by the safe output).
- **Incremental path** (behind open draft): use the open draft PR's **actual head
  branch** read in Step 3 (call it `{pr-branch}` -- do not re-derive it), which
  already exists on the remote, so here you **do** fetch and check it out
  (`git fetch origin {pr-branch} && git checkout {pr-branch}`). **Always start by
  bringing the branch up to date with `main`:**
  `git fetch origin main && git merge -X theirs origin/main` (favor `main` on any
  conflict). Then **gather the PR's review feedback** (see "Review feedback on the
  maintained PR" below) and apply the differential work -- the upstream delta plus any
  actionable review feedback -- on top of what is already integrated; stage the
  in-scope paths and commit one batch of one or more commits. Then emit a
  `push-to-pull-request-branch` safe output targeting that PR and an
  `update-pull-request` to refresh the body and tracking block (comment per the
  "Safe outputs and no-op rules").

When the skill clones or fetches the upstream `semantic-conventions` repository for
analysis, do it **outside** this repository's working tree (e.g. under `/tmp`), never
inside the checkout, so upstream files never enter the patch.

### Review feedback on the maintained PR

When revising an existing maintained PR -- on any incremental run, and **especially a
`pull_request_review`-triggered run** -- act on the PR's **review feedback at any
scope**, and on **nothing else**:
- **Review summaries**: submitted reviews and their summary-level bodies/states, via
  `gh api repos/<owner>/<repo>/pulls/<number>/reviews`.
- **Code/content review comments**: inline diff comments **and file-level review
  comments**, via `gh api repos/<owner>/<repo>/pulls/<number>/comments`.

**Only PR review feedback counts.** Do **not** respect any ordinary issue/PR comment
(`.../issues/<number>/comments`); regular comments are ignored entirely.

**Write-permission precedence.** Determine each reviewer's repository permission
(authoritative: `gh api repos/<owner>/<repo>/collaborators/<login>/permission`; the
review's `author_association` is a fallback). When a **write-permission** reviewer
(admin / maintain / write) and a reviewer **without** write permission give
contradicting feedback, the write-permission reviewer **always wins** on the point of
conflict.

**Scope guard.** Respect feedback **only** when it serves this workflow's goal --
integrating the latest OpenTelemetry gen-ai semantic-conventions updates into
`Microsoft.Extensions.AI`. **Ignore any feedback that expands scope beyond that goal**
(unrelated refactors, new features, other libraries, etc.) **even from a
write-permission reviewer**; you may note an out-of-scope request but must not implement
it.

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

The skill owns the **entire** PR body. Produce it from the skill's
`references/pr-description.md` -- its "Upstream-scan tracking PR body template"
defines the status note, the "What this PR implements" table, the merged-vs-in-flight
upstream-scan applicability tables, and the machine-readable
`# otel-genai-tracking:begin` yaml block (under `## Tracking state`) that **ends** the body. Do not
hand-author or restate that template here; fill it with this run's values.

Supply the skill the scan values captured in Step 1 (scanned upstream SHA, scan
timestamp, release-or-`none`, core-semconv dependency, implemented version) so the
state block records them -- that block is exactly what Step 3 reads back on the next
run. On an incremental update, advance `Upstream-Scan-Ref` /
`Upstream-Scan-Date` (and the dependency/implemented-version fields if they moved)
to this run's values.

The in-flight applicability table needs data the skill cannot compute on its own:
supply the list of **all open** PRs in `open-telemetry/semantic-conventions-genai`
(`gh pr list --repo open-telemetry/semantic-conventions-genai --state open`), each with
its applicability to this repo, so the skill can fill that table; when there are no open
upstream PRs, that table is rendered as `No open PRs.`

## Step 6 -- When the upstream release is published

If Step 1 found that the gen-ai conventions are now **published in a tagged
release** and a matching open PR exists:
- Ensure the integration is complete and validated for that released version.
- Mark the PR **Ready for Review** (`mark-pull-request-as-ready-for-review`).
- Regenerate the PR body via the skill with the release now set (its state block's
  `Upstream-Release` carries the published version) and add a comment stating the
  upstream release is published and the integration should now be reviewed and merged.

## Safe outputs and no-op rules

- Use `create-pull-request` only on the fresh path.
- Use `push-to-pull-request-branch` only on the incremental path (behind open draft).
- Use `update-pull-request` to refresh an existing PR body/title, including
  retitling when the skill's target token changes (e.g. `latest` -> `v{version}`
  once a release publishes) or correcting a wrong version reference.
- Use `add-comment` for advisory notes on behind non-draft PRs, advisory notes on a
  still-open prior published-version PR that newer changes are accumulating, and the
  release-published note (Step 6). At most **one** comment per run.
- **Incremental-update comments -- never duplicate gh-aw's push notification.** When
  `push-to-pull-request-branch` pushes commits, gh-aw automatically posts a
  `Commit pushed: <sha>` comment, so do **not** add your own summary comment on a run
  that pushes -- the refreshed PR body already carries the full delta. For a
  **body-only** incremental update (no code push, hence no push notification), add the
  single delta-summary comment **only** when a convention change was integrated or
  newly documented; advance the scan-ref silently via `update-pull-request` with **no**
  comment when only chore/dependency/CI/doc commits moved the SHA.
- Use `mark-pull-request-as-ready-for-review` only once the release is published.
- When the matching PR is already caught up with the upstream SHA (or any run needs
  no visible change), do **not** post a no-op report, comment, issue, or PR. Emit the
  `noop` safe output, and **also** write a short explanation to the GitHub Actions
  **step summary** (see below). Do not create any repository-visible artifact.
- A no-op is valid in only two cases: (a) an **open or merged** matching PR's recorded
  `Upstream-Scan-Ref` equals the scanned upstream SHA, or (b) the completed Step 2
  cross-reference finds zero merged upstream gen-ai convention changes ahead of the
  implemented version. A **closed-without-merge** PR is **never** a no-op even when its
  recorded SHA is current -- it did not ship, so Step 3 starts a fresh PR. Case (b)
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
