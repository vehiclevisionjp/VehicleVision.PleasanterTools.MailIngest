#!/usr/bin/env bash
# .github/copilot-instructions.md の AGENT-RULES ブロックを AGENTS.md へ複写する。
# 単一の参照元は .github/copilot-instructions.md。AGENTS.md を直接編集しないこと。
# 差分の検査は `scripts/sync-agents.sh --check`。
set -euo pipefail
cd "$(dirname "$0")/.."

SRC=.github/copilot-instructions.md
DST=AGENTS.md

# BEGIN コメントの閉じ (-->) の次行から、END マーカー行の直前までを取り出す。
# 注意: "AGENT-RULES:END" は BEGIN コメントの説明文中にも現れるため、
#       行頭の "<!-- AGENT-RULES:END -->" だけを終端として扱う。
extract_rules() {
    awk '
        /^<!-- AGENT-RULES:BEGIN/ { inhdr = 1; next }
        inhdr && /-->/            { inhdr = 0; body = 1; next }
        inhdr                     { next }
        /^<!-- AGENT-RULES:END/   { body = 0; next }
        body                      { print }
    ' "$SRC" | rebase_links
}

# 相対リンクを AGENTS.md（リポジトリ直下）から見た位置へ書き換える。
# 参照元は .github/ にあるため、`../NOTICE` は `NOTICE` に、
# `instructions/x.md` は `.github/instructions/x.md` にしないと壊れる（Issue #480）。
# 1. スキーム（`:` を含む）・絶対パス・アンカーを除く相対リンクへ `.github/` を前置する
# 2. `.github/../` を打ち消す
rebase_links() {
    sed -E \
        -e 's#\]\(([^)/:#][^):]*)\)#](.github/\1)#g' \
        -e 's#\]\(\.github/\.\./#](#g'
}

build() {
    cat <<'HEADER'
# エージェント向けリポジトリ指示

このファイルは `AGENTS.md` 標準（<https://agents.md/>）を読むコーディングエージェント向けの指示です。
**主な想定はローカルで動かす Codex CLI などです。**

GitHub 上のエージェントは `.github/copilot-instructions.md` を直接読みます。
Claude Code はローカルで `CLAUDE.md` を読み、そこから同じ参照元を取り込みます。
**このファイルが要るのは、AGENTS.md 標準が include の仕組みを規定しておらず、
規約の本文が無いと届かないためです。**

**規約の全文は [`.github/copilot-instructions.md`](.github/copilot-instructions.md) にあります。
以下は同ファイルの「必ず守る規約」の写しです。作業前に参照元を必ず開いてください。**

<!-- この節は .github/copilot-instructions.md から生成される。ここを直接編集しないこと。
     編集は .github/copilot-instructions.md 側で行い、`scripts/sync-agents.sh` を実行する。 -->
HEADER
    extract_rules
}

if [ "${1:-}" = "--check" ]; then
    if diff -u "$DST" <(build) > /dev/null 2>&1; then
        echo "OK: $DST は $SRC と同期している"
    else
        echo "NG: $DST が $SRC と食い違っている。scripts/sync-agents.sh を実行すること" >&2
        diff -u "$DST" <(build) || true
        exit 1
    fi
else
    build > "$DST"
    echo "生成しました: $DST"
fi
