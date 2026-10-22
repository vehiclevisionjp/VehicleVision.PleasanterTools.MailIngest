param([string]$Version = '')
# Windows と Linux 共通の .NET 10 ランタイム依存配布物を生成する。
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$packageRoot = Join-Path $repoRoot ('artifacts/package-' + [Guid]::NewGuid().ToString('N'))
$appDirectory = Join-Path $packageRoot 'app'
$project = Join-Path $repoRoot 'src/VehicleVision.PleasanterTools.Template.Cli/VehicleVision.PleasanterTools.Template.Cli.csproj'
$versionArguments = @()
[xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
$zipVersion = if ($Version) { $Version } else { $props.SelectSingleNode('//VersionPrefix').InnerText.Trim() }
$zipName = "VehicleVision.PleasanterTools.Template-$zipVersion-portable.zip"
if ($Version) { $versionArguments = @("-p:Version=$Version") }
dotnet publish $project -c Release --no-restore --no-self-contained -p:UseAppHost=false -p:DebugType=None -p:DebugSymbols=false -o $appDirectory @versionArguments
if ($LASTEXITCODE -ne 0) { throw '配布物の生成に失敗しました。' }
foreach ($file in @('LICENSE', 'NOTICE', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination $appDirectory
}
Copy-Item -LiteralPath (Join-Path $repoRoot '_documents') -Destination $appDirectory -Recurse
$forbidden = Get-ChildItem -LiteralPath $appDirectory -Recurse -File | Where-Object {
    $_.Name -in @('appsettings.Local.json', '.env') -or
    $_.Extension -in @('.pfx', '.pem', '.key', '.pdb') -or $_.FullName -match '[/\\]_reference[/\\]'
}
if ($forbidden) { throw '配布対象外の設定・秘密・参照ソース・pdb が検出されました。' }
$commit = git -C $repoRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'ソースのコミットを確認できません。' }
Set-Content -LiteralPath (Join-Path $appDirectory 'BUILD.txt') -Value @(
    "Commit: $commit", "Source: https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.Template/tree/$commit"
) -Encoding utf8
$zipPath = Join-Path $packageRoot $zipName
[System.IO.Compression.ZipFile]::CreateFromDirectory($appDirectory, $zipPath)
$checksumPath = "$zipPath.sha256"
$checksum = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $checksumPath -Value "$checksum  $zipName" -Encoding utf8NoBOM
if ($env:GITHUB_OUTPUT) { Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value @("directory=$appDirectory", "zip=$zipPath", "checksum=$checksumPath") -Encoding utf8 }
Write-Output "配布 ZIP: $zipPath"
