# Runs a command with its output captured to a log file while showing a spinner and elapsed time,
# so it is obvious the process has not frozen. On failure, the tail of the log is printed.
# Usage: run-with-spinner.ps1 -Message "Publishing..." -Command dotnet -Arguments "publish,..."  (comma-separated; -File cannot pass arrays)
param(
    [Parameter(Mandatory)][string]$Message,
    [Parameter(Mandatory)][string]$Command,
    [string]$Arguments = ''
)

$log = Join-Path $env:TEMP ("spinner-" + [guid]::NewGuid().ToString('N') + ".log")
$err = "$log.err"

$argList = $Arguments -split ','

$p = Start-Process -FilePath $Command -ArgumentList $argList -NoNewWindow -PassThru `
    -RedirectStandardOutput $log -RedirectStandardError $err
$null = $p.Handle  # keep the handle so ExitCode is available after exit

$frames = '|', '/', '-', '\'
$i = 0
$sw = [Diagnostics.Stopwatch]::StartNew()
while (-not $p.WaitForExit(150)) {
    $t = [int]$sw.Elapsed.TotalSeconds
    Write-Host -NoNewline ("`r[{0}] {1} ({2}:{3:00})" -f $frames[$i % 4], $Message, [math]::Floor($t / 60), ($t % 60))
    $i++
}
$t = [int]$sw.Elapsed.TotalSeconds
Write-Host ("`r[done] {0} ({1}:{2:00})          " -f $Message, [math]::Floor($t / 60), ($t % 60))

$code = $p.ExitCode
if ($code -ne 0) {
    Write-Host ""
    Get-Content $log, $err -Tail 40 -ErrorAction SilentlyContinue
}
Remove-Item $log, $err -ErrorAction SilentlyContinue
exit $code
