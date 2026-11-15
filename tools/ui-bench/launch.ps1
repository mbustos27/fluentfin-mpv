param([string]$Exe, [int]$Runs = 5, [string]$Label = 'release')
$ErrorActionPreference = 'Stop'
Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);'
# no network for the child: a dead proxy makes every .NET HTTP request fail fast (update check, any sign-in)
$env:HTTP_PROXY = 'http://127.0.0.1:9'; $env:HTTPS_PROXY = 'http://127.0.0.1:9'; $env:ALL_PROXY = 'http://127.0.0.1:9'; $env:NO_PROXY = ''
$results = @()
for ($i = 1; $i -le $Runs; $i++) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $p = Start-Process -FilePath $Exe -PassThru -WorkingDirectory (Split-Path $Exe)
  $tWin = $null
  while ($sw.Elapsed.TotalSeconds -lt 60) {
    $p.Refresh()
    if ($p.HasExited) { break }
    if ($p.MainWindowHandle -ne [IntPtr]::Zero -and [W.U]::IsWindowVisible($p.MainWindowHandle)) { $tWin = $sw.Elapsed.TotalMilliseconds; break }
    Start-Sleep -Milliseconds 10
  }
  Start-Sleep -Seconds 6
  $p.Refresh()
  $ws = [math]::Round($p.WorkingSet64/1MB,1); $pb = [math]::Round($p.PrivateMemorySize64/1MB,1); $th = $p.Threads.Count; $title = $p.MainWindowTitle
  $mods = 0; try { $mods = $p.Modules.Count } catch {}
  $ff = 0; try { $ff = @($p.Modules | Where-Object { $_.ModuleName -match '^(avcodec|avformat|avutil|swresample|swscale|avfilter|avdevice)' }).Count } catch {}
  $vlc = 0; try { $vlc = @($p.Modules | Where-Object { $_.ModuleName -match '^libvlc' }).Count } catch {}
  $results += [pscustomobject]@{ Label=$Label; Run=$i; FirstWindowMs=[math]::Round($tWin,0); WorkingSetMB=$ws; PrivateMB=$pb; Threads=$th; Modules=$mods; FFmpegDllsLoaded=$ff; LibVlcLoaded=$vlc; Title=$title; Exited=$p.HasExited }
  try { [void]$p.CloseMainWindow(); if (-not $p.WaitForExit(8000)) { $p.Kill() } } catch { try { $p.Kill() } catch {} }
  Start-Sleep -Seconds 2
}
$results | Format-Table -AutoSize | Out-String -Width 220
