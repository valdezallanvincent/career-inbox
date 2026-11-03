param([switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$data = Join-Path $projectRoot 'data'
New-Item -ItemType Directory -Force $data | Out-Null
$env:CAREER_INBOX_DATA = $data
$dll = Join-Path $projectRoot 'src\CareerInbox\bin\Release\net8.0\CareerInbox.dll'
if (!(Test-Path -LiteralPath $dll)) {
    dotnet build (Join-Path $projectRoot 'src\CareerInbox') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
try {
    $health = Invoke-RestMethod 'http://127.0.0.1:5680/health' -TimeoutSec 2
    if ($health.service -ne 'Career Inbox') { throw 'Port 5680 is in use by another service.' }
} catch {
    if (Get-NetTCPConnection -LocalPort 5680 -State Listen -ErrorAction SilentlyContinue) { throw 'Port 5680 is already in use.' }
    $started = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @(('"' + $dll + '"')) -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $data 'server.log') -RedirectStandardError (Join-Path $data 'error.log')
    $started.Id | Set-Content (Join-Path $data 'server.pid')
}
$n8nLauncher = Join-Path (Split-Path $projectRoot) 'n8n-project\Manage-n8n.ps1'
if (Test-Path -LiteralPath $n8nLauncher) { & $n8nLauncher -Action Start -NoBrowser }
Write-Output 'Career Inbox API: http://127.0.0.1:5680/health'
Write-Output 'Capture jobs: http://localhost:5678/form/career-inbox-capture'
Write-Output 'Update status: http://localhost:5678/form/career-inbox-status'
Write-Output 'Shortlist: http://127.0.0.1:5680/digest'
if (!$NoBrowser) { Start-Process 'http://localhost:5678/form/career-inbox-capture' }
