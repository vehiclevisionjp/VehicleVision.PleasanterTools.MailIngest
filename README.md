# VehicleVision.PleasanterTools.MailIngest

POP3 と IMAP でメールを受信し、差出人・件名・本文・日時などを Pleasanter の列へ対応づけ、
標準 Web API でレコードを作成・更新するためのツールです。C#／.NET 10 の独立したアプリとして開発します。

## 現在の状態

**取り込み実行（`run`）までを実装した段階です。受信後の既読・移動・削除（`leave` 以外）は未実装です。**
実装済み（単体試験と、テスト用サーバ GreenMail に対する統合試験。Pleasanter 実機・実際のメールサーバは未検証）:
設定 JSON の読込み・検証（`validate`）、正規化メールの列への対応づけと API レコードの組立、重複排除の外部キー・ハッシュ、
Pleasanter 標準 API の呼び出し（NuGet の DotNetClient ＋ 応答の検査）、状態 DB（SQLite。`migrate` / `migrate-status`）、
MIME の正規化（MimeKit）、IMAP / POP3 の非破壊の取得（パスワード認証。`Tls` / `StartTls` / `None`、証明書は `Strict` のみ）、
取り込み実行（`run --config`。取得 → 正規化 → 重複排除 → 対応づけ → 既存キー照合 → 作成 → DB 確定、結果不明の照合・保留、終了コード、運用サマリー。メールは `leave` でサーバに残し、既読にも移動にも削除にもしません）。
未実装: 受信後の既読・移動・削除（markSeen・move・delete）、既存レコードの更新（`onExisting` は `skip` のみ）、OAuth2（Microsoft 365・Gmail は現状使えません）、
証明書の例外（`AllowUntrusted` / `Pinned`）、添付の転送。
設計は [アーキテクチャ方針](_documents/アーキテクチャ方針.md) を参照してください。
管理画面・スケジューラ・Docker 化は今回の対象外です。
**実機の Pleasanter での動作は未確認**で、API 応答の形はフェイクの HTTP でだけ試験しています。

メール接続の設計は `Tls`（暗黙 TLS、既定）・`StartTls`・`None`（平文）の 3 モードです。
TLS は必須にせず、平文 POP3（110）・IMAP（143）も明示設定で許可します。
平文は認証情報・本文の盗聴リスクがあるため、信頼できるネットワーク内だけで使用し、
起動時・接続時に警告する設計です。詳細は [設定例](_documents/メール対応づけ設定.md) を参照してください。
設定の検査と、平文・証明書緩和の警告コードは `validate` に実装済みです。接続時の平文の警告は取得の実装（認証の直前）にあり、GreenMail で確認しています。

## 使い方

接続確認の CLI を動かすには、接続先を環境変数で渡します（API キーは `VVPT_PLEASANTER_API_KEY` の代わりに、キーを書いたファイルの絶対パスを `VVPT_PLEASANTER_API_KEY_FILE` で渡せます。両方の指定はできません）。

```bash
export VVPT_PLEASANTER_BASE_URL=https://pleasanter.example.com
export VVPT_PLEASANTER_API_KEY='<環境で管理する秘密値>'
dotnet run --project src/VehicleVision.PleasanterTools.MailIngest.Cli -- check
```

設定ファイルの検査は次のとおりです（接続せず、秘密の値も読みません）。

```bash
dotnet run --project src/VehicleVision.PleasanterTools.MailIngest.Cli -- validate --config <設定ファイル.json>
```

取り込みは、状態 DB を `migrate` で作ってから `run` で実行します。

```bash
dotnet run --project src/VehicleVision.PleasanterTools.MailIngest.Cli -- migrate
dotnet run --project src/VehicleVision.PleasanterTools.MailIngest.Cli -- run --config <設定ファイル.json> [--json]
```

引数は `--config 値`・`--config=値`・`-config 値`・`/config:値` のどれでも書けます（`--help`・`--version`・`completion <shell>` もあります）。
全コマンド（`check`・`validate`・`run`・`migrate`・`migrate-status`）が `--json` に対応し、標準出力へ 1 行の JSON を出します。

秘密（Pleasanter の API キー・メールのユーザー名とパスワード）は、`run` では設定が指す環境変数名の `NAME` か `NAME_FILE` から読みます
（`VVPT_PLEASANTER_*` は `check` だけが読みます）。
終了コードは次のとおりです。

| コード | 意味 |
| --- | --- |
| 0 | 成功。`validate` で問題なし |
| 1 | 処理エラー・照合待ちあり（`run`）。`validate` で問題あり。`migrate-status` で未適用あり |
| 2 | 起動設定・使い方の誤り（何も実行していない）。`validate` では設定ファイルが無い・読めない |
| 3 | `run` が件数上限で未処理が残っている |
| 130 | 中断（Ctrl+C。1 回目で安全に止まり、2 回目ですぐ終了します） |

標準出力は結果（運用サマリーは `[INFO]` 付き。件数だけ。`validate` の指摘も結果なので標準出力）、標準エラーは警告・エラー・構造化ログです。
出力は英語・ASCII です。**添付は Pleasanter へ転送しません。**
**実機のメールサーバ・Pleasanter では未検証です。**

### 環境変数

| 名前 | 用途 |
| --- | --- |
| `VVPT_PLEASANTER_BASE_URL`・`VVPT_PLEASANTER_API_KEY`（または `VVPT_PLEASANTER_API_KEY_FILE`） | `check` の接続先 |
| 設定ファイルが指す名前（例: `MAILINGEST_PLEASANTER_API_KEY`。`NAME` / `NAME_FILE`） | `run` の API キー・メールの資格情報 |
| `MAILINGEST_DB_PROVIDER`・`MAILINGEST_DB_CONNECTIONSTRING`・`MAILINGEST_DB_ALLOW_INSECURE` | 状態 DB |

旧名の `PLEASANTER_BASE_URL`・`PLEASANTER_API_KEY` は受け付けません（読まず、検出も案内もしません）。
旧名だけを設定した環境の `check` は、新名が未設定として終了コード 2 で止まります。`check` を使う環境は、新名へ書き換えてください。
開発・検証手順は [開発ガイド](_documents/開発ガイド.md)、
運用規約は [開発運用](_documents/開発運用.md) にあります。

## ライセンス

**AGPL-3.0-or-later と商用のデュアルライセンス**です。
選択条件は [LICENSING.md](LICENSING.md)、商用条件は [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md)、
依存のライセンスは [NOTICE](NOTICE) を参照してください。
貢献には [CLA](CLA.md) の確認が必要です（[貢献手順](CONTRIBUTING.md)）。
