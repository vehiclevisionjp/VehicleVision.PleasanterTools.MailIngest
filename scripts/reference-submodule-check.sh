#!/usr/bin/env bash
# 参照用サブモジュール `_reference/Implem.Pleasanter` の固定位置が、
# 上流（Implem/Implem.Pleasanter）の main の先頭と一致しているかを見る。
#
# **なぜ main を見るのか。** このサブモジュールは API 仕様・スキーマ・挙動の
# 事実確認にのみ使う参照専用で、**上流の main の先頭を追う**方針
# （`_reference/README.md`）。以前はリリースタグへ固定していたが、2026-10-19 に
# 方針を変えた。リリース前の変更にも早く気付くため。
#
# **「どの版の事実か」は、コミット SHA と、そのコミットの `<Version>` で言う。**
# main はリリース前の変更を含むので、タグだけでは言えない。
# `<Version>` は上流の `Implem.Pleasanter/Implem.Pleasanter.csproj` から読む。
# **取れなくても止めない**（表示のためだけの値。取れなければ空）。
#
# **リリースタグは、表示のためだけに使う。** 「いまのコミットは、どのリリースと
# 同じか」を人が読むための値（latest_tag / current_tag）で、更新の判定には使わない。
#
# ⚠️ **`Release_*` 系のタグは使わない。** 2020 年で更新が止まっており、
#    版の新しさを表していない（`Release_1190` が 2020-11-27、
#    `Pleasanter_1.5.8.1` が 2026-09-10。2026-09-11 に実測）。
#    見るのは `Pleasanter_<x>.<y>.<z>.<w>` の形のタグだけ。
#
# ⚠️ **上流のタグは注釈付きタグ（annotated tag）である。** `git ls-remote --tags` は
#    タグオブジェクトの SHA と、`^{}` を付けた行に指し先のコミット SHA を出す。
#    **サブモジュールのポインタはコミット SHA でなければならない**ので、
#    `^{}` の行だけを採る（2026-09-11 に実測）。
#
# 出力（`$GITHUB_OUTPUT` があればそこへ、無ければ標準出力へ）
#   latest_ref      追従先のブランチ（main）
#   latest_sha      その先頭のコミット SHA
#   latest_tag      その先頭に付いたリリースタグ（無ければ空）
#   latest_version  その先頭の <Version>（取れなければ空）
#   current_tag     いま固定しているコミットに付いたリリースタグ（無ければ空）
#   current_sha     いま固定しているコミット SHA
#   needs_update    更新が要るなら true、最新なら false
#
# 試験用の上書き（本番では設定しない）
#   REFERENCE_UPSTREAM_URL   上流の URL（既定は GitHub）。手元で、ローカルのリポジトリへ向けて試す
#   REFERENCE_UPSTREAM_RAW   <Version> を読む raw の起点
#
# 参照した一次情報（いずれも 2026-09-11 参照）
#   - git ls-remote / gitrevisions の `^{}`（peel）
#     https://git-scm.com/docs/git-ls-remote
#     https://git-scm.com/docs/gitrevisions
#   - git ls-tree（gitlink は mode 160000）
#     https://git-scm.com/docs/git-ls-tree
set -euo pipefail
cd "$(dirname "$0")/.."

SUBMODULE_PATH="_reference/Implem.Pleasanter"
UPSTREAM_URL="${REFERENCE_UPSTREAM_URL:-https://github.com/Implem/Implem.Pleasanter.git}"
UPSTREAM_RAW="${REFERENCE_UPSTREAM_RAW:-https://raw.githubusercontent.com/Implem/Implem.Pleasanter}"
TRACK_REF="main"

# いま固定しているコミット。gitlink は ls-tree の 3 列目に出る
current_sha="$(git ls-tree HEAD "$SUBMODULE_PATH" | awk '$2 == "commit" { print $3 }')"
if [[ -z "$current_sha" ]]; then
    echo "::error::$SUBMODULE_PATH がサブモジュールとして登録されていません。" >&2
    exit 1
fi

# 上流の main の先頭。`git ls-remote` は「SHA<TAB>参照名」で出す
latest_sha="$(
    git ls-remote "$UPSTREAM_URL" "refs/heads/$TRACK_REF" |
        awk -v ref="refs/heads/$TRACK_REF" '$2 == ref { print $1 }'
)"
if [[ ! "$latest_sha" =~ ^[0-9a-f]{40}$ ]]; then
    echo "::error::上流の $TRACK_REF の先頭を取得できません。取得先: $UPSTREAM_URL" >&2
    exit 1
fi

# リリースタグを「版<TAB>コミットSHA」で並べる（表示のためだけ。取れなくても止めない）。
#
# **`sort -V` を素の版文字列へ掛ける。** タグ名ごと渡すと接頭辞が比較に混ざる
tags="$(
    {
        git ls-remote --tags "$UPSTREAM_URL" 'refs/tags/Pleasanter_*' |
            sed -n 's|^\([0-9a-f]\{40\}\)\trefs/tags/Pleasanter_\([0-9][0-9.]*\)\^{}$|\2\t\1|p' |
            sort -V
    } || true
)"

# 指定のコミットに付いたリリースタグ（付いていないこともある）
tag_of() {
    printf '%s\n' "$tags" |
        awk -v sha="$1" -F '\t' '$2 == sha { print "Pleasanter_" $1 }' |
        tail -n 1
}
latest_tag="$(tag_of "$latest_sha")"
current_tag="$(tag_of "$current_sha")"

# <Version>（表示のためだけ。取れなくても止めない）
latest_version="$(
    {
        curl -fsSL --max-time 20 \
            "$UPSTREAM_RAW/$latest_sha/Implem.Pleasanter/Implem.Pleasanter.csproj" 2>/dev/null |
            grep -m1 -oE '<Version>[^<]+' |
            sed 's/<Version>//'
    } || true
)"

if [[ "$current_sha" == "$latest_sha" ]]; then
    needs_update=false
else
    needs_update=true
fi

emit() {
    if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
        printf '%s\n' "$1" >>"$GITHUB_OUTPUT"
    else
        printf '%s\n' "$1"
    fi
}

emit "latest_ref=$TRACK_REF"
emit "latest_sha=$latest_sha"
emit "latest_tag=$latest_tag"
emit "latest_version=$latest_version"
emit "current_tag=$current_tag"
emit "current_sha=$current_sha"
emit "needs_update=$needs_update"
