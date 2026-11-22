$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    dotnet build src/Host/Host.csproj --nologo
    if ($LASTEXITCODE -ne 0) { throw 'API build failed' }
    dotnet build src/Simulator/Simulator.csproj --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Simulator build failed' }
    New-Item -ItemType Directory -Force logs,data | Out-Null
    $processes = @()
    foreach ($item in @(@('tournament',5101),@('scores',5102),@('results',5103),@('simulator',5104))) {
        $name = $item[0]
        $env:SERVICE = $name
        $env:DB = Join-Path $root "data/$name.db"
        $env:ASPNETCORE_URLS = "http://localhost:$($item[1])"
        $dll = if ($name -eq 'simulator') { 'src/Simulator/bin/Debug/net8.0/Simulator.dll' } else { 'src/Host/bin/Debug/net8.0/Host.dll' }
        $p = Start-Process dotnet -ArgumentList $dll -WorkingDirectory $root -PassThru -WindowStyle Hidden -RedirectStandardOutput "logs/$name.log" -RedirectStandardError "logs/$name.err.log"
        $processes += @{name=$name;pid=$p.Id}
    }
    $processes | ConvertTo-Json | Set-Content data/processes.json
    foreach ($port in 5101,5102,5103,5104) {
        $ready = $false
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            try { $health = Invoke-RestMethod "http://localhost:$port/health" -TimeoutSec 2; if ($health.status -eq 'ok') { $ready = $true; break } } catch { }
            Start-Sleep -Milliseconds 300
        }
        if (-not $ready) { throw "Service on port $port did not start. See logs." }
    }
    Write-Host 'Ready. Open http://localhost:5102 and click Demo, or run python scripts/demo.py'
} finally { Pop-Location }
