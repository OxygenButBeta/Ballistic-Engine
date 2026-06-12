# Runs the Black Hollow menu in the standalone player. Temporarily points the project's startup scene
# at BlackHollowMenu.scene, launches the player, and restores your manifest when the player closes.
#
#   Usage:  pwsh ./run-blackhollow.ps1   (or right-click > Run with PowerShell)
#
# In the player: PRESS ANY KEY to enter the menu, then W/S + Enter to navigate, Esc to go back,
# Q/E to switch Options tabs. (Keyboard nav needs play mode, which the standalone player is.)

$ErrorActionPreference = "Stop"
$proj   = Join-Path $PSScriptRoot "SampleProject\project.json"
$menu   = "Assets/UI Porting/BlackHollow/BlackHollowMenu.scene"
$backup = Get-Content $proj -Raw

$menuManifest = @"
{
  "version": 1,
  "name": "SampleProject",
  "startupScene": "$menu",
  "scenesInBuild": [
    "$menu"
  ]
}
"@

Set-Content $proj $menuManifest -Encoding utf8
try {
    dotnet run --project (Join-Path $PSScriptRoot "BallisticEngine.Runtime") -- (Join-Path $PSScriptRoot "SampleProject")
}
finally {
    Set-Content $proj $backup -Encoding utf8
    Write-Host "Restored project.json to its previous state."
}
