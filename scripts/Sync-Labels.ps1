<#
.SYNOPSIS
    .github/labels.json のラベルを GitHub リポジトリへ作成・更新する。
.DESCRIPTION
    gh CLI でログイン済みであること。既存のラベルは色と説明を上書きし、削除はしない。
.PARAMETER Repository
    owner/name。省略時は現在のリポジトリ。
#>
param([string]$Repository = '')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$labels = Get-Content -LiteralPath (Join-Path $repoRoot '.github/labels.json') -Raw -Encoding utf8 | ConvertFrom-Json
$repoArguments = if ($Repository) { @('--repo', $Repository) } else { @() }
foreach ($label in $labels) {
    gh label create $label.name --color $label.color --description $label.description --force @repoArguments
    if ($LASTEXITCODE -ne 0) { throw "ラベルを作成できません: $($label.name)" }
}
Write-Output "ラベルを $($labels.Count) 件反映しました。"
