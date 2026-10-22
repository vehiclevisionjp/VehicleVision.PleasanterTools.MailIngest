# Claude Code 向け指示

@.github/copilot-instructions.md

現在の仕様と未決定事項は `_documents/アーキテクチャ方針.md`、運用は `_documents/開発運用.md` を読むこと。

Pleasanter 本体は `_reference/Implem.Pleasanter` の参照専用サブモジュールであり、実装へ取り込まない。

外部記憶は、必要になった時点で `<リポジトリ名>.ClaudeMemory`（Internal）を作って管理する。現時点では作っていない。作ったらここへ URL を書く。
