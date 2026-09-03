<#
.SYNOPSIS
    Wrapper script for the scheduled Bot MorningJob run. Handles the full lifecycle of the
    Stripe database pgweb proxy (via myob-auth) around the console app execution, so the
    "Stripe Latest Completed" report value is populated automatically with no manual steps.

.DESCRIPTION
    1. Authenticates with Kubernetes (myob-auth kube-login).
    2. Starts "myob-auth kube db-proxy" in the background, forwarding to a local pgweb port.
    3. Waits for the proxy to become reachable (with a timeout).
    4. Runs Bot.MorningJob.Console.exe.
    5. Stops the proxy process afterward, regardless of success/failure.

.NOTES
    Requires the myob-auth CLI to be installed and available on PATH, and the user running
    this script (e.g., the Windows Task Scheduler service account) to have valid OIDC/AWS
    access to the "billing-platform" namespace.
#>

param(
    [string]$Namespace = "billing-platform",
    [string]$Environment = "europa",
    [string]$Database = "stripe-db",
    [int]$ProxyPort = 8081,
    [int]$ProxyReadyTimeoutSeconds = 60,
    [string]$ExePath = (Join-Path $PSScriptRoot "bin\Debug\net8.0\Bot.MorningJob.Console.exe")
)

$ErrorActionPreference = "Stop"

function Write-Log {
    param([string]$Message)
    Write-Host "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Message"
}

function Test-ProxyReachable {
    param([int]$Port)
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $client.Connect("127.0.0.1", $Port)
        $client.Close()
        return $true
    }
    catch {
        return $false
    }
}

$proxyProcess = $null

try {
    Write-Log "Authenticating with myob-auth (kube-login -e $Environment --namespace=$Namespace)..."
    & myob-auth kube-login -e $Environment --namespace=$Namespace
    if ($LASTEXITCODE -ne 0) {
        throw "myob-auth kube-login failed with exit code $LASTEXITCODE"
    }

    Write-Log "Starting myob-auth kube db-proxy (database=$Database, port=$ProxyPort)..."
    $proxyProcess = Start-Process -FilePath "myob-auth" `
        -ArgumentList "kube db-proxy -d $Database -n $Namespace -p $ProxyPort" `
        -WindowStyle Hidden -PassThru

    Write-Log "Waiting for proxy to become reachable on port $ProxyPort (timeout ${ProxyReadyTimeoutSeconds}s)..."
    $deadline = (Get-Date).AddSeconds($ProxyReadyTimeoutSeconds)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        if (Test-ProxyReachable -Port $ProxyPort) {
            $ready = $true
            break
        }
        Start-Sleep -Seconds 2
    }

    if (-not $ready) {
        Write-Log "WARNING: Proxy did not become reachable within timeout. Continuing anyway; 'Stripe Latest completed' will show as Unavailable in the report."
    }
    else {
        Write-Log "Proxy is ready."
    }

    Write-Log "Running $ExePath ..."
    & $ExePath
    $exitCode = $LASTEXITCODE
    Write-Log "Bot.MorningJob.Console exited with code $exitCode."
}
finally {
    if ($proxyProcess -and -not $proxyProcess.HasExited) {
        Write-Log "Stopping db-proxy process (PID $($proxyProcess.Id))..."
        Stop-Process -Id $proxyProcess.Id -Force -ErrorAction SilentlyContinue
    }
}
