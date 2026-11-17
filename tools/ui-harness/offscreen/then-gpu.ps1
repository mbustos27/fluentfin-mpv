# Seaglass GPU and CPU cost of the Home scroll, run with: run-offscreen.ps1 ... -Script home -Then offscreen\then-gpu.ps1
# (dot-sourced by drive.ps1 after `home`). Samples the harness's own Seaglass.exe (found by its --data-dir, never another copy) with
# the Windows "GPU Engine" counters (the sum over its engines) and its CPU time while Home scrolls to the bottom and back 8 times.
# Compare a Full run with -Setting Glass=Solid. The result is written next to the shots as gpu.json and printed.
$me = Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(Seaglass|FluentFin)\.exe$' -and $_.CommandLine -like "*$DataDir*" } | Select-Object -First 1
Check 'gpu: found the harness-started Seaglass.exe' ([bool]$me)
if ($me) {
    $procId = [int]$me.ProcessId
    $samples = New-Object System.Collections.Generic.List[double]
    $cpu = New-Object System.Collections.Generic.List[double]
    $proc = Get-Process -Id $procId
    $lastCpu = $proc.TotalProcessorTime.TotalSeconds
    $lastAt = [DateTime]::UtcNow
    Start-Sleep -Seconds 2
    for ($sweep = 0; $sweep -lt 8; $sweep++) {
        foreach ($percent in 100, 0) {
            [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, $percent)
            for ($i = 0; $i -lt 4; $i++) {
                Start-Sleep -Milliseconds 250
                try {
                    $c = (Get-Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction Stop).CounterSamples | Where-Object { $_.InstanceName -match "pid_${procId}_" }
                    $samples.Add([double](($c | Measure-Object CookedValue -Sum).Sum))
                } catch { }
                $proc.Refresh()
                $now = [DateTime]::UtcNow
                $t = $proc.TotalProcessorTime.TotalSeconds
                $cpu.Add(100 * ($t - $lastCpu) / (($now - $lastAt).TotalSeconds * [Environment]::ProcessorCount))
                $lastCpu = $t; $lastAt = $now
            }
        }
    }
    function Stats($v) { if ($v.Count -eq 0) { return @{ n = 0 } }; $s = $v | Sort-Object; @{ n = $v.Count; mean = [math]::Round(($v | Measure-Object -Average).Average, 2); p95 = [math]::Round($s[[int]([math]::Floor(0.95 * ($s.Count - 1)))], 2); max = [math]::Round($s[-1], 2) } }
    $result = @{ gpuPercentSumOfEngines = Stats $samples; cpuPercentOfAllCores = Stats $cpu; dataDir = $DataDir }
    $json = $result | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText((Join-Path $Out 'gpu.json'), $json)
    Say "gpu: $json"
    Check 'gpu: counters were readable' ($samples.Count -gt 0)
}
