param(
    [string]$GameDir = "C:\\Games\\My.Winter.Car\\game",
    [string]$HostGameDir = "",
    [string]$ClientGameDir = "",
    [UInt64]$HostSteamId = 0,
    [int]$DelaySeconds = 5,
    [string]$HostConfigSuffix = "host",
    [string]$ClientConfigSuffix = "client",
    [string]$HostProductName = "My Winter Car Host",
    [string]$ClientProductName = "My Winter Car Client",
    [string]$OriginalProductName = "My Winter Car",
    [switch]$UseProductNameOverride
)

$resolvedHostGameDir = if ($HostGameDir) { $HostGameDir } else { $GameDir }
$resolvedClientGameDir = if ($ClientGameDir) { $ClientGameDir } else { $GameDir }

$hostExePath = Join-Path $resolvedHostGameDir "mywintercar.exe"
if (!(Test-Path $hostExePath))
{
    throw "Host executable not found: $hostExePath"
}

$clientExePath = Join-Path $resolvedClientGameDir "mywintercar.exe"
if (!(Test-Path $clientExePath))
{
    throw "Client executable not found: $clientExePath"
}

$applyScript = Join-Path $PSScriptRoot "apply-config.ps1"
if (!(Test-Path $applyScript))
{
    throw "Config script not found: $applyScript"
}

$dataDir = Join-Path $resolvedHostGameDir "MyWinterCar_Data"
$patcherProj = Join-Path $PSScriptRoot "UnityPlayerSettingsPatcher\\UnityPlayerSettingsPatcher.csproj"

function Set-ProductName([string]$Name)
{
    if (-not $UseProductNameOverride -or [string]::IsNullOrWhiteSpace($Name))
    {
        return
    }

    if (!(Test-Path $patcherProj))
    {
        Write-Warning "PlayerSettings patcher not found: $patcherProj"
        return
    }

    if (!(Test-Path $dataDir))
    {
        Write-Warning "MyWinterCar_Data not found: $dataDir"
        return
    }

    $env:MWC_PRODUCT_NAME = $Name
    & dotnet run --project $patcherProj -- $dataDir | Out-Null
    if ($LASTEXITCODE -ne 0)
    {
        Write-Warning "PlayerSettings patch failed for product name '$Name'."
    }
    Remove-Item Env:MWC_PRODUCT_NAME -ErrorAction SilentlyContinue
}

Set-ProductName -Name $HostProductName

$hostArgs = @()
if ($HostConfigSuffix)
{
    $hostArgs += "--mwc-config=$HostConfigSuffix"
}

& $applyScript -Mode Host -GameDir $resolvedHostGameDir -ConfigSuffix $HostConfigSuffix | Out-Null
try
{
    $hostProc = Start-Process -FilePath $hostExePath -WorkingDirectory $resolvedHostGameDir -ArgumentList $hostArgs -PassThru -ErrorAction Stop
}
catch
{
    Write-Warning "Host launch failed: $($_.Exception.Message)"
    Write-Warning "Config left in Host mode."
    return
}

Start-Sleep -Seconds $DelaySeconds

if ($resolvedClientGameDir -ne $resolvedHostGameDir)
{
    $dataDir = Join-Path $resolvedClientGameDir "MyWinterCar_Data"
}
Set-ProductName -Name $ClientProductName

$clientArgs = @()
if ($ClientConfigSuffix)
{
    $clientArgs += "--mwc-config=$ClientConfigSuffix"
}

& $applyScript -Mode Client -GameDir $resolvedClientGameDir -HostSteamId $HostSteamId -ConfigSuffix $ClientConfigSuffix | Out-Null
try
{
    $clientProc = Start-Process -FilePath $clientExePath -WorkingDirectory $resolvedClientGameDir -ArgumentList $clientArgs -PassThru -ErrorAction Stop
}
catch
{
    Write-Warning "Client launch failed: $($_.Exception.Message)"
    Write-Warning "Config left in Client mode."
    return
}

Set-ProductName -Name $OriginalProductName

Write-Host "Launched host and client."
Write-Host "Press F6 in the first instance and F7 in the second."
if ($HostSteamId -eq 0)
{
    Write-Host "Set SpectatorHostSteamId in the client config or rerun with -HostSteamId."
}
