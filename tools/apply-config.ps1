param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Host", "Client")]
    [string]$Mode,
    [UInt64]$HostSteamId = 0,
    [string]$GameDir = "C:\\Games\\My.Winter.Car\\game",
    [string]$ConfigSuffix = ""
)

$templatesDir = Join-Path $PSScriptRoot "config-templates"
$templatePath = Join-Path $templatesDir ($Mode.ToLower() + ".cfg")
if (!(Test-Path $templatePath))
{
    throw "Template not found: $templatePath"
}

$configDir = Join-Path $GameDir "BepInEx\\config"
$configFile = "com.tudor.mywintercarmpmod.cfg"
if ($ConfigSuffix)
{
    $configFile = "com.tudor.mywintercarmpmod.$ConfigSuffix.cfg"
}
$configPath = Join-Path $configDir $configFile
New-Item -ItemType Directory -Force -Path $configDir | Out-Null
Copy-Item -Path $templatePath -Destination $configPath -Force

# Force correct mode in the rendered config to avoid Spectator fallback.
(Get-Content $configPath) `
    -replace '^Mode\\s*=.*$', "Mode = $Mode" `
    -replace '^Transport\\s*=.*$', "Transport = TcpLan" |
    Set-Content -Path $configPath -Encoding ASCII

if ($Mode -eq "Client" -and $HostSteamId -ne 0)
{
    (Get-Content $configPath) -replace '^SpectatorHostSteamId\\s*=.*$', "SpectatorHostSteamId = $HostSteamId" |
        Set-Content -Path $configPath -Encoding ASCII
}

Write-Host "Applied $Mode config to $configPath"
