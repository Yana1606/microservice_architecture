param([ValidateRange(1024,65532)][int]$BasePort=5101,[switch]$DisableLabDemos)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Push-Location $root
$started=@()
$names=@('SERVICE','DB','ASPNETCORE_URLS','PEER_TOURNAMENT','PEER_SCORES','PEER_RESULTS','PEER_SIMULATOR','SCORES_URL','ENABLE_LAB_DEMOS')
$saved=@{};foreach($key in $names){$saved[$key]=[Environment]::GetEnvironmentVariable($key,'Process')}
try{
    foreach($offset in 0..3){$listener=New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback,($BasePort+$offset));try{$listener.Start()}catch{throw "Port $($BasePort+$offset) occupied. Stop the previous copy, or use -BasePort 7101."}finally{$listener.Stop()}}
    dotnet build MathTournament.sln --nologo
    if($LASTEXITCODE -ne 0){throw 'Build failed'}
    New-Item -ItemType Directory -Force logs,data|Out-Null
    foreach($item in @(@('tournament',0),@('scores',1),@('results',2),@('simulator',3))){
        $name=$item[0];$port=$BasePort+$item[1]
        $env:SERVICE=$name;$env:DB=Join-Path $root "data/$name.db";$env:ASPNETCORE_URLS="http://127.0.0.1:$port"
        $env:PEER_TOURNAMENT="http://127.0.0.1:$BasePort";$env:PEER_SCORES="http://127.0.0.1:$($BasePort+1)";$env:PEER_RESULTS="http://127.0.0.1:$($BasePort+2)";$env:PEER_SIMULATOR="http://127.0.0.1:$($BasePort+3)";$env:SCORES_URL=$env:PEER_SCORES
        $env:ENABLE_LAB_DEMOS=if($DisableLabDemos){'0'}else{'1'}
        $relative=if($name -eq 'simulator'){'src/Simulator/bin/Debug/net8.0/Simulator.dll'}else{'src/Host/bin/Debug/net8.0/Host.dll'}
        $p=Start-Process dotnet -ArgumentList ('"'+(Join-Path $root $relative)+'"') -WorkingDirectory $root -WindowStyle Hidden -PassThru -RedirectStandardOutput "logs/$name.log" -RedirectStandardError "logs/$name.err.log"
        $started+=@{name=$name;pid=$p.Id;started=$p.StartTime.ToUniversalTime().ToString('o');basePort=$BasePort}
    }
    $started|ConvertTo-Json|Set-Content data/processes.json
    foreach($offset in 0..3){$ready=$false;for($i=0;$i -lt 40;$i++){try{if((Invoke-RestMethod "http://127.0.0.1:$($BasePort+$offset)/health" -TimeoutSec 2).status -eq 'ok'){$ready=$true;break}}catch{};Start-Sleep -Milliseconds 250};if(-not $ready){throw 'A service failed to start. See logs.'}}
    Write-Host "Ready: http://localhost:$($BasePort+1) . Lab demonstrations enabled: $(-not $DisableLabDemos)"
}catch{foreach($entry in $started){Stop-Process -Id $entry.pid -ErrorAction SilentlyContinue};throw}
finally{foreach($key in $names){[Environment]::SetEnvironmentVariable($key,$saved[$key],'Process')};Pop-Location}
