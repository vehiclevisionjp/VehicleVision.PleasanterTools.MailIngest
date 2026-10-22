# .claude

## settings.json

Claude Code の**リポジトリ共有**設定。権限の許可（`allow`）と禁止（`deny`）のルールを置いている。
**すべてのリポジトリで同じ内容**にしている。差が出たら、どちらかが古い。

- **JSON であってコメントを書けない。** 書くと壊れ、中の許可ルールが黙って効かなくなる。
  説明が必要ならこのファイルへ書くこと
- **このファイルは追跡される。** `.claude/` を丸ごと `.gitignore` へ入れないこと。
  共有設定が各人の手元にしか残らず、置いたつもりで誰にも届かない状態になる
- 個人のローカル上書きは `settings.local.json`。**`.gitignore` に入れておくこと**（コミットされないように）
- 変更したら、JSON としてパースできるか確かめる。新しく作った設定は、そのセッションでは効かないことがある
- `git worktree` で作業するときは、そのブランチにこのファイルがあるかを見る。
  共有設定を入れる前に分岐したブランチには無い

## 許可と禁止の内容

### 許可（allow）

- 読み取り・検証: `git fetch`、`git worktree list / add`、`gh pr / issue / run / workflow / repo / label` の参照系、
  `npx markdownlint-cli2`、`bash -n`
- 作業の記録: `git add`、`git commit`（ローカルで取り消せる）
- ビルド・検査: `dotnet build / restore / test / pack / format / list / new / sln / add`、`npm run build / check / lint / typecheck`
  （`src/*` の Frontend は `npm --prefix src/* run build / check / test`）
- サブエージェント: `copilot`（Copilot CLI。意図した許可）
- 文書の取得: `WebFetch` は `docs.github.com` と `code.claude.com`
- **そのリポジトリに無いコマンド（たとえば dotnet が無いリポジトリの dotnet）の許可も、そのまま入っている。** 害はなく、同じ内容を保つことを優先した

### 禁止（deny）

- 破壊的: `git push --force`・`-f`・`--delete`、`git reset --hard`、`git clean -f`、`git commit --amend`、`rm -rf`、`Remove-Item -Recurse`
- 公開の取り消せない操作: `gh repo delete`、`gh secret *`、`gh variable set / delete`
- 保護ブランチへの直接 push（`main`・`develop`・`master`）。**作業ブランチ → PR で入れる**

### 書き方と限界

- ルールは `Bash(...)` と `PowerShell(...)` を対にして並べる。`git -C <パス> push` のような形でも deny が当たるよう、`git * push ...` の形も併記している
- ルールは、Claude Code が書く**典型の形**にしか当たらない。**安全の境界ではない。** 本当の防御は、GitHub 側のルールセットと、バイパス設定
