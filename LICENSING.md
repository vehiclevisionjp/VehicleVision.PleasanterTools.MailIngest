# ライセンスについて（デュアルライセンス）

<!-- license:begin release=unreleased -->
> 本ソフトウェアは未リリースです。
> 最初のリリースは、この提供条件（デュアルライセンス）で出します。
<!-- license:end release -->

対象ソフトウェアは **VehicleVision.PleasanterTools.MailIngest**

Copyright (C) PMC Co., Ltd. IT Solution Div.

本書で「当社」とは、株式会社ピー・エム・シー ITソリューション事業部を指します。

本ソフトウェアは **デュアルライセンス** で提供されます。利用者は以下のいずれかを選択できます。

| | ライセンス | 全文 | 想定する利用 |
| --- | --- | --- | --- |
| A | **GNU AGPL v3 以降**（AGPL-3.0-or-later） | [`LICENSE`](LICENSE) | オープンソースとして利用・改変・再配布する場合 |
| B | **株式会社ピー・エム・シー ITソリューション事業部の商用ライセンス** | [`LICENSE-COMMERCIAL.md`](LICENSE-COMMERCIAL.md) | AGPL の義務を負わずに利用したい場合（個別契約） |

SPDX 表記： `AGPL-3.0-or-later OR LicenseRef-PMC-Commercial`

## どちらを選ぶべきか

### A. AGPL v3 を選ぶ場合

追加の契約や費用は不要です。ただし AGPL の義務が生じます。

- 本ソフトウェアを改変して**配布する場合、その改変版のソースコードを提供する義務**が生じます
- 本ソフトウェアを改変して**ネットワーク経由で他者に提供する場合（第 13 条）、
  その利用者に対して改変版のソースコードを提供する義務**が生じます
- 本ソフトウェアと結合した著作物を配布する場合、その全体を AGPL で提供する必要があります

<!-- license:begin usage=console -->
本ソフトウェアはコンソールアプリで、利用者が自分の環境で実行します。
<!-- license:end usage -->

組織の内部での利用や受託での提供など、義務の有無が分かりにくい場面は確認してください。

### B. 商用ライセンスを選ぶ場合

AGPL の義務（ソースコード開示・コピーレフト）を負わずに利用できます。
条件は個別契約で定めます。契約できる相手は法人に限りません。次のような場合はこちらを検討してください。

- 改変版のソースコードを公開したくない（ネットワーク経由で提供する場合を含む）
- 自社の proprietary な製品・システムに組み込みたい
- AGPL の義務範囲について社内の法務判断が難しい
- 保守・サポート・SLA を契約に含めたい

**お問い合わせ：** 株式会社ピー・エム・シー ITソリューション事業部
（連絡先は [`LICENSE-COMMERCIAL.md`](LICENSE-COMMERCIAL.md) を参照）

## Pleasanter との関係（デュアルライセンスの前提）

<!-- license:begin independence=app -->
本ソフトウェアは Pleasanter（株式会社インプリム, AGPL v3）の
**独立した別アプリケーション**です。
<!-- license:end independence -->

- 本体のソースコードを **複製・改変・リンク・プロジェクト参照していません**。Pleasanter のアセンブリも参照しません
- Pleasanter の派生著作物ではありません
  <!-- license:begin connection=api-only -->
- Pleasanter とは **標準 Web API 越しにのみ通信**します。DB へ直接接続しません
  <!-- license:end connection -->
  <!-- license:begin reference=on -->
- [`_reference/Implem.Pleasanter`](_reference/README.md) は**事実確認のための参照専用**で、
  ビルド対象にも配布物にも含めません
  <!-- license:end reference -->

本ソフトウェアは、Pleasanter 本体の AGPL の**汚染を受けておらず、受け入れるつもりもありません。**
そのため、本ソフトウェアのライセンスは Pleasanter のライセンスに拘束されず、
**著作権者である株式会社ピー・エム・シー ITソリューション事業部が自由に決定できます。
これがデュアルライセンスを成立させている前提です。**

> **AGPL 側だけを見て「どうせ AGPL なのだから本体を取り込んでもよい」と考えないこと。**
> Pleasanter の著作権は当社にないため、**取り込んだ瞬間に B（商用ライセンス）を
> 提供できなくなります。** デュアルの片方が壊れます。

<!-- license:begin pleasanter-note=free -->
確認の方法と、本ソフトウェアの実装の範囲です。

- 本ソフトウェアの目的は、POP3 / IMAP のメールを受け取り、Pleasanter の標準 Web API でレコードを作成・更新することです
- `scripts/reference-isolation-check.sh`（CI で実行）が、本体のソースへの参照が無いことを検査します。
  `_reference/` に取得する本体は Git で追跡しません
- 現時点の実装は、Pleasanter の標準 Web API を呼ぶ最小のクライアントと接続確認（`check`）だけです。
  メール受信と取り込み状態の保持は未実装です。設計では、取り込み状態を保持する DB は MailIngest 専用とし、
  Pleasanter の DB とは別にします（[`_documents/アーキテクチャ方針.md`](_documents/アーキテクチャ方針.md)）
<!-- license:end pleasanter-note -->

## 依存ソフトウェアについて

本ソフトウェアが利用する OSS は **寛容ライセンス（MIT / BSD 系 / Apache-2.0）** に
限定します。一覧は [`NOTICE`](NOTICE) を参照してください。依存の宣言（パッケージ参照）は各 csproj にあり、
`packages.lock.json` を置いているリポジトリでは、推移的依存と実際の版もそこで確認できます。

<!-- license:begin deps-note=free -->
当社の共通部品 Core のうち Core.Data（`VehicleVision.PleasanterTools.Core.Data`）を依存に追加しました。
DB の接続とマイグレーションの基盤で、状態の保存に使います。`core` の節と、Core.Data が SqlClient を経由して
SNI.runtime を推移的に持つための `sni` の節を `on` にしています。
MimeKit と MailKit（いずれも MIT）を依存に追加しました。
<!-- license:end deps-note -->

これは意図的な制約です。**コピーレフト系（GPL / AGPL / SSPL 等）の依存が 1 つでも入ると、
商用ライセンスでの提供ができなくなります。** 依存を追加する際は必ずライセンスを確認してください。

<!-- license:begin sni=on -->
### 例外: Microsoft.Data.SqlClient.SNI.runtime

**例外として**、SQL Server に接続する Windows 向けの配置物には、
`Microsoft.Data.SqlClient.SNI.runtime`（ネイティブのライブラリ。Microsoft Software License Terms
「MICROSOFT.DATA.SQLCLIENT.SNI LIBRARY」）が含まれます。MIT・BSD・Apache-2.0 ではない
Microsoft 独自の許諾です。**選択肢 A・B のどちらでも**、この部品には次の条件が付きます
（出典: パッケージ 7.1.0 内の `LICENSE.txt` 3 項（a）・5 項・6 項・11〜12 項、参照日 2026-10-27）。

- 本ソフトウェアの一部としてのみ頒布します。**単体では頒布・再配布しません**
- 頒布先（再頒布する者と、エンドユーザー）は、この許諾と**同程度以上に部品を保護する条件**に同意するものとします。
  特に、逆コンパイル・逆アセンブルの禁止、Microsoft の表示の削除・変更の禁止、
  単体での共有・公開・貸与・提供の禁止、輸出管理法令の遵守が含まれます
- 部品は無保証（現状のまま）で、Microsoft が負う賠償は直接損害の 5 米ドルまでです
- 頒布者は、部品の頒布・利用に関する請求から、Microsoft を補償し、防御し、損害を与えないものとします
  （改変していない部品だけに基づく請求を除く）
- 部品のソースコードを、**開示や改変の自由を条件とするライセンス（AGPL など）の対象にして頒布しません**。
  本ソフトウェアの AGPL は当社が著作権を持つ部分に及び、この部品はバイナリのまま同梱する別の著作物です

**これらは Questionnaire で決めた取り扱い（2026-10-20）と同じ整理です。
上の整理は法的な助言ではありません。**
条件の正本は、パッケージ内の `LICENSE.txt` です。
<!-- license:end sni -->

<!-- license:begin core=on -->
### 例外: 当社の共通部品 Core（AGPL／商用のデュアルライセンス）

本ソフトウェアは、当社の共通部品 Core（`VehicleVision.PleasanterTools.Core.*` の NuGet パッケージ）を利用します。
Core は寛容ライセンスではなく、**AGPL-3.0-or-later と商用ライセンスのデュアルライセンス**で提供されます。
著作権者は本ソフトウェアと同じ、当社です。

- **A（AGPL）を選ぶ場合**: Core も AGPL で利用します
- **B（商用）を選ぶ場合**: 本ソフトウェアの商用ライセンスは、本ソフトウェアに組み込まれた Core を含みます。
  Core を単体で利用する場合と、当社のアプリケーション以外へ組み込む場合は、個別に契約します

Core の条件は、次を参照してください。
[Core の LICENSING.md](https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.Core/blob/develop/LICENSING.md)
<!-- license:end core -->

<!-- license:begin dotnetclient=off -->
<!-- license:end dotnetclient -->

## 貢献を受け取るとき

**デュアルライセンスを保つには、貢献者からの許諾が要る。**
他人の著作物を商用ライセンスで再頒布する権利は、その人から貰わない限り無い。

**署名のない貢献を 1 つでも取り込むと、その部分だけ商用ライセンスで提供できなくなる。**
後から気付いても、当人全員から同意を集め直すか、該当コードを書き直すしかない。

手続きは [`CLA.md`](CLA.md) と [`CONTRIBUTING.md`](CONTRIBUTING.md) を参照してください。
