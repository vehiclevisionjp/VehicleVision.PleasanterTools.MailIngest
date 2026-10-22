# ライセンスについて（デュアルライセンス）

対象ソフトウェアは **VehicleVision.PleasanterTools.MailIngest**。
Copyright (C) PMC Co.,Ltd.

利用者は次のいずれかを選択できます。

| 選択 | ライセンス | 条件 |
| --- | --- | --- |
| A | AGPL-3.0-or-later | [LICENSE](LICENSE) の条件に従う |
| B | PMC 商用ライセンス | [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md) と個別契約に従う |

SPDX 表記: `AGPL-3.0-or-later OR LicenseRef-PMC-Commercial`。

## AGPL を選ぶ場合

追加契約なしに利用・改変・再配布できますが、AGPL の義務に従う必要があります。
改変版をネットワーク経由で利用させる場合のソース提供義務（第 13 条）も確認してください。
本ツールはメール受信と API 呼び出しを行う独立アプリであり、利用形態ごとの判断が必要です。

## 商用ライセンスを選ぶ場合

AGPL のコピーレフト義務によらない利用、製品への組み込み、保守等は個別契約で定めます。
本書だけで商用利用の権利を許諾するものではありません。

## Pleasanter と依存 OSS

Pleasanter とは標準 Web API 越しに通信し、DB へ直接接続しません。
本体コードはコピー・流用・リンク・プロジェクト参照しません。
[参照専用サブモジュール](_reference/README.md) はビルド・配布物に含めません。
本体を取り込むと、当社が権利を持つ部分だけを商用提供する前提が崩れます。

依存は寛容ライセンス（MIT / BSD 系 / Apache-2.0）に限定し、各条件を別途守ります。
一覧は [NOTICE](NOTICE) を参照してください。外部貢献は [CLA](CLA.md) と
[貢献手順](CONTRIBUTING.md) に従って受け取ります。
