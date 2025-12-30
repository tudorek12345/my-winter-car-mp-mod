param(
    [string]$GameDir = "C:\\Games\\My.Winter.Car\\game",
    [UInt64]$HostSteamId = 0,
    [int]$DelaySeconds = 5
)

$exePath = Join-Path $GameDir "mywintercar.exe"
if (!(Test-Path $exePath))
{
    throw "Game executable not found: $exePath"
}

$applyScript = Join-Path $PSScriptRoot "apply-config.ps1"
if (!(Test-Path $applyScript))
{
    throw "Config script not found: $applyScript"
}

& $applyScript -Mode Host -GameDir $GameDir | Out-Null
try
{
    $hostProc = Start-Process -FilePath $exePath -WorkingDirectory $GameDir -PassThru -ErrorAction Stop
}
catch
{
    Write-Warning "Host launch failed: $($_.Exception.Message)"
    Write-Warning "Config left in Host mode."
    return
}

Start-Sleep -Seconds $DelaySeconds

& $applyScript -Mode Client -GameDir $GameDir -HostSteamId $HostSteamId | Out-Null
try
{
    $clientProc = Start-Process -FilePath $exePath -WorkingDirectory $GameDir -PassThru -ErrorAction Stop
}
catch
{
    Write-Warning "Client launch failed: $($_.Exception.Message)"
    Write-Warning "Config left in Client mode."
    return
}

Write-Host "Launched host and client."
Write-Host "Press F6 in the first instance and F7 in the second."
if ($HostSteamId -eq 0)
{
    Write-Host "Set SpectatorHostSteamId in the client config or rerun with -HostSteamId."
}
