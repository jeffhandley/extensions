---
name: update-otel-genai-conventions
description: >-
  Analyze OpenTelemetry semantic-conventions releases or PRs with gen-ai changes
  and produce compensating change plans for dotnet/extensions. Use when asked to
  "update OTel conventions", "check semantic-conventions release", "plan gen-ai
  convention changes", review gen-ai convention PRs, or when given a release
  version, URL, or PR number/URL from open-telemetry/semantic-conventions with
  area:gen-ai changes. Also use when asked to "update OpenTelemetry", "bump
  semconv version", or "what changed in semantic-conventions vX.Y".
agent: 'agent'
tools: ['github/*', 'sql']
---

# Update OTel Gen-AI Conventions

Analyze OpenTelemetry [semantic-conventions](https://github.com/open-telemetry/semantic-conventions) releases or PRs with `area:gen-ai` changes and produce compensating change plans for `dotnet/extensions`. This skill supports multiple modes of operation, from auditing and planning through implementation and review.

## Mode Detection

Determine the operating mode from the user's request:

| Signal | Mode |
|--------|------|
| User asks to "audit" current implementation or "check alignment" with conventions | **Mode 1: Audit** |
| User asks to "update for vX.Y" or "apply vX.Y changes" in autopilot / one-shot | **Mode 2: Autopilot** |
| User asks to "generate a prompt" or "delegate to Copilot" or "CCA prompt" | **Mode 3: CCA Prompt** |
| Running inside Copilot Coding Agent with a prompt referencing this skill | **Mode 4: CCA Implementation** |
| User is in `/plan` mode or asks to "plan" changes | **Mode 5: Local Plan** |
| User asks to "implement", "apply", or "make the changes" after a plan exists | **Mode 6: Local Implementation** |
| User asks to `/review` or "review" convention changes | **Mode 7: Review** |

If unclear, default to **Mode 5** (Local Plan) and offer Mode 3 as an alternative.

## Input Handling

The user provides one of:
- A **semantic-conventions release version** (e.g. `v1.40.0`) → fetch from `https://github.com/open-telemetry/semantic-conventions/releases/tag/{version}`
- A **release URL** → fetch the release notes directly
- One or more **PR references** from `open-telemetry/semantic-conventions` with `area:gen-ai` changes — as URLs, PR numbers (e.g. `#3598`), or `open-telemetry/semantic-conventions#3598` format

When PR numbers are given without a full URL, resolve them against the `open-telemetry/semantic-conventions` repository.

### Existing dotnet/extensions PR Preflight

For **Mode 1: Audit** and **Mode 5: Local Plan**, after resolving the requested release or upstream PR identifiers but before doing deeper release analysis or creating a plan, search open pull requests in `dotnet/extensions` to determine whether another PR already appears to cover the requested GenAI/OpenTelemetry semantic-conventions update.

Search using the requested release version, release URL, or upstream semantic-conventions PR numbers, plus relevant terms such as `gen-ai`, `GenAI`, `semantic conventions`, `OpenTelemetry`, and `OTel`. If one or more likely matching PRs are open, report the PR number, title, author, URL, and the signal that matched. Then stop and state that the audit or plan is not proceeding because an open PR already appears to cover the update.

Do not silently ignore search failures. If GitHub search/listing is unavailable, report the problem and ask the user whether to proceed without the preflight.

### Analyzing the Release / PRs

1. **Fetch the release notes** or PR descriptions and identify all gen-ai changes
2. **Read** [references/file-inventory.md](references/file-inventory.md) to understand which files in this repo are affected
3. **Classify each change** using [references/change-classification.md](references/change-classification.md)
4. **Check current state** — read the current source files to determine what is already implemented vs. what needs new work
5. **Build a changes audit table** showing each semantic convention change, its classification, and required action

For Step 4, read these files to understand current state:
- `src/Libraries/Microsoft.Extensions.AI/OpenTelemetryConsts.cs` — all attribute/metric constants
- `src/Libraries/Microsoft.Extensions.AI/ChatCompletion/OpenTelemetryChatClient.cs` — the version reference in the doc comment and all attribute emission
- `src/Libraries/Microsoft.Extensions.AI/Embeddings/OpenTelemetryEmbeddingGenerator.cs` — embedding telemetry
- `src/Libraries/Microsoft.Extensions.AI/Common/FunctionInvocationProcessor.cs` — shared function invocation logic (execute_tool spans)
- Any other OpenTelemetry* files listed in the file inventory

---

## Mode 1: Audit

Audit the current gen-ai semantic conventions implementation against the latest published conventions to identify gaps, inconsistencies, or missed updates. Produces a plan that can be implemented locally (Mode 6) or delegated to CCA (Mode 3).

1. Complete the **Existing dotnet/extensions PR Preflight** above. If a matching open PR exists, report it and stop.
2. **Determine the current implemented version**: Read the version reference from `OpenTelemetryChatClient.cs` doc comment to identify which convention version the codebase claims to implement
3. **Check for version drift**: Verify every file with a gen-ai semantic conventions version reference uses the same version. Use the search command from [references/file-inventory.md](references/file-inventory.md#version-references). If files reference different versions, flag that as a critical gap requiring investigation.
4. **Fetch the latest convention spec**: Read the current gen-ai semantic conventions from the [published spec](https://opentelemetry.io/docs/specs/semconv/gen-ai/) and the latest release notes
5. **Read all current source files** listed in [references/file-inventory.md](references/file-inventory.md) to understand what is actually implemented
6. **Cross-reference**: For each attribute, metric, event, and operation name defined in the conventions:
   - Is the constant defined in `OpenTelemetryConsts.cs`?
   - Is it emitted in the relevant OpenTelemetry* client(s)?
   - Are version references consistent across all files?
   - Are tests covering the attribute/metric?
7. **Build an audit report** as a table:

   | Convention Item | Expected | Implemented | Gap |
   |----------------|----------|-------------|-----|
   | `gen_ai.request.attribute` | v1.XX | ✅ Yes / ❌ No / ⚠️ Partial | Description of gap |

8. **Produce a remediation plan** covering all identified gaps — formatted as either:
   - A **local plan** (Mode 5 format) with SQL-tracked todos, or
   - A **CCA prompt** (Mode 3 format) suitable for delegation
   
   Ask the user which format they prefer, or produce both if requested.
9. **Verify this skill is still accurate** (same as Mode 7, step 6): compare skill content against the current codebase and call out any discrepancies

---

## Mode 2: Autopilot

One-shot mode that analyzes the release, builds an internal plan, and implements all changes in a single pass. Best suited for autopilot usage or when the user wants end-to-end execution without intermediate review.

1. Complete the **Input Handling** analysis above
2. Build an internal work plan (do not write plan.md — keep it in working memory):
   - Changes audit table with classification for each gen-ai change
   - Ordered list of implementation steps
3. Read [references/implementation-patterns.md](references/implementation-patterns.md) and [references/testing-guide.md](references/testing-guide.md)
4. Implement all changes in order:
   - Version reference updates across all matched files
   - New constants in `OpenTelemetryConsts.cs`
   - Attribute/metric emission in relevant OpenTelemetry* clients
   - Test updates — augment existing tests, add new assertions
5. Self-review against [references/review-checklist.md](references/review-checklist.md)
6. Validate per the **Validation** section below
7. Present a summary of all changes made with the audit table showing what was implemented

---

## Mode 3: Generate CCA Prompt

Generate a structured prompt suitable for delegating to Copilot Coding Agent on github.com.

1. Complete the **Input Handling** analysis above
2. Read [references/prompt-template.md](references/prompt-template.md) for the template structure
3. Generate the prompt following the template, filling in:
   - Background with links to the upstream release/PRs
   - Changes audit table
   - Required changes with exact file paths and code context from the current source
   - Test expectations referencing [references/testing-guide.md](references/testing-guide.md)
   - Validation steps
4. Present the prompt to the user for review

The generated prompt should reference this skill:
> Reference the `update-otel-genai-conventions` skill in `.github/skills/` for implementation patterns and testing guidance.

---

## Mode 4: CCA Implementation

When running inside Copilot Coding Agent (github.com) with a prompt that references this skill.

1. Parse the prompt to identify the required changes
2. Read [references/implementation-patterns.md](references/implementation-patterns.md) for code patterns
3. Read [references/testing-guide.md](references/testing-guide.md) for test patterns
4. Read [references/review-checklist.md](references/review-checklist.md) to anticipate review feedback
5. Implement each change following the patterns:
   - Add constants to `OpenTelemetryConsts.cs`
   - Add attribute emission to the relevant OpenTelemetry* client classes
   - Update version references in doc comments across all OpenTelemetry* classes
   - Update or augment tests
6. Validate per the **Validation** section below

---

## Mode 5: Generate Local Plan

Generate a plan.md with SQL-tracked todos for local implementation.

1. Resolve the user's input to a semantic-conventions release or upstream PR identifiers
2. Complete the **Existing dotnet/extensions PR Preflight** above. If a matching open PR exists, report it and stop without creating `plan.md` or SQL todos.
3. Complete the **Analyzing the Release / PRs** analysis above
4. Create `plan.md` with:
   - Problem statement linking to the upstream release
   - Changes audit table (from analysis)
   - Numbered todos for each required change
5. Insert todos into the SQL `todos` table with descriptive IDs and detailed descriptions
6. For each todo, include:
   - Which file(s) to modify
   - What constants/attributes/code to add
   - Which tests to update
   - Reference to the relevant implementation pattern from [references/implementation-patterns.md](references/implementation-patterns.md)

---

## Mode 6: Local Implementation

After a plan has been generated (Mode 5), implement the changes locally.

1. Read the existing plan from `plan.md`
2. Query `SELECT * FROM todos WHERE status = 'pending' ORDER BY id` to find work items
3. For each todo:
   - Update status to `in_progress`
   - Read [references/implementation-patterns.md](references/implementation-patterns.md) for the relevant pattern
   - Implement the change
   - Update status to `done`
4. After all todos are complete:
   - Read [references/review-checklist.md](references/review-checklist.md) and self-review
   - Validate per the **Validation** section below

---

## Mode 7: Review

Review changes to gen-ai conventions against past patterns and known gotchas.

1. Identify the changes to review (local diff or PR diff)
2. Read [references/review-checklist.md](references/review-checklist.md) for the full checklist
3. Read [references/historical-releases.md](references/historical-releases.md) for past PR patterns. This file is point-in-time reference data from skill creation and may not include recent releases.
4. Check each item against the checklist:
   - Sensitive data gating (`EnableSensitiveData`)
   - Fluent Activity API chain style
   - Code deduplication (shared `Common/` classes)
   - Test augmentation vs. new tests
   - Version reference completeness
   - Exception recording approach (ILogger vs Activity.AddEvent)
5. Report findings with references to past PRs where similar feedback was given
6. **Verify this skill is still accurate without polluting it with release-specific details**: Read through this SKILL.md and all reference files, comparing against the current codebase. The codebase may have evolved since this skill was last updated — new features integrated, files moved, patterns changed. If any skill content has become inaccurate (e.g. file paths, code patterns, constant naming conventions, test infrastructure), call out each discrepancy and recommend specific updates to the skill files so the author can update them alongside the convention changes.
   - Recommend skill updates only for durable, cross-release guidance: reusable workflow steps, validation commands, repository conventions, stable implementation patterns, recurring review gotchas, or changed file paths/test infrastructure.
   - Do **not** add semantic-conventions release notes, per-version audit findings, one-off attribute mappings, or implementation details that apply only to the current release into the skill or reference files.
   - Capture version-specific findings in the review report, PR description, or implementation summary instead. Update `historical-releases.md` only when explicitly asked to curate long-lived historical reference data.

---

## Gotchas

Critical knowledge from past PR reviews that should inform all modes:

- **Exception recording**: Use `ILogger` with `[LoggerMessage]`, NOT `Activity.AddEvent`. The OTel SDK handles `Exception` passed to `ILogger`. See `OpenTelemetryLog.cs` in `Common/`.
- **Sensitive data**: Attributes that could contain user data (e.g. `exception.message`, message content) must be gated behind `EnableSensitiveData`. When in doubt, gate it.
- **Fluent chains**: Use fluent Activity API chains (`.SetStatus(...).SetTag(...)`) rather than separate statements.
- **Shared code**: Cross-cutting concerns (like exception logging) shared across multiple OpenTelemetry* clients belong in `src/Libraries/Microsoft.Extensions.AI/Common/`.
- **Test augmentation**: Prefer augmenting existing test assertions over creating new test methods. Check for existing tests that validate the same scenario.
- **Version references**: When bumping the convention version, update all files that match `grep -rn "Semantic Conventions for Generative AI systems v" src/Libraries/Microsoft.Extensions.AI/`. Not all OpenTelemetry* files contain this reference — only update the ones that do.
- **No CHANGELOGs**: This repository no longer maintains per-library CHANGELOG.md files. Do NOT create or update any CHANGELOG files.
- **Source-generated JSON**: Adding new OTel part types requires: (1) new inner class, (2) `[JsonSerializable]` registration on `OtelContext`, (3) switch case in `SerializeChatMessages()`.
- **LoggerMessage text**: When using `[LoggerMessage]`, the message text should match the OTel event name for console logger readability.

## Validation

After implementing changes (Modes 2, 4, and 6):

1. **Remove any existing `SDK.sln*` files** from the repo root — stale solution files cause build errors
2. **Baseline restore and build**: Run `.\build.cmd` from the repo root to restore dependencies and confirm a clean baseline build
3. **Generate filtered AI solution**: `.\build.cmd -vs AI -nolaunch` (the `-nolaunch` flag prevents Visual Studio from opening)
4. **Build and test**: `.\build.cmd -build -test`
5. Verify no new build warnings in `artifacts/log/Build.binlog`
6. If public API surface changed, run `./scripts/MakeApiBaselines.ps1` — then **discard API baseline updates for unrelated libraries** (only keep baselines for libraries changed as part of the convention update)
