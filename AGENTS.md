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

## 必ず守る規約

1. 応答、Issue、PR、コミット、レビュー、コメントとドキュメントは日本語で書く。CLI の進行メッセージだけは英語でよい。
2. 作業ブランチは `develop` から分岐し、PR の base も `develop` とする。`master` はリリース確定用。
3. 作業に着手する前に、これからやること・やるべきこと（作業中に見つけたものを含む）を、先に Issue にする。会話の「残りの作業」も Issue に起こし、リポジトリをまたぐ作業は、中心のリポジトリに親の Issue を起こして各リポジトリの Issue から参照する。Issue を作ってから変更し、PR 本文に `Closes #N` を書く。初期リポジトリ作成のみ直接配置する。
4. 作業中の PR は Draft とし、タイトルに `WIP:` を付ける。Draft 解除はレビュー可能、WIP 除去はマージ可能の合図。
5. CI をスキップするコミット指定を使わない。実装変更後は Release ビルドと影響するテストを実行する。
6. 本ツールは AGPL と商用のデュアル。Pleasanter 本体は参照専用で、必要時に取得し Git では追跡しない。
   コピー・流用・リンク・プロジェクト参照・配布は禁止。取り込むと商用側を提供できない。
7. API キーとパスワードはコード・設定ファイル・ログ・公開ファイル・ブラウザへ出さない。環境変数か Git 管理外の設定から読む。未検証を動作済みとして説明しない。
8. デュアル提供を維持するため、依存 OSS は寛容ライセンス（MIT / BSD 系 / Apache-2.0）のみ。追加時に一次情報で確認し、NOTICE とロックファイルを更新する。
9. 作業前に [共通指示](.github/copilot-instructions.md) の全文を読む。外部仕様は一次情報で確認し、出典と参照日を残す。
10. README は利用者向けの取扱説明・製品説明の入口にする。ビルド・テスト・CI・エージェント運用など開発者向けの説明は [開発ガイド](_documents/開発ガイド.md) と [開発運用](_documents/開発運用.md) に分離する。
11. `if` / `else` / `for` / `foreach` / `while` / `using` / `lock` などの本文の `{}` を省略しない（1 行で済む本文も同じ）。C# は `.editorconfig` とビルドで、TypeScript・Svelte は ESLint の `curly` で強制する。
