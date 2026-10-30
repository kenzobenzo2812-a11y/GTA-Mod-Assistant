<#
.SYNOPSIS
    Increment the version, commit the changes, and push to the configured Git remote.
    Run `.\scripts\ship-update.ps1` with NO arguments needed:
      - Automatically increments the version number (v1.2.0 -> v1.2.1 -> v1.2.2 -> v1.2.3...)
      - Updates Directory.Build.props so the app's green header badge shows the new version
      - Auto-generates a commit summary from changed files if you don't type a note
      - Commits, tags, and pushes to the configured GitHub source repository
#>

param(
    [string]$Note = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $RepoRoot

try {
    # 1. Read current version from Directory.Build.props and auto-increment patch +1
    $PropsPath = Join-Path $RepoRoot "Directory.Build.props"
    [xml]$Xml = Get-Content $PropsPath
    $CurrentVerStr = $Xml.Project.PropertyGroup[0].Version
    if ([string]::IsNullOrWhiteSpace($CurrentVerStr)) { $CurrentVerStr = "1.2.0" }

    $Parts = $CurrentVerStr.Split('.')
    $Major = [int]$Parts[0]
    $Minor = [int]$Parts[1]
    $Patch = if ($Parts.Length -ge 3) { [int]$Parts[2] + 1 } else { 1 }
    $NextVer = "$Major.$Minor.$Patch"
    $NextTag = "v$NextVer"

    $Xml.Project.PropertyGroup[0].Version = $NextVer
    $Xml.Save($PropsPath)

    # 2. Auto-name the update from modified files if user didn't provide a note
    git add .
    if ([string]::IsNullOrWhiteSpace($Note)) {
        $ChangedFiles = (git diff --cached --name-only | ForEach-Object { Split-Path $_ -Leaf } | Select-Object -First 4) -join ", "
        if ([string]::IsNullOrWhiteSpace($ChangedFiles)) { $ChangedFiles = "maintenance update" }
        $Note = "Auto-save $NextTag ($ChangedFiles)"
    }

    git commit -m "$NextTag - $Note"
    git tag -a $NextTag -m $Note
    git push origin main --follow-tags

    Write-Host "`n[SAVED] Version $NextTag committed and tagged in the configured source repo!" -ForegroundColor Green
    Write-Host "  Note: $Note" -ForegroundColor Cyan
    Write-Host "  To view or roll back to this exact code anytime later: git checkout $NextTag" -ForegroundColor DarkGray
}
finally {
    Pop-Location
}
