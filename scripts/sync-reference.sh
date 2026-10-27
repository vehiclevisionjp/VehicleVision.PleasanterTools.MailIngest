#!/usr/bin/env bash
# AI の事実確認用ソースだけを取得する。ビルドや CI からは呼ばない。
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd -P)"
manifest="$repo_root/references.json"
root="$repo_root"
explicit_manifest=false
explicit_root=false
fail() {
    echo "Error: $*" >&2
    exit 1
}
while [[ $# -gt 0 ]]; do
    case "$1" in
        --manifest|--root)
            [[ $# -ge 2 && -n "$2" ]] || fail "$1 requires a path."
            if [[ "$1" == --manifest ]]; then
                manifest="$2"
                explicit_manifest=true
            else
                root="$2"
                explicit_root=true
            fi
            shift 2
            ;;
        *) fail "Usage: sync-reference.sh [--manifest <path>] [--root <dir>]" ;;
    esac
done
normalize_path() {
    local value="$1"
    if command -v cygpath > /dev/null 2>&1; then
        value="$(cygpath -u "$value")"
    fi
    if [[ "$value" != /* ]]; then
        value="$repo_root/$value"
    fi
    printf '%s\n' "$value"
}
manifest="$(normalize_path "$manifest")"
root="$(normalize_path "$root")"
default_reference=false
if [[ ! -e "$manifest" ]]; then
    [[ "$explicit_manifest" == false ]] || fail "Manifest does not exist."
    [[ "$explicit_root" == false ]] || fail "--root requires a manifest."
    default_reference=true
fi

# 各パーサーは型だけを検査し、共通の NUL 区切りレコードへ変換する。
read_manifest() {
    if command -v jq > /dev/null 2>&1; then
        jq -js '
            def text: if type == "string" then (test("[\\x00-\\x1f\\x7f]") | not) else false end;
            if length == 1 then .[0] else error("Expected one JSON document") end |
            if type != "array" or length == 0 then error("Expected a nonempty array") else
            to_entries[] |
            if (.value | if type == "object" then
                (.path | text) and (.url | text) and (.branch | text) and
                (if has("private") then (.private | type == "boolean") else true end) and
                (if has("depth") then (.depth | if type == "number" then . >= 1 and
                    . <= 2147483647 and floor == . else false end) else true end)
                else false end)
            then .value | ["valid", .path, .url, .branch,
                (if .private == true then "true" else "false" end), ((.depth // 1) | tostring)]
            else ["invalid", ("entry-" + ((.key + 1) | tostring)), "", "", "false", "1"]
            end | .[] | ., "\u0000"
            end' "$manifest"
    elif command -v node > /dev/null 2>&1; then
        node - "$manifest" <<'NODE'
const fs = require('node:fs');
const entries = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
if (!Array.isArray(entries) || entries.length === 0) {
    throw new Error('Expected a nonempty array');
}
for (const [index, item] of entries.entries()) {
    const text = value => typeof value === 'string' && !/[\x00-\x1f\x7f]/.test(value);
    const valid = item !== null && typeof item === 'object' && !Array.isArray(item) &&
        text(item.path) && text(item.url) && text(item.branch) &&
        (!Object.hasOwn(item, 'private') || typeof item.private === 'boolean') &&
        (!Object.hasOwn(item, 'depth') || (Number.isInteger(item.depth) &&
            item.depth >= 1 && item.depth <= 2147483647));
    const fields = valid
        ? ['valid', item.path, item.url, item.branch, String(item.private ?? false), String(item.depth ?? 1)]
        : ['invalid', `entry-${index + 1}`, '', '', 'false', '1'];
    process.stdout.write(fields.join('\0') + '\0');
}
NODE
    else
        local python=""
        if command -v python3 > /dev/null 2>&1; then
            python=python3
        elif command -v python > /dev/null 2>&1; then
            python=python
        else
            return 1
        fi
        "$python" - "$manifest" <<'PYTHON'
import json
import re
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    entries = json.load(source)
if not isinstance(entries, list) or not entries:
    raise ValueError("Expected a nonempty array")
for index, item in enumerate(entries):
    valid = (isinstance(item, dict) and
             all(isinstance(item.get(key), str) and not re.search(r"[\x00-\x1f\x7f]", item[key])
                 for key in ("path", "url", "branch")) and
             ("private" not in item or type(item["private"]) is bool) and
             ("depth" not in item or (type(item["depth"]) in (int, float) and
              1 <= item["depth"] <= 2147483647 and int(item["depth"]) == item["depth"])))
    fields = (["valid", item["path"], item["url"], item["branch"],
               str(item.get("private", False)).lower(), str(int(item.get("depth", 1)))]
              if valid else ["invalid", "entry-" + str(index + 1), "", "", "false", "1"])
    sys.stdout.buffer.write(("\0".join(fields) + "\0").encode("utf-8"))
PYTHON
    fi
}

mkdir -p "$repo_root/temp"
records="$(mktemp "$repo_root/temp/reference-records.XXXXXX")"
summary="$(mktemp "$repo_root/temp/reference-summary.XXXXXX")"
trap 'rm -f -- "$records" "$summary"' EXIT
if [[ "$default_reference" == true ]]; then
    printf '%s\0' valid _reference/Implem.Pleasanter \
        https://github.com/Implem/Implem.Pleasanter.git main false 1 > "$records"
else
    if ! command -v jq > /dev/null 2>&1 && ! command -v node > /dev/null 2>&1 &&
        ! command -v python3 > /dev/null 2>&1 && ! command -v python > /dev/null 2>&1; then
        fail "Reading a manifest requires jq, node or python."
    fi
    read_manifest > "$records" 2> /dev/null ||
        fail "Cannot parse manifest; expected a nonempty JSON array with jq, node or python available."
fi
mkdir -p "$root"
root="$(cd "$root" && pwd -P)"

sync_entry() (
    set -euo pipefail
    kind="$1" path="$2" url="$3" branch="$4" private="$5" depth="$6"
    reference_dir="" clone_dir="" lock_dir="" locked=false inspected=false
    state=failed sha=- commit_date=- reason="" version=unknown
    finish() {
        local result=$?
        trap - EXIT
        if [[ -n "$clone_dir" ]]; then
            if ! rm -rf -- "$clone_dir"; then
                reason="Error: Cannot remove temporary clone."
                state=failed
                result=1
            fi
        fi
        if [[ "$locked" == true ]]; then
            if ! rmdir "$lock_dir"; then
                reason="Error: Cannot remove reference lock."
                state=failed
                result=1
            fi
        fi
        if [[ "$inspected" == true ]]; then
            if ! sha="$(git -C "$reference_dir" rev-parse --short HEAD 2> /dev/null)" ||
                ! commit_date="$(git -C "$reference_dir" show -s --format=%cI HEAD 2> /dev/null)"; then
                sha=- commit_date=-
                reason="Error: Cannot read reference commit metadata."
                state=failed
                result=1
            fi
        fi
        if [[ -n "$reason" ]]; then
            echo "$reason Path=$path" >&2
        fi
        printf 'Reference: Path=%s SHA=%s Date=%s Status=%s' "$path" "$sha" "$commit_date" "$state"
        if [[ "$default_reference" == true && "$state" == updated ]]; then
            local project="$reference_dir/Implem.Pleasanter/Implem.Pleasanter.csproj"
            if [[ -f "$project" ]]; then
                version="$(sed -nE 's/.*<Version>([^<]+)<\/Version>.*/\1/p' "$project")"
                version="${version%%$'\n'*}"
            fi
            if [[ -z "$version" || "$version" == unknown ]]; then
                version=unknown
                echo "Warning: Version could not be read from Implem.Pleasanter.csproj." >&2
            fi
            printf ' FullSHA=%s Retrieved=%s Version=%s' \
                "$(git -C "$reference_dir" rev-parse HEAD)" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$version"
        fi
        printf '\n'
        exit "$result"
    }
    trap finish EXIT
    entry_fail() {
        reason="Error: $*"
        exit 1
    }
    unavailable() {
        if [[ "$private" == true ]]; then
            state=unavailable
            reason="Warning: Private reference could not be retrieved; check your GitHub user authentication."
            exit 0
        fi
        entry_fail "Reference retrieval failed; check connectivity, origin and branch."
    }
    [[ "$kind" != overlap ]] || entry_fail "Overlapping reference path."
    [[ "$kind" == valid ]] || entry_fail "Invalid manifest entry types."
    # Windows の区切り・ドライブ・末尾の別名表現も拒否し、Git 管理領域を保護する。
    if [[ -z "$path" || "$path" == /* || "$path" == *\\* || "$path" == *:* ||
        "$path" == *[[:cntrl:]]* ]]; then
        path="<invalid-path>"
        entry_fail "Invalid reference path."
    fi
    IFS='/' read -r -a components <<< "$path"
    for component in "${components[@]}"; do
        [[ -n "$component" && "$component" != . && "$component" != .. &&
            "${component,,}" != .git* && "$component" != *[.\ ] ]] ||
            entry_fail "Invalid reference path."
    done
    [[ "$path" != */ ]] || entry_fail "Invalid reference path."
    [[ "$url" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?/[^[:space:]@?#]+$ ]] ||
        entry_fail "Reference URL must be HTTPS without credentials, query or fragment."
    git check-ref-format "refs/heads/$branch" > /dev/null 2>&1 &&
        [[ "$branch" != -* ]] || entry_fail "Invalid branch."
    reference_dir="$root/$path"
    if [[ "$default_reference" == true && -n "${PLEASANTER_REFERENCE_DIR:-}" ]]; then
        reference_dir="$(normalize_path "$PLEASANTER_REFERENCE_DIR")"
        echo "Read PLEASANTER_REFERENCE_DIR at $reference_dir; no local link is created." >&2
    else
        local current="$root"
        for component in "${components[@]}"; do
            current="$current/$component"
            [[ ! -L "$current" ]] || entry_fail "Reference path must not contain symbolic links."
            if [[ -d "$current" ]]; then
                local physical
                physical="$(cd "$current" && pwd -P)"
                [[ "$physical" == "$root/"* ]] || entry_fail "Reference path escapes root."
            fi
        done
    fi
    [[ ! -L "$reference_dir" ]] || entry_fail "Reference directory must not be a symbolic link."
    mkdir -p "$(dirname "$reference_dir")"
    reference_dir="$(cd "$(dirname "$reference_dir")" && pwd -P)/$(basename "$reference_dir")"
    lock_dir="${reference_dir}.sync-lock"
    mkdir "$lock_dir" 2> /dev/null || entry_fail "Reference sync is already running, or cannot lock."
    locked=true
    unset GIT_TRACE GIT_TRACE_PACKET GIT_TRACE_CURL GIT_CURL_VERBOSE \
        GIT_TRACE_SETUP GIT_TRACE_PERFORMANCE GIT_TRACE2 GIT_TRACE2_EVENT GIT_TRACE2_PERF
    local git_options=()
    if [[ "$private" == true && "$url" == https://github.com/* ]] &&
        command -v gh > /dev/null 2>&1; then
        # トークンは credential helper の標準入出力内だけを通し、引数や設定へ保存しない。
        git_options+=(-c 'credential.https://github.com.helper=!f() {
            set +x
            [ "$1" = get ] || exit 0
            unset GH_DEBUG
            token=$(gh auth token --hostname github.com 2>/dev/null) || exit 0
            printf "username=x-access-token\npassword=%s\n" "$token"
        }; f')
    fi
    export GIT_TERMINAL_PROMPT=0 GCM_INTERACTIVE=never GH_PROMPT_DISABLED=1
    check_clean() {
        local status
        status="$(git -C "$reference_dir" status --porcelain --untracked-files=all 2> /dev/null)" ||
            entry_fail "Cannot inspect reference worktree."
        [[ -z "$status" ]] || entry_fail "Reference worktree is dirty; preserve changes manually."
    }
    if [[ -e "$reference_dir/.git" ]]; then
        [[ "$(git -C "$reference_dir" rev-parse --is-inside-work-tree 2> /dev/null)" == true ]] ||
            entry_fail "Reference directory is not a Git worktree."
        local git_root
        git_root="$(git -C "$reference_dir" rev-parse --show-toplevel)"
        [[ "$(cd "$git_root" && pwd -P)" == "$(cd "$reference_dir" && pwd -P)" ]] ||
            entry_fail "Reference directory must be the root of its Git worktree."
        inspected=true
        [[ "$(git -C "$reference_dir" config --get remote.origin.url)" == "$url" ]] ||
            entry_fail "Reference origin must match the manifest URL."
        check_clean
        git "${git_options[@]}" -C "$reference_dir" fetch --quiet --depth "$depth" origin \
            "refs/heads/$branch" > /dev/null 2>&1 || unavailable
        check_clean
        git -C "$reference_dir" checkout --quiet --detach FETCH_HEAD > /dev/null 2>&1 ||
            entry_fail "Cannot check out fetched reference."
    else
        if [[ -e "$reference_dir" ]]; then
            [[ -d "$reference_dir" && -z "$(ls -A "$reference_dir")" ]] ||
                entry_fail "Reference destination exists and is not an empty directory or Git worktree."
        fi
        clone_dir="$(mktemp -d "${reference_dir}.clone.XXXXXX")"
        git "${git_options[@]}" clone --quiet --depth "$depth" --branch "$branch" --single-branch \
            "$url" "$clone_dir" > /dev/null 2>&1 || unavailable
        git -C "$clone_dir" show-ref --verify --quiet "refs/remotes/origin/$branch" ||
            unavailable
        git -C "$clone_dir" checkout --quiet --detach HEAD > /dev/null 2>&1 ||
            entry_fail "Cannot detach cloned reference."
        if [[ -d "$reference_dir" ]]; then
            rmdir "$reference_dir"
        fi
        mv -- "$clone_dir" "$reference_dir"
        clone_dir=""
        inspected=true
    fi
    state=updated
)

result=0
seen_paths=()
while IFS= read -r -d '' kind && IFS= read -r -d '' path &&
    IFS= read -r -d '' url && IFS= read -r -d '' branch &&
    IFS= read -r -d '' private && IFS= read -r -d '' depth; do
    for seen in "${seen_paths[@]}"; do
        if [[ "${path,,}/" == "${seen,,}/"* || "${seen,,}/" == "${path,,}/"* ]]; then
            kind=overlap
        fi
    done
    seen_paths+=("$path")
    # if の条件内では errexit が無効になるため、項目の実行とは分離する。
    set +e
    sync_entry "$kind" "$path" "$url" "$branch" "$private" "$depth" >> "$summary"
    entry_result=$?
    set -e
    if [[ "$entry_result" -ne 0 ]]; then
        result=1
    fi
done < "$records"
cat "$summary"
exit "$result"
