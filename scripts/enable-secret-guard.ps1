# Enables the repository's local pre-commit and pre-push secret guards on Windows
$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
git -C $RepoRoot config core.hooksPath .githooks
Write-Host "Git secret guards (.githooks/pre-commit & .githooks/pre-push) enabled for $RepoRoot" -ForegroundColor Green
