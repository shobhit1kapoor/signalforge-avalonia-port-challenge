param(
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath,
    [ValidateRange(5, 60)]
    [int] $StartupSeconds = 12
)

$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$process = Start-Process -FilePath $resolvedExecutable -WindowStyle Hidden -PassThru

try {
    for ($second = 0; $second -lt $StartupSeconds; $second++) {
        Start-Sleep -Seconds 1
        $process.Refresh()

        if ($process.HasExited) {
            throw "Desktop process exited during startup with code $($process.ExitCode)."
        }
    }

    Write-Host "Desktop remained running for $StartupSeconds seconds."
}
finally {
    $process.Refresh()
    if (-not $process.HasExited) {
        if (-not $process.CloseMainWindow()) {
            Stop-Process -Id $process.Id -Force
        }

        if (-not $process.WaitForExit(5000)) {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit()
        }
    }
}
