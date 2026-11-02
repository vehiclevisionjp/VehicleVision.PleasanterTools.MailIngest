# VehicleVision.PleasanterTools.MailIngest

POP3 と IMAP でメールを受信し、差出人・件名・本文・日時などを Pleasanter の列へ対応づけ、
標準 Web API でレコードを作成・更新するためのツールです。C#／.NET 10 の独立したアプリとして開発します。

## 現在の状態

**設計段階から、外部通信を伴わないロジック層の実装へ進んだところです。メール受信（POP3 / IMAP）、状態の保存、取り込み実行は未実装です。**
実装済み（単体試験のみ）: 設定 JSON の読込み・検証（`validate` コマンド）、
正規化メールの列への対応づけと API レコードの組立、重複排除の外部キー・ハッシュ、標準 API クライアントの応答検証。
未実装: メールの受信・MIME 解析、状態の保存、結果不明の照合、受信後の既読・移動・削除、取り込み実行コマンド（`run`）。
受信と状態の保存は、設計文書の未決事項（状態の所在・認証・証明書例外など）の決定待ちです。
設計は [アーキテクチャ方針](_documents/アーキテクチャ方針.md) を参照してください。
管理画面・スケジューラ・Docker 化は今回の対象外です。
**実機の Pleasanter での動作は未確認**で、API 応答の形はフェイクの HTTP でだけ試験しています。

メール接続の設計は `Tls`（暗黙 TLS、既定）・`StartTls`・`None`（平文）の 3 モードです。
TLS は必須にせず、平文 POP3（110）・IMAP（143）も明示設定で許可します。
平文は認証情報・本文の盗聴リスクがあるため、信頼できるネットワーク内だけで使用し、
起動時・接続時に警告する設計です。詳細は [設定例](_documents/メール対応づけ設定.md) を参照してください。
設定の検査と、平文・証明書緩和の警告コードは `validate` に実装済みです。実際の接続時の警告・検証は未実装です。

## 使い方

接続確認の CLI を動かすには、接続先を環境変数で渡します。

```bash
export PLEASANTER_BASE_URL=https://pleasanter.example.com
export PLEASANTER_API_KEY='<環境で管理する秘密値>'
dotnet run --project src/VehicleVision.PleasanterTools.MailIngest.Cli -- check
```

設定ファイルの検査は次のとおりです（接続せず、秘密の値も読みません）。

```bash
dotnet run --project src/VehicleVision.PleasanterTools.MailIngest.Cli -- validate --config <設定ファイル.json>
```

この CLI はメールを取り込みません。`check` の秘密設定は環境変数のみで、`_FILE` は未対応です
（`validate` は `NAME` と `NAME_FILE` の同時指定を検査します）。
開発・検証手順は [開発ガイド](_documents/開発ガイド.md)、
運用規約は [開発運用](_documents/開発運用.md) にあります。

## ライセンス

**AGPL-3.0-or-later と商用のデュアルライセンス**です。
選択条件は [LICENSING.md](LICENSING.md)、商用条件は [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md)、
依存のライセンスは [NOTICE](NOTICE) を参照してください。
貢献には [CLA](CLA.md) の確認が必要です（[貢献手順](CONTRIBUTING.md)）。
