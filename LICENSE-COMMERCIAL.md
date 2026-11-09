# 商用ライセンス（LicenseRef-PMC-Commercial）

対象ソフトウェアは **VehicleVision.PleasanterTools.MailIngest**

Copyright (C) PMC Co., Ltd. IT Solution Div.

本書で「当社」とは、株式会社ピー・エム・シー ITソリューション事業部を指します。

本文書は、[`LICENSING.md`](LICENSING.md) の選択肢 **B（商用ライセンス）** の概要です。
**正式な条件は個別に締結する契約書によります。** 本文書は契約書ではありません。

## 概要

商用ライセンスは、**GNU AGPL v3 の義務を負わずに**本ソフトウェアを利用するための
選択肢です。契約できる相手は法人に限りません。次を想定しています。

- 改変版のソースコードを公開せずに利用する（ネットワーク経由で提供する場合を含む）
- proprietary な製品・システムへ組み込む
- 保守・サポート・SLA を契約に含める

## AGPL 版との違い

| | AGPL v3（選択肢 A） | 商用ライセンス（選択肢 B） |
| --- | --- | --- |
| 費用 | 無償 | 個別契約 |
| ソースコード開示義務 | **あり**（改変して配布またはネットワーク提供する場合を含む） | なし |
| proprietary 製品への組み込み | 不可 | 可 |
| 保守・サポート | なし | 契約による |

## 対象範囲

本リポジトリのうち、**当社が著作権を持つ部分**が対象です。次は対象外です。

- サードパーティの OSS と部品（[`NOTICE`](NOTICE) 参照）。各ライセンスに従ってください
  <!-- license:begin sni-scope=on -->
- Windows 向けの `Microsoft.Data.SqlClient.SNI.runtime` は Microsoft 独自の許諾で、下の「頒布先に守っていただく条件」が付きます
  <!-- license:end sni-scope -->
  <!-- license:begin reference-scope=on -->
- [`_reference/`](_reference/README.md) 配下（Pleasanter 本体。AGPL v3）。
  **参照専用であり、本ソフトウェアの一部ではありません**
  <!-- license:end reference-scope -->

<!-- license:begin core-terms=on -->
## 共通部品 Core との関係

本ソフトウェアは、当社の共通部品 Core（`VehicleVision.PleasanterTools.Core.*` の NuGet パッケージ）を利用します。
Core は AGPL-3.0-or-later と商用ライセンスのデュアルライセンスで、著作権者は当社です
（[`LICENSING.md`](LICENSING.md) の依存の節を参照）。
**本ソフトウェアの商用ライセンスは、本ソフトウェアに組み込まれた Core を含みます。**

- 本ソフトウェアの商用契約を結んだ方には、組み込まれた Core を、本ソフトウェアと同じ条件で提供します
- Core を単体で利用する場合と、VehicleVision.PleasanterTools 以外へ組み込む場合は、個別に契約します
<!-- license:end core-terms -->

<!-- license:begin dotnetclient-terms=on -->
## 共通部品 DotNetClient との関係

本ソフトウェアは、当社の共通部品 DotNetClient（`VehicleVision.PleasanterTools.DotNetClient` の NuGet パッケージ）を利用します。
DotNetClient は LGPL-3.0-or-later・AGPL-3.0-or-later・商用ライセンスのトリプルライセンスで、著作権者は当社です
（[`LICENSING.md`](LICENSING.md) の依存の節を参照）。
**本ソフトウェアの商用ライセンスは、本ソフトウェアに組み込まれた DotNetClient を含みます。**

- 本ソフトウェアの商用契約を結んだ方には、組み込まれた DotNetClient を、本ソフトウェアと同じ条件で提供します
- DotNetClient を単体で利用する場合と、VehicleVision.PleasanterTools 以外へ組み込む場合は、DotNetClient の条件によります
<!-- license:end dotnetclient-terms -->

<!-- license:begin sni-terms=on -->
## 頒布先に守っていただく条件

本ソフトウェアの配置物（SQL Server に接続する Windows 向け）には、Microsoft の
`Microsoft.Data.SqlClient.SNI.runtime`（Microsoft Software License Terms）が含まれます。
この許諾は頒布者に、頒布先へ次の条件への同意を求めることを義務づけています。
**個別の契約書にも、同程度以上の条件を盛り込みます**（出典: パッケージ 7.1.0 内の `LICENSE.txt`、参照日 2026-10-27）。

- 当該部品を本ソフトウェアの一部としてのみ利用し、単体で共有・公開・貸与・再配布しないこと
- 当該部品を逆コンパイル・逆アセンブルせず、Microsoft の表示を削除・変更しないこと
- 輸出管理に関する国内外の法令を遵守すること
- 当該部品が無保証（現状のまま）で、Microsoft の責任が限定されていること（直接損害の 5 米ドルまで）を了解すること
- 当該部品の利用に関する請求から、Microsoft を補償し、防御し、損害を与えないこと
  （改変していない部品だけに基づく請求を除く。頒布者である当社を通じた補償の範囲は、個別の契約で定めます）

本節は契約書ではなく、法的な助言でもありません。
<!-- license:end sni-terms -->

## お問い合わせ

株式会社ピー・エム・シー ITソリューション事業部

お問い合わせは、次のページの連絡先からお願いします: [https://www.vehiclevision.jp/division/](https://www.vehiclevision.jp/division/)
