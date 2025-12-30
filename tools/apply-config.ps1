param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Host", "Client")]
    [string]$Mode,
    [UInt64]$HostSteamId = 0,
    [string]$GameDir = "C:\\Games\\My.Winter.Car\\game"
)

$templatesDir = Join-Path $PSScriptRoot "config-templates"
$templatePath = Join-Path $templatesDir ($Mode.ToLower() + ".cfg")
if (!(Test-Path $templatePath))
{
    throw "Template not found: $templatePath"
}

$configDir = Join-Path $GameDir "BepInEx\\config"
$configPath = Join-Path $configDir "com.tudor.mywintercarmpmod.cfg"
New-Item -ItemType Directory -Force -Path $configDir | Out-Null
Copy-Item -Path $templatePath -Destination $configPath -Force

if ($Mode -eq "Client" -and $HostSteamId -ne 0)
{
    (Get-Content $configPath) -replace '^SpectatorHostSteamId\\s*=.*$', "SpectatorHostSteamId = $HostSteamId" |
        Set-Content -Path $configPath -Encoding ASCII
}

Write-Host "Applied $Mode config to $configPath"
