$root=Split-Path $PSScriptRoot -Parent
$file=Join-Path $root 'data/processes.json'
if(Test-Path -LiteralPath $file){foreach($entry in(Get-Content -LiteralPath $file -Raw|ConvertFrom-Json)){$p=Get-Process -Id $entry.pid -ErrorAction SilentlyContinue;if($p -and $p.ProcessName -eq 'dotnet' -and (-not $entry.started -or $p.StartTime.ToUniversalTime().ToString('o') -eq $entry.started)){Stop-Process -Id $p.Id}}}
