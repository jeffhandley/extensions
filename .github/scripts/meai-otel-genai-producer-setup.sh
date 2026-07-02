#!/usr/bin/env bash
# Host-side setup for the "MEAI: Otel GenAI Producer" agentic workflow.
#
# Deterministically prepares everything the agent needs before it runs so the agent
# never has to discover, filter, or de-duplicate state itself:
#   1. Resolves the upstream scan target (from the manager's `target` input, or from a
#      standalone dispatch's upstream_ref / default HEAD).
#   2. Discovers the maintained draft PR and classifies it: OURS (carries our tracking
#      marker), ADOPT (a human-bootstrapped automation+area-ai PR on our branch with no
#      marker yet), BLOCKED (a non-automation PR occupying our branch -- a human owns it),
#      or NONE.
#   3. Reads the PR's recorded Upstream-Scan-Ref, Upstream-Release, and
#      Feedback-Processed-Through watermark, and computes a recommended lifecycle action.
#   4. Builds the reviewer-feedback batch (submitted review summaries + inline
#      review-comment threads created after the watermark, from write-access authors).
#
# Writes (under $AGENT_DIR, uploaded in the agent artifact so downstream jobs read them):
#   target.json         resolved scan target + PR discovery + recommended action
#   feedback.json       JSON array of the reviewer-feedback batch ([] when empty)
#   feedback-meta.json  {"pr":..,"watermark":..,"run_started_at":..,"pending":..}
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
feedback_file="$AGENT_DIR/feedback.json"
meta_file="$AGENT_DIR/feedback-meta.json"
echo '[]' >"$feedback_file"

run_started_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

write_meta() {
	# $1=pr $2=watermark $3=pending
	jq -cn --arg pr "$1" --arg wm "$2" --arg rs "$run_started_at" --argjson pending "$3" \
		'{pr:$pr, watermark:$wm, run_started_at:$rs, pending:$pending}' >"$meta_file"
}

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
	# $6=classification(ours|adopt|blocked|none) $7=action $8=pending
	jq -cn \
		--arg upstream_repo "$UPSTREAM_REPO" --arg upstream_ref "$UPSTREAM_REF" \
		--arg upstream_sha "$upstream_sha" --arg upstream_release "$upstream_release" \
		--arg desired_branch "$DESIRED_BRANCH" --arg marker_id "$MARKER_ID" \
		--arg pr "$1" --arg pr_state "$2" --argjson pr_is_draft "${3:-false}" \
		--arg pr_recorded_sha "$4" --arg pr_recorded_release "$5" \
		--arg classification "$6" --arg action "$7" \
		--arg watermark "${watermark:-}" --arg run_started_at "$run_started_at" \
		--argjson pending "$8" \
		'{upstream_repo:$upstream_repo, upstream_ref:$upstream_ref, upstream_sha:$upstream_sha,
		  upstream_release:$upstream_release, desired_branch:$desired_branch, marker_id:$marker_id,
		  pr:$pr, pr_state:$pr_state, pr_is_draft:$pr_is_draft, pr_recorded_sha:$pr_recorded_sha,
		  pr_recorded_release:$pr_recorded_release, classification:$classification, action:$action,
		  watermark:$watermark, run_started_at:$run_started_at, pending:$pending}' >"$target_file"
}

step_summary() {
	# $1=classification $2=action $3=pr $4=pr_recorded_sha $5=pending
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
		echo "| pending feedback | ${5} |"
	} >>"${GITHUB_STEP_SUMMARY:-/dev/null}" 2>/dev/null || true
}

watermark=""

if [ -z "$REPO" ] || [ -z "${GH_TOKEN:-}" ]; then
	echo "GITHUB_REPOSITORY or GH_TOKEN unset; cannot discover PR -- defaulting to produce (fresh)"
	write_meta "" "" 0
	write_target "" "" false "" "" "none" "produce" 0
	step_summary "none" "produce" "" "" 0
	exit 0
fi

# ---- 2. Discover the maintained PR and classify it ---------------------------------
# List open PRs on our evergreen branch (lightweight fields only -- never bulk-fetch
# bodies). Consider BOTH automation+area-ai PRs (ours or bootstrapped) and any other PR
# occupying the branch (human-owned -> blocked). Retry so a blip never drops the PR.
pr="" pr_labels="" pr_is_draft="false" classification="none" body_ok="false"
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
	# Fetch the PR body with retries. A transient blip must not drop our tracking
	# marker -- that would misclassify our own PR as a fresh adopt and replay feedback
	# off a phantom watermark; body_ok tells a real empty body from a failed fetch.
	body="" body_ok="false"
	for battempt in 1 2 3; do
		if body="$(gh pr view "$pr" --repo "$REPO" --json body -q '.body' 2>/dev/null)"; then
			body_ok="true"; break
		fi
		[ "$battempt" -lt 3 ] && sleep 2
	done
	has_marker="false"
	printf '%s' "$body" | grep -q "${MARKER_ID}-tracking:begin" && has_marker="true"
	if [ "$body_ok" != "true" ] && [ "$has_automation" = "true" ]; then
		# Body unreadable after retries but our automation labels are present: almost
		# certainly our own PR mid-blip. Treat as ours -- the agent re-reads the real
		# body and reconciles -- rather than re-adopting or replaying stale feedback.
		classification="ours"
	elif [ "$has_automation" = "true" ] && [ "$has_marker" = "true" ]; then
		classification="ours"
	elif [ "$has_automation" = "true" ] && [ "$has_marker" != "true" ]; then
		# Automation-labeled PR on our branch with no tracking marker yet: a human
		# bootstrapped it -- adopt it (write the marker on this run's update).
		classification="adopt"
	else
		# A PR occupies our branch but is not our automation PR: a human owns it. Stand down.
		classification="blocked"
	fi
fi

# ---- 3. Read recorded state + compute the recommended action -----------------------
pr_recorded_sha="" pr_recorded_release=""
if [ -n "$pr" ] && [ "$classification" != "blocked" ] && [ "$body_ok" = "true" ]; then
	watermark="$(printf '%s\n' "$body" \
		| sed -n 's/^[[:space:]]*Feedback-Processed-Through:[[:space:]]*//p' \
		| head -1 | tr -d '"'\''\r' | sed 's/[[:space:]]*$//')"
	pr_recorded_sha="$(printf '%s\n' "$body" \
		| sed -n 's/^[[:space:]]*Upstream-Scan-Ref:[[:space:]]*//p' \
		| head -1 | tr -d '"'\''\r' | sed 's/[[:space:]]*$//')"
	pr_recorded_release="$(printf '%s\n' "$body" \
		| sed -n 's/^[[:space:]]*Upstream-Release:[[:space:]]*//p' \
		| head -1 | tr -d '"'\''\r' | sed 's/[[:space:]]*$//')"
fi

# ---- 4. Build the reviewer-feedback batch (write-access review feedback since watermark)
pending=0
if [ -n "$pr" ] && [ "$classification" != "blocked" ] && [ "$body_ok" = "true" ]; then
	tmp="$(mktemp)"
	{
		gh api "repos/${REPO}/pulls/${pr}/comments" --paginate \
			-q '.[]|{id:.id,in_reply_to_id:.in_reply_to_id,created:.created_at,assoc:.author_association,type:.user.type,kind:"review_comment",author:.user.login,url:.html_url,path:.path,body:.body}' 2>/dev/null || true
		gh api "repos/${REPO}/pulls/${pr}/reviews" --paginate \
			-q '.[]|{id:.id,created:.submitted_at,assoc:.author_association,type:.user.type,kind:"review",author:.user.login,url:.html_url,state:.state,body:.body}' 2>/dev/null || true
	} >"$tmp"

	jq -s --arg since "$watermark" '
		def wa: (.assoc=="OWNER" or .assoc=="MEMBER" or .assoc=="COLLABORATOR");
		def human: (.type != "Bot");
		def hasbody: ((.body // "") != "");
		def isnew: ($since=="" or ((.created // "") > $since));
		[ .[]
			| select(.kind=="review")
			| select(human and wa and hasbody and isnew)
			| {kind, id, author, assoc, created, url, state:(.state // null), body, is_new:true, _sort:(.created // "")}
		]
		+
		( [ .[] | select(.kind=="review_comment") ]
			| group_by(.in_reply_to_id // .id)
			| map( select( any(.[]; human and wa and hasbody and isnew) )
						 | { kind:"review_thread",
								 path:(.[0].path // null),
								 comments:[ .[] | select(human and hasbody)
															| {id, author, assoc, created, url, body, is_new:isnew} ],
								 _sort:( [ .[].created ] | max ) } )
		)
		| sort_by(._sort) | map(del(._sort))' "$tmp" >"$feedback_file" 2>/dev/null || echo '[]' >"$feedback_file"
	rm -f "$tmp"
	pending="$(jq 'length' "$feedback_file" 2>/dev/null || echo 0)"
fi

# ---- 5. Recommended lifecycle action ------------------------------------------------
# Only a high-confidence caught-up state early-noops; every uncertain case produces so
# the agent can make the real zero-delta / release-gate decision in Steps 2-3.
action="produce"
case "$classification" in
	blocked)
		action="noop" ;;
	none)
		action="produce" ;; # fresh
	ours|adopt)
		release_changed="false"
		[ "$upstream_release" != "none" ] && [ "$upstream_release" != "$pr_recorded_release" ] && release_changed="true"
		if [ "$classification" = "ours" ] \
			&& [ -n "$upstream_sha" ] && [ -n "$pr_recorded_sha" ] \
			&& [ "$upstream_sha" = "$pr_recorded_sha" ] \
			&& [ "$pending" -eq 0 ] \
			&& [ "$release_changed" != "true" ]; then
			action="noop"
		else
			action="produce"
		fi ;;
esac

write_meta "$pr" "$watermark" "$pending"
write_target "$pr" "open" "$pr_is_draft" "$pr_recorded_sha" "$pr_recorded_release" "$classification" "$action" "$pending"
step_summary "$classification" "$action" "${pr:-}" "$pr_recorded_sha" "$pending"

echo "Setup: classification=${classification} action=${action} pr=${pr:-<none>} recorded_sha=${pr_recorded_sha:-<none>} upstream_sha=${upstream_sha:-<none>} pending=${pending}"
echo "-- target.json --"; jq '.' "$target_file"
echo "-- feedback.json --"; jq '.' "$feedback_file"
