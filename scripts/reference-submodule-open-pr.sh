#!/usr/bin/env bash
# 参照用サブモジュール `_reference/Implem.Pleasanter` のポインタを、上流（Implem/Implem.Pleasanter）の
# main の先頭へ進め、Issue と Draft PR を出す。
# 日次のワークフロー（.github/workflows/reference-submodule-update.yml）から呼ぶ。
#
# **main へ追従する 1 本の PR にする。** 上流の main は週に数回動く。更新のたびに別の PR を作ると、
# 未マージの PR が積み上がる。**開いている PR があれば、同じブランチへコミットを足して、
# ポインタを最新へ進める**（PR 本文の SHA も書き換える）。無ければ、Issue → ブランチ → Draft PR を出す。
#
# **本文の文面をここに置くのは、YAML の中でヒアドキュメントが書けないため。**
# `run: |` の中は行頭が字下げされるので、閉じの `EOF` が行頭に来ず、
# 素のヒアドキュメントは終端しない。**文面を扱う処理はスクリプト側へ寄せる。**
#
# ⚠️ **サブモジュールは clone しない。** 索引の gitlink（mode 160000）を
#    `git update-index --cacheinfo` で直接書き換える。上流は大きく、
#    ポインタを進めるためだけに毎日取ってくる必要が無い。
#
# ⚠️ **ドキュメントの版記述は書き換えない。** `_reference/README.md` や
#    `_documents/アーキテクチャ方針.md` は「この版の実ソースを根拠にしている」と
#    書いている。**版だけ機械的に上げると、確認していない版を根拠にしたことになる。**
#    PR 本文へ確認先を列挙して、人がやり直す前提にする。
#
# ⚠️ **main はリリース前の変更を含む。** 「どの版か」は、コミット SHA と <Version> で言う。
#
# 要る環境変数
#   GH_TOKEN        gh コマンド用（ブランチの push と PR の作成。GitHub App のトークン）
#   ISSUE_GH_TOKEN  Issue の作成だけに使うトークン（GITHUB_TOKEN。既定は GH_TOKEN）。
#                   App に Issues の権限を足さずに済ませるため
#   LATEST_SHA / CURRENT_SHA
#                   scripts/reference-submodule-check.sh の出力（必須）
#   LATEST_REF / LATEST_TAG / LATEST_VERSION / CURRENT_TAG
#                   同上（表示用。無くてよい）
#   BASE_BRANCH     PR の base（既定ブランチ。develop）
#
# 参照した一次情報（いずれも 2026-09-11 参照）
#   - git update-index --cacheinfo（gitlink は mode 160000）
#     https://git-scm.com/docs/git-update-index
#   - gh issue create / gh pr create / gh pr edit
#     https://cli.github.com/manual/gh_issue_create
#     https://cli.github.com/manual/gh_pr_create
#     https://cli.github.com/manual/gh_pr_edit
set -euo pipefail
cd "$(dirname "$0")/.."

: "${LATEST_SHA:?LATEST_SHA が要ります}"
: "${CURRENT_SHA:?CURRENT_SHA が要ります}"
LATEST_REF="${LATEST_REF:-main}"
LATEST_TAG="${LATEST_TAG:-}"
LATEST_VERSION="${LATEST_VERSION:-}"
CURRENT_TAG="${CURRENT_TAG:-}"
BASE_BRANCH="${BASE_BRANCH:-develop}"

SUBMODULE_PATH="_reference/Implem.Pleasanter"
short="${LATEST_SHA:0:8}"
branch="chore/reference-submodule-main"

# 表示用の名前。タグや <Version> が取れていなくても、SHA だけで書ける
latest_label="${LATEST_REF} の先頭 \`${short}\`"
if [[ -n "$LATEST_VERSION" ]]; then
    latest_label+="（Version ${LATEST_VERSION}）"
fi
if [[ -n "$LATEST_TAG" ]]; then
    latest_label+="、\`${LATEST_TAG}\` と同じコミット"
fi
current_label="${CURRENT_TAG:-${CURRENT_SHA:0:8}}"

summary() {
    if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
        cat >>"$GITHUB_STEP_SUMMARY"
    else
        cat
    fi
}

# PR 本文。引数は Issue 番号。新規作成と、開いている PR の更新で同じ文面を使う
pr_body() {
    cat <<MARKDOWN
## 概要

\`_reference/Implem.Pleasanter\` のポインタを
\`${current_label}\` から ${latest_label}（\`$LATEST_SHA\`）へ進める。

日次の検査（\`.github/workflows/reference-submodule-update.yml\`）が、上流の \`${LATEST_REF}\` に
新しいコミットを検出して自動で出した。

**この PR は、上流の \`${LATEST_REF}\` へ追従する 1 本の PR。** 未マージのうちに \`${LATEST_REF}\` が進んだら、
同じ PR へコミットを足してポインタを最新へ進める（この本文の SHA もそのとき書き換わる）。

## ⚠️ マージ前にやること

**版を書いてあるドキュメントは自動で書き換えていない。**
「この版の実ソースを根拠にしている」と書いてある文書の版だけ機械的に上げると、
**確認していない版を根拠にしたことになる。** 次を人が確認して揃えること。
**\`${LATEST_REF}\` はリリース前の変更を含む。** 文書には、確認した**コミットと \`<Version>\`** を書くこと
（リリース版との対応は \`Pleasanter_*\` タグで確かめる）。

- [ ] \`_reference/README.md\` の「固定位置」
- [ ] \`_documents/アーキテクチャ方針.md\` 冒頭の「根拠にした版」と、
      各節が引用するソースパスが新しいコミットにも存在すること
- [ ] \`README.md\` の技術スタックが指す版（対象フレームワークの変化も見る）
- [ ] \`tools/pleasanter-testenv\` のイメージを上げるかどうかの判断
      （上げるなら E2E を \`workflow_dispatch\` で先に流して確かめる）

GitHub App の短期トークンで作成した PR のため、通常の CI が起動する。

## 制約の確認

- **コードの取り込みは行っていない。** 参照専用のポインタ更新のみ
- サブモジュールはビルド対象・ソリューションに含まれないため、実装への影響は無い

Closes #${1}
MARKDOWN
}

# ポインタを進めて、現在のブランチへコミットする。引数はコミットメッセージ
commit_pointer() {
    git update-index --cacheinfo "160000,$LATEST_SHA,$SUBMODULE_PATH"
    git -c user.name="github-actions[bot]" \
        -c user.email="41898282+github-actions[bot]@users.noreply.github.com" \
        commit -m "$1"
}

commit_message() {
    cat <<MESSAGE
参照用サブモジュール Implem.Pleasanter を ${LATEST_REF} の先頭（${short}）へ進める

固定位置が ${current_label} のままで、上流の ${LATEST_REF} の先頭より遅れていた。
参照専用のサブモジュールが古いと、API 仕様やスキーマの確認結果が
現行の Pleasanter と食い違う。

検証したこと: 日次のワークフローが、上流の ${LATEST_REF} の先頭（${LATEST_SHA}）と
固定位置が違うことを確認した。サブモジュールはビルド対象外のため本体のビルドには影響しない。
コードの取り込みは行っていない。

版を書いてあるドキュメントは自動で書き換えていない（PR 本文を参照）。
MESSAGE
}

# ---- 開いている追従 PR があれば、同じブランチへコミットを足す ----
existing="$(gh pr list --head "$branch" --state open --json number --jq '.[0].number // ""')"
if [[ -n "$existing" ]]; then
    git fetch origin "$branch"
    remote_sha="$(git ls-tree "origin/$branch" "$SUBMODULE_PATH" | awk '$2 == "commit" { print $3 }')"
    if [[ "$remote_sha" == "$LATEST_SHA" ]]; then
        summary <<MARKDOWN
## 既に PR が出ています

${LATEST_REF} の先頭（\`$short\`）への更新は #$existing で進行中です。
MARKDOWN
        exit 0
    fi

    # 本文の「Closes #N」から Issue 番号を引き継ぐ
    issue_number="$(gh pr view "$existing" --json body --jq '.body' | grep -oE 'Closes #[0-9]+' | head -n 1 | grep -oE '[0-9]+' || true)"
    if [[ -z "$issue_number" ]]; then
        echo "::error::PR #$existing の本文に 'Closes #N' が見つからない。Issue 番号を引き継げない。" >&2
        exit 1
    fi

    git switch -C "$branch" "origin/$branch"
    commit_pointer "$(commit_message)"
    git push origin "$branch"
    body_file="$(mktemp)"
    pr_body "$issue_number" >"$body_file"
    gh pr edit "$existing" --body-file "$body_file"
    rm -f "$body_file"

    summary <<MARKDOWN
## 開いている PR を更新しました

- #$existing: ポインタを ${latest_label} へ進めた
- Issue #${issue_number}
MARKDOWN
    exit 0
fi

# 閉じられた PR のブランチが残っていると、同じ名前では push できない。短い SHA を足して避ける
if git ls-remote --exit-code --heads origin "$branch" >/dev/null 2>&1; then
    branch="${branch}-${short}"
fi

issue_url="$(
    env GH_TOKEN="${ISSUE_GH_TOKEN:-${GH_TOKEN:-}}" gh issue create \
        --title "参照用サブモジュール Implem.Pleasanter を上流の ${LATEST_REF} の先頭へ進める" \
        --label "1-owner:claude" \
        --label "2-type:dependencies" \
        --label "3-area:pleasanter" \
        --body "$(
            cat <<MARKDOWN
## 背景

日次の検査（\`.github/workflows/reference-submodule-update.yml\`）が、
\`_reference/Implem.Pleasanter\` の固定位置が、上流の \`${LATEST_REF}\` の先頭より
遅れていることを検出した。

| | コミット | 備考 |
|---|---|---|
| いま | \`$CURRENT_SHA\` | ${CURRENT_TAG:-（タグ無し）} |
| 上流の ${LATEST_REF} | \`$LATEST_SHA\` | ${LATEST_VERSION:+Version ${LATEST_VERSION}}${LATEST_TAG:+、${LATEST_TAG} と同じコミット} |

（検出した時点の値。追従 PR が更新されても、この表は書き換わらない。最新の値は PR の本文を見る。）

このサブモジュールは事実確認のための参照専用（\`_reference/README.md\`）だが、
**古いままだと API 仕様・スキーマの確認結果が現行の Pleasanter と食い違う。**

## 対応内容

- サブモジュールのポインタを、上流の \`${LATEST_REF}\` の先頭へ進める（自動 PR を用意した。\`${LATEST_REF}\` が進めば同じ PR へ足す）
- **版を書いてあるドキュメントを、そのコミットで確認し直して揃える**（人の作業）

## 制約

- **コードの取り込みは行わない。** 参照ポインタの更新のみ
- 検証環境のイメージ（\`tools/pleasanter-testenv\`）は E2E の切り分けに
  影響するため別途扱う
MARKDOWN
        )"
)"
issue_number="${issue_url##*/}"

git switch -c "$branch"
commit_pointer "$(commit_message)

Closes #${issue_number}"
git push origin "$branch"

body_file="$(mktemp)"
pr_body "$issue_number" >"$body_file"
# **Draft ＋ WIP: で出す。** Draft 解除＝レビュー可能、WIP: 除去＝マージ可能の
# 2 段のゲートとして使う（.github/copilot-instructions.md）。
# ドキュメントの版記述を人が揃えるまでは、どちらも外れない
gh pr create \
    --draft \
    --base "$BASE_BRANCH" \
    --head "$branch" \
    --title "WIP: 参照用サブモジュール Implem.Pleasanter を上流の ${LATEST_REF} の先頭へ進める" \
    --label "1-owner:claude" \
    --label "2-type:dependencies" \
    --label "3-area:pleasanter" \
    --body-file "$body_file"
rm -f "$body_file"

summary <<MARKDOWN
## 更新の PR を出しました

- \`${current_label}\` → ${latest_label}
- Issue #${issue_number}
MARKDOWN
