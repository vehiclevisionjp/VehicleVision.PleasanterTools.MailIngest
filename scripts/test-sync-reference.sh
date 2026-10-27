#!/usr/bin/env bash
# 実際の取得スクリプトをローカル bare リポジトリで試す（ネットワーク不要）。
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd -P)"
mkdir -p "$repo_root/temp"
test_root="$(mktemp -d "$repo_root/temp/reference-sync-test.XXXXXX")"
trap 'rm -rf -- "$test_root"' EXIT
trap 'echo "Reference test failed at line $LINENO." >&2; cat "$test_root/output" >&2' ERR
export GIT_CONFIG_GLOBAL="$test_root/gitconfig"
export GIT_CONFIG_NOSYSTEM=1
unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE
unset GH_TOKEN GITHUB_TOKEN GH_ENTERPRISE_TOKEN GITHUB_ENTERPRISE_TOKEN GH_DEBUG
export GIT_TERMINAL_PROMPT=0 GCM_INTERACTIVE=never
upstream="https://github.com/Implem/Implem.Pleasanter.git"
git config --global user.name 'Reference test'
git config --global user.email 'reference-test@example.invalid'
git config --global core.autocrlf false
git config --global "url.file://$test_root/remote.git.insteadOf" "$upstream"
git init --quiet --initial-branch=main "$test_root/source"
mkdir -p "$test_root/source/Implem.Pleasanter"
printf '<Project><Version>1.0.0</Version></Project>\n' > "$test_root/source/Implem.Pleasanter/Implem.Pleasanter.csproj"
git -C "$test_root/source" add .
git -C "$test_root/source" commit --quiet -m '初期テスト用コミット'
git clone --quiet --bare "$test_root/source" "$test_root/remote.git"
export PLEASANTER_REFERENCE_DIR="$test_root/shared reference"
sync_reference() {
    bash "$repo_root/scripts/sync-reference.sh" "$@" > "$test_root/output" 2>&1 || {
        cat "$test_root/output" >&2
        return 1
    }
}
expect_failure() {
    local expected="$1"
    shift
    if bash "$repo_root/scripts/sync-reference.sh" "$@" > "$test_root/output" 2>&1; then
        echo 'Expected sync failure.' >&2
        exit 1
    fi
    grep -q "$expected" "$test_root/output"
}
sync_reference
first_sha="$(git -C "$test_root/source" rev-parse HEAD)"
[[ "$(git -C "$PLEASANTER_REFERENCE_DIR" rev-parse HEAD)" == "$first_sha" ]]
[[ "$(git -C "$PLEASANTER_REFERENCE_DIR" rev-parse --is-shallow-repository)" == true ]]
! git -C "$PLEASANTER_REFERENCE_DIR" symbolic-ref -q HEAD
grep -Eq "SHA=[0-9a-f]+ Date=.+ Status=updated FullSHA=$first_sha Retrieved=.+ Version=1.0.0$" "$test_root/output"
grep -q 'no local link is created' "$test_root/output"
printf '<Project><Version>1.0.1</Version></Project>\n' > "$test_root/source/Implem.Pleasanter/Implem.Pleasanter.csproj"
git -C "$test_root/source" commit --quiet -am 'テスト用バージョンを更新する'
git -C "$test_root/source" push --quiet "$test_root/remote.git" main
sync_reference
second_sha="$(git -C "$test_root/source" rev-parse HEAD)"
[[ "$first_sha" != "$second_sha" ]]
[[ "$(git -C "$PLEASANTER_REFERENCE_DIR" rev-parse HEAD)" == "$second_sha" ]]
grep -q 'Version=1.0.1$' "$test_root/output"

project="$PLEASANTER_REFERENCE_DIR/Implem.Pleasanter/Implem.Pleasanter.csproj"
printf 'dirty\n' >> "$project"
expect_failure 'worktree is dirty'
grep -q dirty "$project"
git -C "$PLEASANTER_REFERENCE_DIR" add .
expect_failure 'worktree is dirty'
git -C "$PLEASANTER_REFERENCE_DIR" restore --source=HEAD --staged --worktree .
touch "$PLEASANTER_REFERENCE_DIR/untracked"
expect_failure 'worktree is dirty'
rm "$PLEASANTER_REFERENCE_DIR/untracked"
git -C "$PLEASANTER_REFERENCE_DIR" remote set-url origin 'https://example.invalid/unrelated.git'
expect_failure 'origin must match'
git -C "$PLEASANTER_REFERENCE_DIR" remote set-url origin "$upstream"
mv "$test_root/remote.git" "$test_root/offline.git"
expect_failure 'retrieval failed'
[[ "$(git -C "$PLEASANTER_REFERENCE_DIR" rev-parse HEAD)" == "$second_sha" ]]
git -C "$PLEASANTER_REFERENCE_DIR" diff --quiet
PLEASANTER_REFERENCE_DIR="$test_root/failed-clone" expect_failure 'retrieval failed'
[[ ! -e "$test_root/failed-clone" && ! -e "$test_root/failed-clone.sync-lock" ]]
[[ -z "$(find "$test_root" -maxdepth 1 -name '*.clone.*' -print)" ]]
mv "$test_root/offline.git" "$test_root/remote.git"
mkdir "$test_root/unrelated"
touch "$test_root/unrelated/keep"
PLEASANTER_REFERENCE_DIR="$test_root/unrelated" expect_failure 'destination exists'
[[ -f "$test_root/unrelated/keep" ]]
mkdir "$test_root/empty"
PLEASANTER_REFERENCE_DIR="$test_root/empty" sync_reference
[[ -d "$test_root/empty/.git" ]]
mkdir "$PLEASANTER_REFERENCE_DIR.sync-lock"
expect_failure 'already running'
rmdir "$PLEASANTER_REFERENCE_DIR.sync-lock"
[[ ! -L "$repo_root/_reference/Implem.Pleasanter" ]]
git -C "$PLEASANTER_REFERENCE_DIR" worktree add --quiet --detach "$test_root/linked" HEAD
PLEASANTER_REFERENCE_DIR="$test_root/linked" sync_reference
[[ -f "$test_root/linked/.git" ]]

# 既定一覧の探索も、実リポジトリへ references.json を置かずに検証する。
fixture="$test_root/fixture"
mkdir -p "$fixture/scripts"
cp "$repo_root/scripts/sync-reference.sh" "$fixture/scripts/"
git -C "$test_root/source" checkout --quiet -b develop
printf 'branch content\n' > "$test_root/source/branch"
git -C "$test_root/source" add .
git -C "$test_root/source" commit --quiet -m 'ブランチ指定のテスト'
branch_sha="$(git -C "$test_root/source" rev-parse HEAD)"
git -C "$test_root/source" push --quiet "$test_root/remote.git" develop
public_url='https://example.invalid/public.git'
private_url='https://example.invalid/private.git'
git config --global --add "url.file://$test_root/remote.git.insteadOf" "$public_url"
git config --global "url.file://$test_root/missing.git.insteadOf" "$private_url"
cat > "$fixture/references.json" <<JSON
[
  {"path":"resources/first repo","url":"$public_url","branch":"main"},
  {"path":"resources/private","url":"$private_url","branch":"main","private":true},
  {"path":"resources/last","url":"$public_url","branch":"develop","depth":2}
]
JSON
run_fixture() {
    bash "$fixture/scripts/sync-reference.sh" "$@" > "$test_root/output" 2>&1 || {
        cat "$test_root/output" >&2
        return 1
    }
}
assert_manifest_result() {
    local destination="$1"
    [[ "$(git -C "$destination/resources/first repo" rev-parse HEAD)" == "$second_sha" ]]
    [[ "$(git -C "$destination/resources/last" rev-parse HEAD)" == "$branch_sha" ]]
    [[ "$(git -C "$destination/resources/last" rev-list --count HEAD)" == 2 ]]
    ! git -C "$destination/resources/last" symbolic-ref -q HEAD
    [[ ! -e "$destination/resources/private" && ! -e "$destination/resources/private.sync-lock" ]]
    [[ "$(grep -c '^Reference:' "$test_root/output")" == 3 ]]
    grep -q 'Path=resources/private SHA=- Date=- Status=unavailable$' "$test_root/output"
    grep -Eq 'Path=resources/last SHA=[0-9a-f]+ Date=.+ Status=updated$' "$test_root/output"
}
run_fixture
assert_manifest_result "$fixture"
# 一覧が存在すると PLEASANTER_REFERENCE_DIR は無視される。
[[ "$(git -C "$PLEASANTER_REFERENCE_DIR" rev-parse HEAD)" == "$second_sha" ]]
run_fixture --root "$test_root/alternate root"
assert_manifest_result "$test_root/alternate root"
sync_reference --manifest "$fixture/references.json" --root "$test_root/explicit root"
assert_manifest_result "$test_root/explicit root"
run_fixture # fetch 経路と private の失敗後の継続
assert_manifest_result "$fixture"
touch "$fixture/resources/first repo/untracked"
if bash "$fixture/scripts/sync-reference.sh" > "$test_root/output" 2>&1; then
    echo 'Expected dirty manifest item failure.' >&2
    exit 1
fi
grep -q 'worktree is dirty' "$test_root/output"
grep -q 'Path=resources/last .*Status=updated$' "$test_root/output"
[[ -f "$fixture/resources/first repo/untracked" ]]
rm "$fixture/resources/first repo/untracked"

cat > "$test_root/failures.json" <<JSON
[
  {"path":"public-failure","url":"$private_url","branch":"main"},
  {"path":"private-failure","url":"$private_url","branch":"main","private":true},
  {"path":"missing-branch","url":"$public_url","branch":"absent"},
  {"path":"success","url":"$public_url","branch":"develop"}
]
JSON
expect_failure 'retrieval failed' --manifest "$test_root/failures.json" --root "$test_root/failures"
[[ "$(grep -c 'Status=failed$' "$test_root/output")" == 2 ]]
grep -q 'Path=success .*Status=updated$' "$test_root/output"
[[ "$(git -C "$test_root/failures/success" rev-parse HEAD)" == "$branch_sha" ]]
git -C "$test_root/source" tag tag-only
git -C "$test_root/source" push --quiet "$test_root/remote.git" tag-only
printf '[{"path":"tag-only","url":"%s","branch":"tag-only"}]\n' "$public_url" > "$test_root/tag.json"
expect_failure 'retrieval failed' --manifest "$test_root/tag.json" --root "$test_root/failures"
[[ ! -e "$test_root/failures/tag-only" ]]

default_fixture="$test_root/default-fixture"
mkdir -p "$default_fixture/scripts"
cp "$repo_root/scripts/sync-reference.sh" "$default_fixture/scripts/"
PLEASANTER_REFERENCE_DIR="" bash "$default_fixture/scripts/sync-reference.sh" > "$test_root/output" 2>&1
[[ "$(git -C "$default_fixture/_reference/Implem.Pleasanter" rev-parse HEAD)" == "$second_sha" ]]
if bash "$default_fixture/scripts/sync-reference.sh" --root "$test_root/no-manifest-root" \
    > "$test_root/output" 2>&1; then
    echo 'Expected --root without manifest failure.' >&2
    exit 1
fi
grep -q -- '--root requires a manifest' "$test_root/output"
run_fixture --manifest references.json --root relative-root
assert_manifest_result "$fixture/relative-root"

# GitHub 認証は偽の gh と credential fill で試し、ネットワークにも実資格情報にも触れない。
auth_bin="$test_root/auth-bin"
mkdir "$auth_bin"
export REAL_GIT
REAL_GIT="$(command -v git)"
export AUTH_CAPTURE="$test_root/auth-capture" AUTH_ARGUMENTS="$test_root/auth-arguments"
cat > "$auth_bin/gh" <<'SH'
#!/bin/bash
set -eu
[[ -z "${GH_DEBUG:-}" ]]
[[ "$*" == 'auth token --hostname github.com' ]]
printf 'reference-test-placeholder\n'
SH
cat > "$auth_bin/git" <<'SH'
#!/bin/bash
set -euo pipefail
printf '%s\n' "$@" >> "$AUTH_ARGUMENTS"
options=()
while [[ "${1:-}" == -c ]]; do
    options+=("$1" "$2")
    shift 2
done
if [[ "${1:-}" == clone ]]; then
    printf 'protocol=https\nhost=github.com\n\n' |
        "$REAL_GIT" "${options[@]}" credential fill > "$AUTH_CAPTURE"
fi
exec "$REAL_GIT" "${options[@]}" "$@"
SH
chmod +x "$auth_bin/git" "$auth_bin/gh"
auth_url='https://github.com/reference-test/private.git'
git config --global --add "url.file://$test_root/remote.git.insteadOf" "$auth_url"
printf '[{"path":"private","url":"%s","branch":"main","private":true}]\n' "$auth_url" > "$test_root/auth.json"
GH_DEBUG=api PATH="$auth_bin:$PATH" sync_reference --manifest "$test_root/auth.json" --root "$test_root/auth"
grep -q 'password=reference-test-placeholder' "$AUTH_CAPTURE"
! grep -q 'reference-test-placeholder' "$AUTH_ARGUMENTS" "$test_root/output" "$test_root/auth/private/.git/config"
git config --global credential.https://github.com.helper \
    '!f() { [ "$1" = get ] && printf "username=user\npassword=configured-helper-placeholder\n"; }; f'
PATH="$auth_bin:$PATH" sync_reference --manifest "$test_root/auth.json" --root "$test_root/configured-auth"
grep -q 'password=configured-helper-placeholder' "$AUTH_CAPTURE"
! grep -q 'configured-helper-placeholder' "$AUTH_ARGUMENTS" "$test_root/output" \
    "$test_root/configured-auth/private/.git/config"
git config --global --unset-all credential.https://github.com.helper
touch "$test_root/auth/private/untracked"
expect_failure 'worktree is dirty' --manifest "$test_root/auth.json" --root "$test_root/auth"
grep -q 'Status=failed$' "$test_root/output"
[[ -f "$test_root/auth/private/untracked" ]]
rm "$test_root/auth/private/untracked"
mv "$test_root/remote.git" "$test_root/offline.git"
sync_reference --manifest "$test_root/auth.json" --root "$test_root/auth"
grep -Eq 'SHA=[0-9a-f]+ Date=.+ Status=unavailable$' "$test_root/output"
[[ "$(git -C "$test_root/auth/private" rev-parse HEAD)" == "$second_sha" ]]
mv "$test_root/offline.git" "$test_root/remote.git"

for path in '../escape' '/absolute' 'C:/absolute' '.git/config' '.GIT/config' '.github-copy' \
    'nested/../escape' 'nested/.git/config' 'nested\escape' 'trailing.' 'trailing ' 'empty//part'; do
    # テスト値のバックスラッシュだけ JSON 用にエスケープする。
    json_path="${path//\\/\\\\}"
    printf '[{"path":"%s","url":"%s","branch":"main"},' "$json_path" "$public_url" > "$test_root/invalid.json"
    printf '{"path":"after-invalid","url":"%s","branch":"main"}]\n' "$public_url" >> "$test_root/invalid.json"
    expect_failure 'Invalid reference path' --manifest "$test_root/invalid.json" --root "$test_root/invalid"
    grep -q 'Path=after-invalid .*Status=updated$' "$test_root/output"
done
[[ ! -e "$test_root/escape" ]]
mkdir -p "$test_root/link-root" "$test_root/link-outside"
if MSYS=winsymlinks:nativestrict ln -s "$test_root/link-outside" "$test_root/link-root/linked" 2> /dev/null &&
    [[ -L "$test_root/link-root/linked" ]]; then
    printf '[{"path":"linked/repo","url":"%s","branch":"main"}]\n' "$public_url" > "$test_root/link.json"
    expect_failure 'symbolic links' --manifest "$test_root/link.json" --root "$test_root/link-root"
    [[ ! -e "$test_root/link-outside/repo" ]]
else
    echo 'Symbolic link test skipped: native symbolic links are unavailable.'
fi
for fields in \
    '"path":"bad","url":"http://example.invalid/a.git","branch":"main"' \
    '"path":"bad","url":"https://user:secret@example.invalid/a.git","branch":"main"' \
    '"path":"bad","url":"https://example.invalid/a.git?token=secret","branch":"main"' \
    '"path":"bad","url":"https://example.invalid/a.git","branch":"--option"' \
    '"path":"bad","url":"https://example.invalid/a.git","branch":"main","private":"true"' \
    '"path":"bad","url":"https://example.invalid/a.git","branch":"main","depth":0' \
    '"path":"bad","url":"https://example.invalid/a.git","branch":"main","depth":true' \
    '"path":"bad","url":"https://example.invalid/a.git","branch":"main","depth":1.5' \
    '"path":"bad\ninjected","url":"https://example.invalid/a.git","branch":"main"' \
    '"path":"bad","url":"https://example.invalid/a.git"'; do
    printf '[{%s},{"path":"after-invalid","url":"%s","branch":"main"}]\n' \
        "$fields" "$public_url" > "$test_root/invalid.json"
    expect_failure 'Error:' --manifest "$test_root/invalid.json" --root "$test_root/invalid"
    grep -q 'Path=after-invalid .*Status=updated$' "$test_root/output"
    ! grep -q secret "$test_root/output"
done
for json in '[]' '{}' '[null]' '[{"path":' '[] []'; do
    printf '%s\n' "$json" > "$test_root/invalid.json"
    expect_failure 'Error:' --manifest "$test_root/invalid.json"
done
expect_failure 'does not exist' --manifest "$test_root/no-manifest.json"
expect_failure 'requires a path' --root
expect_failure 'Usage:' --unknown
cat > "$test_root/overlap.json" <<JSON
[
  {"path":"same","url":"$public_url","branch":"main"},
  {"path":"same","url":"$public_url","branch":"main"},
  {"path":"same/child","url":"$public_url","branch":"main"}
]
JSON
expect_failure 'Overlapping reference path' --manifest "$test_root/overlap.json" --root "$test_root/overlap"
[[ "$(grep -c 'Status=failed$' "$test_root/output")" == 2 ]]

# パーサーの選択は PATH で再現し、利用可能な各実装と未導入時を試す。
original_path="$PATH"
bash_path="$(command -v bash)"
parser_bin="$test_root/parser-bin"
mkdir "$parser_bin"
for tool in dirname basename mkdir mktemp rm cat git rmdir ls mv date sed; do
    printf '#!/bin/bash\nPATH="%s" exec "%s" "$@"\n' \
        "$original_path" "$(command -v "$tool")" > "$parser_bin/$tool"
    chmod +x "$parser_bin/$tool"
done
for parser in jq node python3 python none; do
    if [[ "$parser" != none ]] && ! command -v "$parser" > /dev/null 2>&1; then
        continue
    fi
    if [[ "$parser" == python* ]] && ! "$parser" -c 'import sys' > /dev/null 2>&1; then
        echo "Parser test skipped: $parser is not runnable."
        continue
    fi
    if [[ "$parser" != none ]]; then
        printf '#!/bin/bash\nPATH="%s" exec "%s" "$@"\n' \
            "$original_path" "$(command -v "$parser")" > "$parser_bin/$parser"
        chmod +x "$parser_bin/$parser"
        if ! PATH="$parser_bin" "$bash_path" "$fixture/scripts/sync-reference.sh" \
            --root "$test_root/parser-$parser" > "$test_root/output" 2>&1; then
            cat "$test_root/output" >&2
            exit 1
        fi
        assert_manifest_result "$test_root/parser-$parser"
        if PATH="$parser_bin" "$bash_path" "$fixture/scripts/sync-reference.sh" \
            --manifest "$test_root/invalid.json" > "$test_root/output" 2>&1; then
            echo "Expected invalid JSON failure with $parser." >&2
            exit 1
        fi
        grep -q 'Cannot parse manifest' "$test_root/output"
        rm "$parser_bin/$parser"
    else
        if PATH="$parser_bin" "$bash_path" "$fixture/scripts/sync-reference.sh" > "$test_root/output" 2>&1; then
            echo 'Expected missing parser failure.' >&2
            exit 1
        fi
        grep -q 'jq, node or python' "$test_root/output"
    fi
done
[[ "$PATH" == "$original_path" ]]

isolation="$test_root/isolation"
mkdir -p "$isolation/scripts" "$isolation/src"
cp "$repo_root/scripts/reference-isolation-check.sh" "$isolation/scripts/"
printf '<Project />\n' > "$isolation/Directory.Build.props"
bash "$isolation/scripts/reference-isolation-check.sh" > "$test_root/isolation-output"
for reference in "$PLEASANTER_REFERENCE_DIR" "${PLEASANTER_REFERENCE_DIR//\//\\}" '_reference/Implem.Pleasanter'; do
    printf '<Project><Compile Include="%s/source.cs" /></Project>\n' "$reference" > "$isolation/src/test.csproj"
    if bash "$isolation/scripts/reference-isolation-check.sh" > "$test_root/isolation-output" 2>&1; then
        echo 'Expected reference isolation failure.' >&2
        exit 1
    fi
done
rm "$isolation/src/test.csproj" "$isolation/Directory.Build.props"
if bash "$isolation/scripts/reference-isolation-check.sh" > "$test_root/isolation-output" 2>&1; then
    echo 'Expected failure for missing build input.' >&2
    exit 1
fi
grep -q 'isolation scan failed' "$test_root/isolation-output"
echo 'Reference sync tests passed: defaults, manifests, branches, private/public failures, dirty worktrees, validation, parsers, isolation.'
