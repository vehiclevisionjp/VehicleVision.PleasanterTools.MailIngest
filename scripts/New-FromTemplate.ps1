<#
.SYNOPSIS
    テンプレートから新しいツールのリポジトリ名へ置換する。
.DESCRIPTION
    テンプレートの名前（VehicleVision.PleasanterTools.Template）を、指定した名前へ置き換える。
    ファイルの中身、ファイル名、フォルダ名が対象。置換後に packages.lock.json を作り直す。
    Git 管理外の複製（GitHub の Use this template で作ったリポジトリを clone した直後）で実行する。
.PARAMETER Name
    新しい名前。例: VehicleVision.PleasanterTools.IndexCreator
.PARAMETER Organization
    GitHub の組織名。RepositoryUrl に使う。既定は vehiclevisionjp。
.PARAMETER KeepScript
    置換後もこのスクリプトを残す。既定では削除する。
#>
param(
    [Parameter(Mandatory)][string]$Name,
    [string]$Organization = 'vehiclevisionjp',
    [switch]$KeepScript
)
$ErrorActionPreference = 'Stop'

$old = 'VehicleVision.PleasanterTools.Template'
if ($Name -notmatch '^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)+$') {
    throw 'Name は Company.Product.Tool のようにドットで区切った英数字にしてください。'
}
if ($Name -eq $old) { throw 'テンプレートと同じ名前は指定できません。' }

$repoRoot = Split-Path $PSScriptRoot -Parent
$self = $PSCommandPath
# リポジトリ直下からの相対パスで判定する。絶対パスだと、置き場所の親フォルダ名（temp など）に引っかかる。
$excludedDirs = '(^|[/\\])(\.git|_reference|node_modules|bin|obj|artifacts|TestResults|temp)([/\\]|$)'
function Get-RelativePath([string]$Path) { [IO.Path]::GetRelativePath($repoRoot, $Path) }
$textExtensions = '.cs', '.csproj', '.props', '.slnx', '.json', '.md', '.yml', '.yaml', '.ps1', '.sh', '.mjs', '.editorconfig', '.gitattributes', '.gitignore', '.gitmodules'

$files = Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Force | Where-Object {
    (Get-RelativePath $_.FullName) -notmatch $excludedDirs -and $_.FullName -ne $self -and
    $_.Name -ne 'packages.lock.json' -and $_.Name -ne 'package-lock.json' -and
    ($_.Extension -in $textExtensions -or $_.Name -in $textExtensions -or $_.Name -in 'NOTICE', 'LICENSE')
}

# 1. 中身。LICENSE は AGPL の本文なので触らない。
$utf8 = [System.Text.UTF8Encoding]::new($false)
$npmName = ($Name -replace '\.', '-').ToLowerInvariant()
foreach ($file in $files) {
    if ($file.Name -eq 'LICENSE') { continue }
    $text = [IO.File]::ReadAllText($file.FullName)
    $updated = $text.Replace("vehiclevisionjp/$old", "$Organization/$Name").Replace($old, $Name)
    if ($file.Name -eq 'package.json') {
        $updated = $updated.Replace('vehiclevision-pleasantertools-template', $npmName)
    }
    if ($updated -ne $text) { [IO.File]::WriteAllText($file.FullName, $updated, $utf8) }
}

# 2. 名前。深い階層から順に変えて、親の改名で子のパスが無効にならないようにする。
Get-ChildItem -LiteralPath $repoRoot -Recurse -Force |
    Where-Object { (Get-RelativePath $_.FullName) -notmatch $excludedDirs -and $_.Name -like "*$old*" } |
    Sort-Object { $_.FullName.Length } -Descending |
    ForEach-Object { Rename-Item -LiteralPath $_.FullName -NewName $_.Name.Replace($old, $Name) }

# 3. ロックファイルを作り直す（プロジェクト名がロックへ入るため）。
Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Filter packages.lock.json |
    Where-Object { (Get-RelativePath $_.FullName) -notmatch $excludedDirs } | Remove-Item
Push-Location $repoRoot
try {
    dotnet restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore に失敗しました。' }
    npm install --package-lock-only --ignore-scripts
    if ($LASTEXITCODE -ne 0) { throw 'npm install に失敗しました。' }
    bash scripts/sync-agents.sh
    if ($LASTEXITCODE -ne 0) { throw 'AGENTS.md の同期に失敗しました。' }
}
finally { Pop-Location }

if (-not $KeepScript) { Remove-Item -LiteralPath $self }

Write-Output "置換しました: $old -> $Name"
Write-Output '次にやること: README.md の説明と NOTICE の依存を実態に合わせる。_documents/アーキテクチャ方針.md を書く。'
