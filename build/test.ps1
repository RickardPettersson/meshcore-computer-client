<#
.SYNOPSIS
    Runs all tests in Release. On GitHub Actions, each failing test also becomes an error annotation
    (test name + message), which anyone can read on the run page or through the public API, without signing in.

.EXAMPLE
    ./build/test.ps1
#>
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
Set-Location (Split-Path $PSScriptRoot -Parent)

$results = "artifacts/test-results"
Remove-Item $results -Recurse -Force -ErrorAction SilentlyContinue

dotnet test MeshCore.slnx -c Release --nologo -v q --logger "trx" --results-directory $results
$exit = $LASTEXITCODE

if ($exit -ne 0 -and $env:GITHUB_ACTIONS -eq "true") {
    foreach ($trx in Get-ChildItem $results -Filter *.trx -Recurse -ErrorAction SilentlyContinue) {
        [xml] $doc = Get-Content -LiteralPath $trx.FullName -Raw
        foreach ($r in @($doc.TestRun.Results.UnitTestResult) | Where-Object { $_ -and $_.outcome -eq "Failed" }) {
            $message = "$($r.Output.ErrorInfo.Message)".Trim()
            $stack = "$($r.Output.ErrorInfo.StackTrace)".Trim() -split "`n" | Select-Object -First 3
            # Annotation text: newlines must be encoded as %0A.
            $text = (@($message) + $stack) -join "`n" -replace '%', '%25' -replace "`r", '' -replace "`n", '%0A'
            Write-Host "::error title=Test failed: $($r.testName)::$text"
        }
    }
}
if ($exit -ne 0) { throw "Tests failed (exit $exit)" }
