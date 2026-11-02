[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Scenes = 'import,settings,commit,update,push,branches,log,diff,blame',
    [string]$Out = '',
    [switch]$Prepare
)
$ErrorActionPreference = 'Stop'
Add-Type -Namespace Rec -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int c);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
'@
$store = (Resolve-Path "$PSScriptRoot\..\..\..").ProviderPath
$project = "$store\demo-cn"
$video = "$store\media\video"
if (-not $Out) { $Out = "$video\raw.mkv" }
$editor = 'F:\tuanjie\installs\2022.3.61t15\Editor\Tuanjie.exe'
$state = "$project\Temp\Video.state.json"
New-Item -ItemType Directory -Force $video | Out-Null

if (Get-CimInstance Win32_Process -Filter "Name='Tuanjie.exe'" | Where-Object { $_.CommandLine -match 'demo-cn' }) { "Close Tuanjie on demo-cn first"; exit 1 }
& python "$PSScriptRoot\reset.py"
if ($LASTEXITCODE -ne 0) { "reset failed"; exit 1 }
if (Test-Path $state) { Remove-Item $state -Force }

$process = Start-Process $editor -ArgumentList '-projectPath', "`"$project`"" -PassThru
$deadline = (Get-Date).AddMinutes(8)
while (-not (Test-Path $state)) {
    if ((Get-Date) -gt $deadline) { "The editor did not start in 8 min"; exit 1 }
    Start-Sleep -Seconds 2
}
$process.Refresh()
while ($process.MainWindowHandle -eq [IntPtr]::Zero) {
    if ((Get-Date) -gt $deadline) { "No editor window in 8 min"; exit 1 }
    Start-Sleep -Milliseconds 500
    $process.Refresh()
}
[Rec.W]::ShowWindow($process.MainWindowHandle, 3) | Out-Null
[Rec.W]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
Start-Sleep -Seconds 10
if ($Prepare) { "The editor is ready on $project"; exit 0 }

$info = New-Object System.Diagnostics.ProcessStartInfo
$info.FileName = 'ffmpeg'
$info.Arguments = "-hide_banner -loglevel error -y -use_wallclock_as_timestamps 1 -f gdigrab -framerate 30 -draw_mouse 1 -i desktop -copyts -c:v libx264 -preset ultrafast -crf 12 `"$Out`""
$info.UseShellExecute = $false
$info.RedirectStandardInput = $true
$ffmpeg = [System.Diagnostics.Process]::Start($info)
Start-Sleep -Seconds 2

if (Test-Path "$video\timeline.json") { Remove-Item "$video\timeline.json" -Force }
& python "$PSScriptRoot\drive.py" --scenes $Scenes --timeline "$video\timeline.json"
$code = $LASTEXITCODE

Start-Sleep -Seconds 1
$ffmpeg.StandardInput.Write('q')
$ffmpeg.StandardInput.Close()
if (-not $ffmpeg.WaitForExit(60000)) { $ffmpeg.Kill() }
"driver exit $code, recording: $Out, timeline: $video\timeline.json"
exit $code
