# VehicleVision.PleasanterTools.MailIngest

POP3 と IMAP でメールを受信し、差出人・件名・本文・日時などを Pleasanter の列へ対応づけ、
標準 Web API でレコードを作成・更新するためのツールです。C#／.NET 10 の独立したアプリとして開発します。

## 現在の状態

**初期化・設計段階です。メール受信、重複排除、列への対応づけ、取り込み実行は未実装です。**
現時点ではテンプレート由来の最小 API クライアントと接続確認 CLI だけを含みます。
設計は [アーキテクチャ方針](_documents/アーキテクチャ方針.md) を参照してください。
管理画面・スケジューラ・Docker 化は今回の対象外です。実機の Pleasanter での動作は未確認です。

## 使い方

接続確認の CLI を動かすには、接続先を環境変数で渡します。

```bash
export PLEASANTER_BASE_URL=https://pleasanter.example.com
export PLEASANTER_API_KEY='<環境で管理する秘密値>'
dotnet run --project src/VehicleVision.PleasanterTools.MailIngest.Cli -- check
```

この CLI はメールを取り込みません。現行 CLI の秘密設定は環境変数のみで、`_FILE` は未対応です。
開発・検証手順は [開発ガイド](_documents/開発ガイド.md)、
運用規約は [開発運用](_documents/開発運用.md) にあります。

## ライセンス

**AGPL-3.0-or-later と商用のデュアルライセンス**です。
選択条件は [LICENSING.md](LICENSING.md)、商用条件は [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md)、
依存のライセンスは [NOTICE](NOTICE) を参照してください。
貢献には [CLA](CLA.md) の確認が必要です（[貢献手順](CONTRIBUTING.md)）。
