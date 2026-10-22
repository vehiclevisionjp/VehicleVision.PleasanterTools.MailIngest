<#
.SYNOPSIS
    .github/repo-settings.json を GitHub リポジトリの設定へ反映する。
.DESCRIPTION
    gh CLI でログイン済みで、リポジトリの管理者権限があること。全リポジトリで同じ設定にする（2026-10-20 に統一した）。
      - マージ後にブランチを削除する・自動マージを許可する・ブランチ更新のボタンを出す
      - マージ方式は squash と merge commit だけ（rebase は使わない）
      - Wiki・Projects・Discussions は使わない（内容は _documents に置く）
      - 依存グラフと Dependabot アラートを有効にする（dependency-review に要る）
      - public のリポジトリでは、シークレットスキャン・プッシュ保護を有効にする（Dependabot のセキュリティ更新は使わない）
        （internal / private は GitHub Advanced Security が要るので対象外）
.PARAMETER Repository
    owner/name。省略時は現在のリポジトリ。
#>
param(
    [string]$Repository = ''
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $Repository) {
    $Repository = gh repo view --json nameWithOwner --jq .nameWithOwner
    if ($LASTEXITCODE -ne 0) { throw "リポジトリを特定できません。-Repository を指定してください。" }
}

$settings = Get-Content -LiteralPath (Join-Path $repoRoot '.github/repo-settings.json') -Raw -Encoding utf8 | ConvertFrom-Json
$settings.repository | ConvertTo-Json | gh api --method PATCH "repos/$Repository" --input - | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'リポジトリの設定を反映できません。' }
Write-Output "リポジトリの設定を反映しました: $Repository"

# 依存グラフと Dependabot アラート。**無いと「入ってくる依存を見る」（dependency-review）が
# 「Dependency graph が有効でない」で失敗する**（新しいリポジトリで起きた）
gh api --method PUT "repos/$Repository/vulnerability-alerts" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warning "Dependabot アラートを有効にできませんでした: $Repository" }

$visibility = gh repo view $Repository --json visibility --jq .visibility
if ($LASTEXITCODE -ne 0) { throw '公開範囲を取得できません。' }
if ($visibility -ne 'PUBLIC') {
    Write-Output "公開範囲が $visibility のため、セキュリティ機能は反映しません（GitHub Advanced Security が要る）: $Repository"
    return
}

$security = [ordered]@{
    security_and_analysis = [ordered]@{
        secret_scanning                 = [ordered]@{ status = $settings.public_security.secret_scanning }
        secret_scanning_push_protection = [ordered]@{ status = $settings.public_security.secret_scanning_push_protection }
    }
}
$security | ConvertTo-Json -Depth 5 | gh api --method PATCH "repos/$Repository" --input - | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'セキュリティ機能を反映できません。' }
if ($settings.public_security.automated_security_fixes) {
    gh api --method PUT "repos/$Repository/automated-security-fixes" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Dependabot のセキュリティ更新を有効にできません。' }
} else {
    # 使わない設定（2026-10-21 に全リポジトリで無効にした）。無効のときの 404 は無視する
    gh api --method DELETE "repos/$Repository/automated-security-fixes" 2>$null | Out-Null
}
Write-Output "セキュリティ機能を反映しました: $Repository"
