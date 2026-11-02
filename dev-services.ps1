# Starts (or stops, with -Stop) the local dev services wildcard-api needs.
# -Stop also shuts down Docker Desktop, WSL and RabbitMQ's leftover Erlang
# port mapper (epmd), so nothing dev-related keeps running in the background.
# Re-launches itself elevated, since starting/stopping services needs admin.
param([switch]$Stop)

$services = @('postgresql-x64-18', 'Redis', 'RabbitMQ')

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($Stop) { $argList += ' -Stop' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit
}

foreach ($name in $services) {
    try {
        if ($Stop) {
            Stop-Service -Name $name -Force -ErrorAction Stop
        } else {
            Start-Service -Name $name -ErrorAction Stop
        }
    } catch {
        Write-Host "$name : $($_.Exception.Message)" -ForegroundColor Red
    }
}

if ($Stop) {
    # Docker Desktop (app + backend service), then the WSL2 VM it runs in.
    Get-Process -Name 'Docker Desktop', 'com.docker.backend', 'com.docker.build' -ErrorAction SilentlyContinue |
        Stop-Process -Force
    Stop-Service -Name 'com.docker.service' -Force -ErrorAction SilentlyContinue
    wsl.exe --shutdown

    # RabbitMQ stops, but Erlang's port mapper daemon stays behind.
    Get-Process -Name 'epmd', 'erl', 'erlsrv' -ErrorAction SilentlyContinue | Stop-Process -Force
}

Get-Service -Name ($services + 'com.docker.service') | Format-Table Name, Status -AutoSize

$node = Get-Process -Name node -ErrorAction SilentlyContinue
if ($Stop -and $node) {
    $mb = [math]::Round(($node | Measure-Object WorkingSet64 -Sum).Sum / 1MB)
    Write-Host "$($node.Count) node processes still running ($mb MB) - stop 'pnpm dev' / close VS Code to free them." -ForegroundColor Yellow
}

Read-Host 'Press Enter to close'
