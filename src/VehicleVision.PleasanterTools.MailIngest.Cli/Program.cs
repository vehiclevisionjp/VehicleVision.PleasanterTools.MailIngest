using VehicleVision.PleasanterTools.MailIngest.Cli;

// check          : 接続確認（VVPT_PLEASANTER_BASE_URL / VVPT_PLEASANTER_API_KEY または _API_KEY_FILE）。
// validate       : 設定 JSON を検査する（接続しない。秘密の値は読まない）。問題ありは 1、検査を始められない（ファイルが無い・読めない）は 2。
// run            : 取り込みを 1 回実行する（run --config <path>）。API キーは、設定ファイルが指す環境変数名（NAME / NAME_FILE）から読む。
//                  終了コード 0 成功 / 1 処理エラー・照合待ち / 2 起動設定不正 / 3 件数上限で残り / 130 中断。
//                  Ctrl+C は 1 回目で安全に止め、2 回目ですぐ終了する（どちらも 130）。
// migrate / migrate-status : 状態 DB（既定は SQLite の mailingest.db）のマイグレーションを当てる／状態を見る。
//   設定: MAILINGEST_DB_PROVIDER / MAILINGEST_DB_CONNECTIONSTRING / MAILINGEST_DB_ALLOW_INSECURE。
// completion     : シェルの補完スクリプトを出す（bash・zsh・powershell）。
// 全コマンドが --json に対応する（共通の枠。1 行・ASCII）。引数の解析・ヘルプ・--version は Core.Cli。
return await CliApp.RunWithConsoleAsync(args);
