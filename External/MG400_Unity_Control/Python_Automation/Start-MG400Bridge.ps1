[CmdletBinding()]
param(
    [ipaddress] $RobotIp = '192.168.1.6',
    [string] $PythonExe,
    # everything else goes to bridge_server.py, e.g.  --user-frame 1 --tool-frame 1
    [Parameter(ValueFromRemainingArguments = $true)] [string[]] $BridgeArgs
)

$ErrorActionPreference = 'Stop'
$bridgeFile = Join-Path $PSScriptRoot 'bridge_server.py'
$bridgePorts = @(5555, 5556)
$robotPorts = @(29999, 30003)
$robotAddress = $RobotIp.IPAddressToString
$bridgePattern = '(?:^|\s)"?' + [regex]::Escape($bridgeFile) + '"?(?=\s|$)'

if (-not (Test-Path -LiteralPath $bridgeFile -PathType Leaf)) {
    throw "Bridge script not found: $bridgeFile"
}

$pythonOptions = @()
if ($PythonExe) {
    $python = Get-Command $PythonExe -CommandType Application
} else {
    $python = Get-Command py.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($python) { $pythonOptions = @('-3') }
    else { $python = Get-Command python.exe -CommandType Application }
}

function Get-BridgeListeners {
    Get-NetTCPConnection -LocalPort $bridgePorts -State Listen -ErrorAction SilentlyContinue
}

function Get-ProjectBridgeProcess([int] $ProcessId) {
    $process = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId"
    if ($null -eq $process -or $process.Name -notmatch '^python(?:3(?:\.\d+)?)?\.exe$' -or
        $process.CommandLine -notmatch $bridgePattern) {
        throw "Local Bridge port is held by another process (PID $ProcessId). No process was stopped."
    }
    $process
}

$listeners = @(Get-BridgeListeners)
$existingIds = @($listeners | Select-Object -ExpandProperty OwningProcess -Unique)
$existing = @(foreach ($processId in $existingIds) { Get-ProjectBridgeProcess $processId })

$clients = @(Get-NetTCPConnection -LocalPort $bridgePorts -State Established -ErrorAction SilentlyContinue)
if ($clients.Count) {
    throw 'A Bridge client is connected. Exit Unity Play mode, then run this launcher again.'
}

$otherRobotClients = @(Get-NetTCPConnection -RemoteAddress $robotAddress -RemotePort $robotPorts `
    -State Established,SynSent,SynReceived -ErrorAction SilentlyContinue |
    Where-Object { $_.OwningProcess -notin $existingIds })
if ($otherRobotClients.Count) {
    $details = ($otherRobotClients | ForEach-Object { "port $($_.RemotePort), PID $($_.OwningProcess)" }) -join '; '
    throw "Another client is using MG400 $robotAddress ($details). Disconnect that client and try again."
}

foreach ($process in $existing) {
    $owned = Get-ProjectBridgeProcess $process.ProcessId
    if ($owned.CreationDate -ne $process.CreationDate) {
        throw "Bridge process changed during startup (PID $($process.ProcessId)). Try again."
    }
    if (@(Get-NetTCPConnection -OwningProcess $process.ProcessId -LocalPort $bridgePorts `
            -State Established -ErrorAction SilentlyContinue).Count) {
        throw 'A Bridge client just connected. Exit Unity Play mode, then try again.'
    }
    $running = Get-Process -Id $process.ProcessId
    Stop-Process -InputObject $running -Force
    if (-not $running.WaitForExit(5000)) {
        throw "Bridge process did not exit (PID $($process.ProcessId))."
    }
    Write-Host "Closed previous project Bridge (PID $($process.ProcessId))."
}

if (@(Get-BridgeListeners).Count) {
    throw 'A process is still listening on 5555/5556. No second Bridge was started.'
}

Write-Host "Starting MG400 Bridge at $robotAddress. Keep this window open while using Unity."
Push-Location -LiteralPath $PSScriptRoot
try {
    & $python.Source @pythonOptions '-u' $bridgeFile '--real' '--ip' $robotAddress '--profile' 'cr' '--no-audio' '--no-mqtt' @BridgeArgs
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
