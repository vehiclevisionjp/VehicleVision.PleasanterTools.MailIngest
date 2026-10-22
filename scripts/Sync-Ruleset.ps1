<#
.SYNOPSIS
    .github/rulesets/*.json を GitHub リポジトリのルールセットへ作成・更新する。
.DESCRIPTION
    gh CLI でログイン済みで、リポジトリの管理者権限があること。同名のルールセットがあれば更新する。
    定義は 3 つに分かれている（全リポジトリで同じ形。2026-10-20 に統一した）。
      - develop-protection      develop。削除と強制 push を禁止し、必須チェックを要求する
      - master-protection       master。上に加えて PR を要求する
      - release-tag-protection  タグ v*。削除と強制 push を禁止する
    リポジトリの管理者は迂回できる（初期配置と緊急対応のため）。
    旧い 1 つにまとめた形（「保護ブランチ」）が残っていれば、新しい 3 つを作ってから消す。
.PARAMETER Repository
    owner/name。省略時は現在のリポジトリ。
.PARAMETER IncludeCodeQL
    develop と master の必須チェックへ CodeQL を加える。public か、GHAS を有効にして CODEQL_ENABLED=true にしたリポジトリで使う。
.PARAMETER ExtraChecks
    develop と master の必須チェックへ足す名前（例: 'integration'）。そのリポジトリの CI のジョブ名に合わせる。
#>
param(
    [string]$Repository = '',
    [switch]$IncludeCodeQL,
    [string[]]$ExtraChecks = @()
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $Repository) {
    $Repository = gh repo view --json nameWithOwner --jq .nameWithOwner
    if ($LASTEXITCODE -ne 0) { throw "リポジトリを特定できません。-Repository を指定してください。" }
}

$extra = @($ExtraChecks)
if ($IncludeCodeQL) { $extra += 'Analyze (csharp)' }

foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repoRoot '.github/rulesets') -Filter '*.json' | Sort-Object Name) {
    $ruleset = Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8 | ConvertFrom-Json
    $statusRule = $ruleset.rules | Where-Object type -eq 'required_status_checks'
    if ($statusRule -and $extra.Count -gt 0) {
        $checks = @($statusRule.parameters.required_status_checks)
        foreach ($name in $extra) {
            if (-not ($checks | Where-Object context -eq $name)) {
                $checks += [pscustomobject]@{ context = $name }
            }
        }
        $statusRule.parameters.required_status_checks = $checks
    }
    $body = $ruleset | ConvertTo-Json -Depth 10

    $existing = gh api "repos/$Repository/rulesets" --jq ".[] | select(.name == `"$($ruleset.name)`") | .id"
    if ($LASTEXITCODE -ne 0) { throw 'ルールセットを取得できません。' }
    if ($existing) {
        $body | gh api --method PUT "repos/$Repository/rulesets/$existing" --input - | Out-Null
        $action = '更新'
    }
    else {
        $body | gh api --method POST "repos/$Repository/rulesets" --input - | Out-Null
        $action = '作成'
    }
    if ($LASTEXITCODE -ne 0) { throw "ルールセットを反映できません: $($ruleset.name)" }
    Write-Output "ルールセットを${action}しました: $Repository / $($ruleset.name)"
}

# 旧い形（1 つにまとめた「保護ブランチ」）は、新しい 3 つを作ったあとで消す（保護が切れる時間を作らない）
$legacy = gh api "repos/$Repository/rulesets" --jq '.[] | select(.name == "保護ブランチ") | .id'
if ($LASTEXITCODE -ne 0) { throw 'ルールセットを取得できません。' }
foreach ($id in @($legacy)) {
    if ($id) {
        gh api --method DELETE "repos/$Repository/rulesets/$id" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "旧いルールセットを消せません: $id" }
        Write-Output "旧いルールセット（保護ブランチ）を消しました: $Repository"
    }
}
