$projectRoot = Split-Path $PSScriptRoot
$pidFile = Join-Path $projectRoot 'data\server.pid'
if (Test-Path -LiteralPath $pidFile) {
    $serviceProcessId = [int](Get-Content -LiteralPath $pidFile)
    $serviceProcess = Get-CimInstance Win32_Process -Filter "ProcessId = $serviceProcessId" -ErrorAction SilentlyContinue
    $dll = Join-Path $projectRoot 'src\CareerInbox\bin\Release\net8.0\CareerInbox.dll'
    if ($serviceProcess -and $serviceProcess.CommandLine.Contains($dll)) { Stop-Process -Id $serviceProcessId }
    Remove-Item -LiteralPath $pidFile
}
Write-Output 'Career Inbox API stopped. n8n is left running for your other workflows.'
