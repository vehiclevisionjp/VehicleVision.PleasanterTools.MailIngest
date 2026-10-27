# _reference（参考専用・AGPL）

Pleasanter 本体（**GNU AGPL v3**）は、AI（Claude・Copilot・Codex）が
API 仕様・スキーマ・挙動を事実確認するためだけに読む参照ソース。
本リポジトリでは追跡せず、必要なときに上流の `main` の先頭を取得する。
CI は参照ソースを使わず、checkout の `submodules: false` も維持する。

- 上流: <https://github.com/Implem/Implem.Pleasanter>
- 既定の取得先: リポジトリ直下の `_reference/Implem.Pleasanter/`（Git 管理外）
- 共有取得先: 環境変数 `PLEASANTER_REFERENCE_DIR`（その場所を直接読む）
- この `README.md` は引き続き Git で追跡する

## 厳守事項

- **コードをコピー・流用・リンク・プロジェクト参照しない。**
  本アプリは本体を改造せず、**標準 API を叩く別アプリ**として実装する
- ビルド対象・配布物・ソリューションへ含めない
- 取得した本体の作業ツリーを編集しない。本リポジトリへのコミット対象でもない
- 実機未検証の結果を動作確認済みと書かない。設定・権限や API の実際の応答は実機で確認する

> 本アプリは AGPL / 商用のデュアル提供だが、本体を取り込んでよいという意味ではない。
> ネットワーク越しの API 利用だけ、という前提を保つ。本体コードの取り込みは商用提供の前提を崩す。

## 取得と更新

事実確認を始める前に毎回実行する。Git と bash が必要で、Windows では Git Bash を使う。
ビルドや通常のテストのためには取得しない。

```bash
bash scripts/sync-reference.sh
```

worktree 間で共有する場合は PowerShell から次のように指定できる（絶対パスを推奨）。

```powershell
$env:PLEASANTER_REFERENCE_DIR = 'D:\references\Implem.Pleasanter'
bash scripts/sync-reference.sh
```

未取得なら `main` を深さ 1 で clone し、既存なら fetch 後に detach checkout する。
終了時に Path・短い SHA・コミット日時・Status・完全な FullSHA・取得日時（UTC）・csproj の Version を出す。
Version が読めない場合は警告と `Version=unknown` を出すため、版を推測しない。

変更済み・ステージ済み・未追跡ファイルがある作業ツリー、origin が異なるリポジトリ、
非空の取得先では停止する。通信失敗は失敗コードで終了し、既存の HEAD とファイルを維持する。
新規 clone は一時取得先で成功してから配置し、並行取得はロックで止める。
異常終了で取得先に対応する `.sync-lock` が残ったら、同期が実行中でないことを確認してから空のロックを除く。

共有先を指定しても、既定先にジャンクションやシンボリックリンクは作らない。
相対パスはリポジトリ直下が基準。共有先を更新する間は、ほかの AI が読まないよう調整する。
参照取得に bot の App は使わない。

MailIngest は一覧ファイルなしの本体 1 件を使う。
配布した共通スクリプトの `--manifest`・`--root` は一覧があるリポジトリ向けで、
この移行では `references.json` を追加しない。

## 事実の記録と再現性

日次更新 workflow と gitlink による追跡を廃止し、文書に残す確認済み SHA を再現性の根拠にする。
完全なコミット SHA・該当ソースへの固定リンク・参照日を併記する。
過去の記述を、必要時に取得した最新ソースの確認結果として扱わない。
`main` はリリース前の変更を含むため、Version やリリースタグとの対応も個別に確認する。

[設計の参照資料](../_documents/設計の参照資料.md#5-pleasanter-参照ソースでの事実確認) は、
2026-10-22 に確認した `626a173ec82e32f7055f3e76970a145b79c2296f` の記録。
[当時のソース][pleasanter-source] の読取りに基づくもので、実機や最新の版の再検証は未実施。

## 確認の範囲

`Implem.Pleasanter/` 内の API Controller・ApiModel・Context・View 等を読むことで、
標準 API のルート・項目・キーの受け渡し・検索指定を調査できる。
実際の JSON の日時書式・タイムゾーン・未設定値・ページ上限や、実運用の設定・権限は実機で確認する。

[pleasanter-source]: https://github.com/Implem/Implem.Pleasanter/tree/626a173ec82e32f7055f3e76970a145b79c2296f/Implem.Pleasanter
