param([string]$Tag = '', [switch]$DryRun)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
$prefix = $props.SelectSingleNode('//VersionPrefix').InnerText.Trim()
if (!$Tag) {
    if (!$DryRun) { throw '公開するタグを指定してください。' }
    $Tag = "v$prefix"
}
if ($Tag -notmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-([0-9A-Za-z-]+)(\.[0-9A-Za-z-]+)*)?$') {
    throw 'タグは vX.Y.Z または vX.Y.Z-rc.1 の形式にしてください。'
}
$version = $Tag.Substring(1)
if (($version -split '-')[0] -ne $prefix) { throw 'タグと VersionPrefix が一致しません。' }
if ($version.Contains('-')) {
    foreach ($part in (($version -split '-', 2)[1] -split '\.')) {
        if ($part -match '^0[0-9]+$') { throw 'プレリリースの数値識別子に先頭ゼロは使えません。' }
    }
}
if (!$DryRun) {
    $tagCommit = git -C $repoRoot rev-parse --verify "refs/tags/$Tag^{commit}"
    if ($LASTEXITCODE -ne 0) { throw 'タグが存在しません。' }
    $headCommit = git -C $repoRoot rev-parse HEAD
    if ($tagCommit -ne $headCommit) { throw '取得したソースとタグが一致しません。' }
    git -C $repoRoot merge-base --is-ancestor $tagCommit refs/remotes/origin/master
    if ($LASTEXITCODE -ne 0) { throw 'master に含まれるコミットへタグを付けてください。' }
}
$pre = $version.Contains('-').ToString().ToLowerInvariant()
if ($env:GITHUB_OUTPUT) {
    @("tag=$Tag", "version=$version", "prerelease=$pre") | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
}
Write-Output "検証済みのリリース候補: $Tag（dry-run: $DryRun）"
