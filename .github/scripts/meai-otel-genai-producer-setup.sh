#!/usr/bin/env bash
# Host-side setup for the "MEAI: Otel GenAI Producer" agentic workflow.
#
# Deterministically prepares everything the agent needs before it runs so the agent
# never has to discover, filter, or de-duplicate state itself:
#   1. Resolves the upstream scan target (from the manager's `target` input, or from a
#      standalone dispatch's upstream_ref / default HEAD).
#   2. Discovers the maintained draft PR and classifies it: OURS (carries our tracking
#      marker), BLOCKED (a non-automation PR occupying our branch -- a human owns it),
#      or NONE.
#   3. Reads the PR's recorded Upstream-Scan-Ref and Upstream-Release, and computes a
#      recommended lifecycle action.
#
# Writes (under $AGENT_DIR, uploaded in the agent artifact so downstream jobs read them):
#   target.json         resolved scan target + PR discovery + recommended action
#
# The recommended action lets a caught-up run early-noop before the expensive build. The
# agent's Step 3 remains authoritative and refines edge cases (e.g. release-gate mark-ready).
#
# A transient API blip must never masquerade as "no PR / no work"; discovery is retried
# and, when the maintained PR cannot be established with confidence, the action defaults
# to "produce" (never a silent skip).
#
# Environment:
#   GITHUB_REPOSITORY  owner/repo (set by Actions)
#   GH_TOKEN           token with pull-requests:read (the agent job's github.token)
#   TARGET_JSON        target object from the manager (workflow_call); may be empty
#   UPSTREAM_REF       standalone-dispatch scan ref override (used only when TARGET_JSON empty)
#   AGENT_DIR          output dir (default /tmp/gh-aw/agent)
set -euo pipefail

AGENT_DIR="${AGENT_DIR:-/tmp/gh-aw/agent}"
REPO="${GITHUB_REPOSITORY:-}"
TARGET_JSON="${TARGET_JSON:-}"
UPSTREAM_REF="${UPSTREAM_REF:-}"
mkdir -p "$AGENT_DIR"
target_file="$AGENT_DIR/target.json"

run_started_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

# ---- 1. Resolve the upstream scan target -------------------------------------------
UPSTREAM_REPO="open-telemetry/semantic-conventions-genai"
DESIRED_BRANCH="update-otel-genai-to-latest"
MARKER_ID="otel-genai"
upstream_sha=""
if [ -n "$TARGET_JSON" ]; then
	UPSTREAM_REPO="$(jq -r '.upstream_repo // "open-telemetry/semantic-conventions-genai"' <<<"$TARGET_JSON")"
	DESIRED_BRANCH="$(jq -r '.desired_branch // "update-otel-genai-to-latest"' <<<"$TARGET_JSON")"
	MARKER_ID="$(jq -r '.marker_id // "otel-genai"' <<<"$TARGET_JSON")"
	UPSTREAM_REF="$(jq -r '.upstream_ref // ""' <<<"$TARGET_JSON")"
	upstream_sha="$(jq -r '.upstream_sha // ""' <<<"$TARGET_JSON")"
fi

resolve_sha() {
	local ref="$1" sha="" attempt db
	for attempt in 1 2 3; do
		if [ -z "$ref" ]; then
			db="$(gh api "repos/${UPSTREAM_REPO}" -q '.default_branch' 2>/dev/null || true)"
			[ -n "$db" ] && sha="$(gh api "repos/${UPSTREAM_REPO}/commits/${db}" -q '.sha' 2>/dev/null || true)"
		else
			sha="$(gh api "repos/${UPSTREAM_REPO}/commits/${ref}" -q '.sha' 2>/dev/null || true)"
		fi
		[ -n "$sha" ] && { printf '%s' "$sha"; return 0; }
		[ "$attempt" -lt 3 ] && sleep 2
	done
	return 0
}
if [ -z "$upstream_sha" ] && [ -n "${GH_TOKEN:-}" ]; then
	upstream_sha="$(resolve_sha "$UPSTREAM_REF")"
fi

# Best-effort latest published gen-ai release tag (normally none -- Development stability).
upstream_release="none"
if [ -n "${GH_TOKEN:-}" ]; then
	rel="$(gh api "repos/${UPSTREAM_REPO}/releases/latest" -q '.tag_name' 2>/dev/null || true)"
	[ -n "$rel" ] && upstream_release="$rel"
fi

write_target() {
	# $1=pr $2=pr_state $3=pr_is_draft $4=pr_recorded_sha $5=pr_recorded_release
	# $6=classification(ours|blocked|none) $7=action
	jq -cn \
		--arg upstream_repo "$UPSTREAM_REPO" --arg upstream_ref "$UPSTREAM_REF" \
		--arg upstream_sha "$upstream_sha" --arg upstream_release "$upstream_release" \
		--arg desired_branch "$DESIRED_BRANCH" --arg marker_id "$MARKER_ID" \
		--arg pr "$1" --arg pr_state "$2" --argjson pr_is_draft "${3:-false}" \
		--arg pr_recorded_sha "$4" --arg pr_recorded_release "$5" \
		--arg classification "$6" --arg action "$7" \
		--arg run_started_at "$run_started_at" \
		'{upstream_repo:$upstream_repo, upstream_ref:$upstream_ref, upstream_sha:$upstream_sha,
		  upstream_release:$upstream_release, desired_branch:$desired_branch, marker_id:$marker_id,
		  pr:$pr, pr_state:$pr_state, pr_is_draft:$pr_is_draft, pr_recorded_sha:$pr_recorded_sha,
		  pr_recorded_release:$pr_recorded_release, classification:$classification, action:$action,
		  run_started_at:$run_started_at}' >"$target_file"
}

step_summary() {
	# $1=classification $2=action $3=pr $4=pr_recorded_sha
	{
		echo "## Otel GenAI producer -- setup decision"
		echo ""
		echo "| field | value |"
		echo "|---|---|"
		echo "| upstream_repo | \`${UPSTREAM_REPO}\` |"
		echo "| scan ref | \`${UPSTREAM_REF:-<default HEAD>}\` |"
		echo "| upstream SHA | \`${upstream_sha:-<unresolved>}\` |"
		echo "| upstream release | \`${upstream_release}\` |"
		echo "| maintained PR | ${3:-<none>} |"
		echo "| PR recorded SHA | \`${4:-<none>}\` |"
		echo "| classification | **${1}** |"
		echo "| recommended action | **${2}** |"
	} >>"${GITHUB_STEP_SUMMARY:-/dev/null}" 2>/dev/null || true
}

if [ -z "$REPO" ] || [ -z "${GH_TOKEN:-}" ]; then
	echo "GITHUB_REPOSITORY or GH_TOKEN unset; cannot discover PR -- defaulting to produce (fresh)"
	write_target "" "" false "" "" "none" "produce"
	step_summary "none" "produce" "" ""
	exit 0
fi

# ---- 2. Discover the maintained PR and classify it ---------------------------------
# List open PRs on our evergreen branch (lightweight fields only -- never bulk-fetch
# bodies). Consider our automation+area-ai PR and any other PR
# occupying the branch (human-owned -> blocked). Retry so a blip never drops the PR.
pr="" pr_labels="" pr_is_draft="false" classification="none"
for attempt in 1 2 3; do
	rows="$(gh pr list --repo "$REPO" --state open \
		--json number,headRefName,isDraft,labels,updatedAt \
		--jq "map(select(.headRefName==\"${DESIRED_BRANCH}\")) | sort_by(.updatedAt) | reverse" \
		2>/dev/null || true)"
	[ -n "$rows" ] && break
	[ "$attempt" -lt 3 ] && sleep 2
done
rows="${rows:-[]}"

pr="$(jq -r '(.[0].number) // empty' <<<"$rows" 2>/dev/null || true)"
if [ -n "$pr" ]; then
	pr_is_draft="$(jq -r '.[0].isDraft // false' <<<"$rows")"
	has_automation="$(jq -r '[.[0].labels[].name] | (index("automation") != null) and (index("area-ai") != null)' <<<"$rows")"
	body="$(gh pr view "$pr" --repo "$REPO" --json body -q '.body' 2>/dev/null || true)"
	has_marker="false"
	printf '%s' "$body" | grep -q "${MARKER_ID}-tracking:begin" && has_marker="true"
	if [ "$has_automation" = "true" ] && [ "$has_marker" = "true" ]; then
		classification="ours"
	else
		# A PR occupies our branch that we can't confirm is ours (no tracking marker):
		# treat it as human-owned and stand down.
		classification="blocked"
	fi
fi

# ---- 3. Read recorded state + compute the recommended action -----------------------
pr_recorded_sha="" pr_recorded_release=""
if [ -n "$pr" ] && [ "$classification" != "blocked" ]; then
	pr_recorded_sha="$(printf '%s\n' "$body" \
		| sed -n 's/^[[:space:]]*Upstream-Scan-Ref:[[:space:]]*//p' \
		| head -1 | tr -d '"'\''\r' | sed 's/[[:space:]]*$//')"
	pr_recorded_release="$(printf '%s\n' "$body" \
		| sed -n 's/^[[:space:]]*Upstream-Release:[[:space:]]*//p' \
		| head -1 | tr -d '"'\''\r' | sed 's/[[:space:]]*$//')"
fi

# ---- 4. Recommended lifecycle action ------------------------------------------------
# Only a high-confidence caught-up state early-noops; every uncertain case produces so
# the agent can make the real zero-delta / release-gate decision in Steps 2-3.
action="produce"
case "$classification" in
	blocked)
		action="noop" ;;
	none)
		action="produce" ;; # fresh
	ours)
		release_changed="false"
		[ "$upstream_release" != "none" ] && [ "$upstream_release" != "$pr_recorded_release" ] && release_changed="true"
		if [ -n "$upstream_sha" ] && [ -n "$pr_recorded_sha" ] \
			&& [ "$upstream_sha" = "$pr_recorded_sha" ] \
			&& [ "$release_changed" != "true" ]; then
			action="noop"
		else
			action="produce"
		fi ;;
esac

write_target "$pr" "open" "$pr_is_draft" "$pr_recorded_sha" "$pr_recorded_release" "$classification" "$action"
step_summary "$classification" "$action" "${pr:-}" "$pr_recorded_sha"

echo "Setup: classification=${classification} action=${action} pr=${pr:-<none>} recorded_sha=${pr_recorded_sha:-<none>} upstream_sha=${upstream_sha:-<none>}"
echo "-- target.json --"; jq '.' "$target_file"
