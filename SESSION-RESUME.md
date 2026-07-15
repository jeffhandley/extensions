# Session Resume -- Release-Process Skills Revamp

> Handoff doc for resuming the release-process skill work on another machine/session.
> **Last updated:** 2026-07-15 (after the 10.8 release wrapped and the skill branch was rebuilt clean on `main`).
> This doc lives on the **`session/release-process-skills`** branch of `jeffhandley/extensions` so it does not pollute the skill PR branch.

## TL;DR -- how to resume

1. Clone/fetch your fork and add the upstreams:
   ```
   git remote add jeffhandley https://jeffhandley@github.com/jeffhandley/extensions.git   # note the username in the URL (GCM account disambiguation)
   git remote add dotnet      https://github.com/dotnet/extensions.git
   git remote add microsoft   https://dnceng@dev.azure.com/dnceng/internal/_git/dotnet-extensions
   git fetch jeffhandley dotnet
   ```
2. Check out the skill work: `git checkout -b jeffhandley/release-process jeffhandley/jeffhandley/release-process`
3. Read this doc for context, then continue with **Next steps** below.

## The two branches on the fork (`jeffhandley/extensions`)

| Branch | Purpose | Tip |
|---|---|---|
| `jeffhandley/release-process` | **The deliverable.** A pristine 10-commit, skills-only chain on top of fresh `dotnet/main`. Ready to open as a PR revamping the release-process skills. | `02ed6bfaac` |
| `session/release-process-skills` | This resume doc only (off `dotnet/main`). | -- |

`jeffhandley/release-process` differs from `dotnet/main` in **only** `.github/skills/` (verified: zero release-prep leakage). The 10 commits, newest first:

```
02ed6bfaac  write-release-notes: dedup backported PRs by harvesting referenced main-PR numbers
64d5fc5cad  Fix Test-SourceLink.ps1 to query the Microsoft symbol server (msdl)
4abc178429  Make internal-landing Option 2 non-squash (rebase and fast-forward) to match Option 1
a1da4b84b5  Note that prepare-release commits omit the Copilot-Session trailer
dc78e4b252  Teach Stage 5 to compare the internal-to-public merge against the last 5 releases
86d748b018  Add Stage 5 - Reconcile Branches to prepare-release skill
ca9e4d0806  Add Stage 4 - Publish and Verify to prepare-release skill
28ac1a3f18  Add Stage 3 - Validate and Land to prepare-release skill
39921ead57  Add Stage 2 - Update Dependencies to prepare-release skill
5c34233c24  Refactor prepare-release skill for progressive disclosure
```

### Skill delta vs `dotnet/main` (8 files)
- `.github/skills/prepare-release/SKILL.md` -- refactored for progressive disclosure
- `.github/skills/prepare-release/references/stage-1-prepare-internal-branch.md` (new)
- `.github/skills/prepare-release/references/stage-2-update-dependencies.md` (new)
- `.github/skills/prepare-release/references/stage-3-validate-and-land.md` (new)
- `.github/skills/prepare-release/references/stage-4-publish-and-verify.md` (new)
- `.github/skills/prepare-release/references/stage-5-reconcile-branches.md` (new)
- `.github/skills/prepare-release/scripts/Test-SourceLink.ps1` (new)
- `.github/skills/write-release-notes/references/collect-prs.md` -- robust backport dedup

> **Why the branch was rebuilt:** the early skill commits (`Refactor`, `Add Stage 2`) were authored below the 10.8 release commit `8f88b00840`. During the public merges their file effects were reverted out of `main`'s tree even though the commits stayed in `main`'s ancestry. A plain rebase would silently skip them, so the chain was reconstructed by **cherry-picking** the 10 skill commits onto fresh `dotnet/main` (dropping the 4 release-prep commits). Verified: skill content byte-identical to the pre-rebuild tip.

## Next steps

**Open the skill PR (primary remaining work):**
- The branch is already clean off `main`, so the PR can be opened **directly from `jeffhandley/release-process`** into `dotnet/extensions:main`. No further branch surgery needed.
- PR should describe the *current state* of the revamped skills (not a changelog of how we got here).
- Consider whether `prepare-release` should stay a skill vs. become an agent -- decision so far: keep it a **skill** (human-gated at each stage), delegating autonomous sub-tasks (release-notes collection/comparison) to subagents.

**10.8 release tail (mostly done):**
- [x] v10.8.0 published (51 pkgs), mirrored, tagged, release notes published.
- [x] PR #7631 (internal->public) merged.
- [x] Published v10.8.0 notes: erroneous #7546 bullet **removed by a team member** (verified 2026-07-15); notes now correct.
- [ ] PR #7632 (release->main merge commit) -- confirm it merged.
- [ ] .NET support page update (runbook item 13).
- [ ] Source Link: 10.8 symbols still **pending on msdl** -- re-check in a day (see below).

## Backport dedup fix (the RCA that shaped `collect-prs.md`)

**Problem:** #7546 (a `main` PR) was wrongly included in the 10.8 notes -- it had already shipped in 10.7 via its backport #7547. Commit-ancestry dedup misses the `main` half of a backport.

**Root learning (from studying 15 months of `base:release/*` PRs):** backport PRs name the `main` PR number(s) they carry, in the title (`(#NNNN)`) and/or body. Two shapes:
- **Single-PR backport:** `[release/10.7] Fix ToolJson...` (#7547) -> body references #7546.
- **Aggregate backport:** `Stage an MEAI 10.4.1 release` (#7402) -> body lists **13** `main` PRs, **no title twin**. Title-matching would miss all of them.

**Fix (in `collect-prs.md`):** primary dedup = harvest every `#NNNN` from the title+body of every PR merged into the prior release branch; exclude candidates in that set. Title-equivalence kept only as a fallback. Verified against `release/10.7`: harvested set `{7544, 7546, 7554}` -- correctly excludes #7546 and #7544.

## Source Link / symbols status

- dotnet/extensions publishes symbols to **msdl.microsoft.com**, NOT symbols.nuget.org. Verify with `Test-SourceLink.ps1` (uses `dotnet-symbol --microsoft-symbol-server` + `sourcelink test`).
- These packages ship **no `.snupkg`** (0 of 51); symbols reach msdl only via the internal Microsoft symbol-publishing pipeline, which rides on the build being "Released".
- As of 2026-07-15, 10.8.0 symbols are **not yet indexed** on msdl (control test: v10.7.0 = `valid`, so the tooling works). No released BAR build exists for the release commit yet. Re-check with:
  ```
  ./.github/skills/prepare-release/scripts/Test-SourceLink.ps1 -PackageDir C:\Users\jeffhand\Downloads\ToPublish  # staged 51 pkgs
  ```

## Environment & conventions

- **Remotes:** `dotnet` = github.com/dotnet/extensions (public); `microsoft` = dev.azure.com/dnceng/internal (internal AzDO); `jeffhandley` = the fork. Resolve the internal remote by **URL**, not name (the name varies by machine).
- **Two gh accounts on this box:** `jeffhand_microsoft` (usually active, but **cannot write** the personal fork) and `jeffhandley` (owns the fork). The `jeffhandley` remote URL is qualified with `jeffhandley@` so Git Credential Manager picks the right account regardless of the active gh account. If a fork push 403s, `gh auth switch --user jeffhandley`, push, then switch back.
- **Runbook** (not in git): `CoreFx - Documents/Infrastructure/dotnet-extensions-release-management.md` on SharePoint/OneDrive -- synced across machines. Already updated to match these skills (msdl, Option 2 non-squash, Copilot-Session omission, backport review). Release-notes generation delegates to the `/write-release-notes` skill.
- **Commit conventions:** public dotnet/extensions commits keep `Co-authored-by: Copilot` but **omit** the `Copilot-Session` trailer. No em-dashes (use `--`). Weave user-directed fixes into their originating commit; use fresh add-on commits only during automated test/fix loops. Commit subjects describe *what* changed, not why-over-alternatives; don't name specific reviewers.
- **Tools installed:** `darc`, `sourcelink`, `dotnet-symbol` (global), `gh`, `az`.
- **gh gotchas:** `gh pr view --json` has no `authorAssociation`; `isLatest` invalid on release view. `az pipelines runs list` uses `--pipeline-ids`.
