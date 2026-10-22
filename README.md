# VehicleVision.PleasanterTools.Template

Pleasanter 向けツールを作るときの、社内用テンプレートリポジトリです。標準 Web API を叩く別アプリとして、C#／.NET 10 で作ります。

## 含まれるもの

- 最小の Pleasanter API クライアント（`src/VehicleVision.PleasanterTools.Template.Core`）
- 接続確認の CLI（`src/VehicleVision.PleasanterTools.Template.Cli`）
- 単体テスト（`tests/`）
- 参照専用の Pleasanter 本体ソース（`_reference/Implem.Pleasanter`、サブモジュール）
- CI、CodeQL、依存の脆弱性検査、リリース、参照ソースの定期更新（`.github/workflows/`）
- AI エージェント向けの共通指示（`.github/copilot-instructions.md`、`AGENTS.md`、`CLAUDE.md`）

## 使い方

接続確認の CLI を動かすには、接続先を環境変数で渡します。

```bash
export PLEASANTER_BASE_URL=https://pleasanter.example.com
export PLEASANTER_API_KEY=...
dotnet run --project src/VehicleVision.PleasanterTools.Template.Cli -- check
```

新しいツールの作り方は [開発ガイド](_documents/開発ガイド.md) を参照してください。

## ライセンス

既定は AGPL-3.0-or-later です（[LICENSE](LICENSE)）。商用公開で AGPL と商用のデュアルライセンスにする手順は [ライセンス](_documents/ライセンス.md) を参照してください。依存のライセンスは [NOTICE](NOTICE) にあります。
