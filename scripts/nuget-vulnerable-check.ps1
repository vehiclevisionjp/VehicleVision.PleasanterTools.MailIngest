param([string]$OutputPath = 'vulnerable.json')
$ErrorActionPreference = 'Stop'
dotnet restore VehicleVision.PleasanterTools.Template.slnx --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }
$json = dotnet list VehicleVision.PleasanterTools.Template.slnx package --vulnerable --include-transitive --format json
if ($LASTEXITCODE -ne 0) { throw 'Package audit failed.' }
$text = $json -join "`n"
[IO.File]::WriteAllText((Join-Path (Get-Location) $OutputPath), $text)
$report = $text | ConvertFrom-Json
if ($report.problems) { throw 'Package audit reported a problem.' }
$found = @()
foreach ($project in $report.projects) {
    foreach ($framework in $project.frameworks) {
        foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
            foreach ($vulnerability in $package.vulnerabilities) {
                $found += "$($package.id): $($vulnerability.severity) $($vulnerability.advisoryurl)"
            }
        }
    }
}
if ($found.Count -gt 0) {
    $found | Write-Output
    throw 'Vulnerable packages found.'
}
Write-Output 'No known package vulnerabilities found.'
