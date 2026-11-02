param([string[]]$Shots = @('setup', 'changes', 'branches', 'log', 'diff', 'blame'))
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Cap -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int c);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool PrintWindow(System.IntPtr h, System.IntPtr dc, uint flags);
public struct RECT { public int Left, Top, Right, Bottom; }
'@
$project = 'E:\projects\vetka\store\demo'
$out = 'E:\projects\vetka\store\media\shots'
New-Item -ItemType Directory -Force $out | Out-Null
$previous = [Cap.W]::GetForegroundWindow()

$unity = Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object { $_.CommandLine -match 'store\\demo' } | Select-Object -First 1
if (-not $unity) {
  Start-Process -FilePath 'F:\unity\installs\6000.0.74f1\Editor\Unity.exe' -ArgumentList '-projectPath', $project | Out-Null
}
$deadline = (Get-Date).AddMinutes(4); $window = $null
while ((Get-Date) -lt $deadline -and -not $window) {
  Start-Sleep -Seconds 3
  $window = Get-Process Unity -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -match '^demo\b' } | Select-Object -First 1
}
if (-not $window) { 'Unity window did not appear'; exit 1 }
Start-Sleep -Seconds 15
$hwnd = $window.MainWindowHandle
[void][Cap.W]::ShowWindow($hwnd, 3)
[void][Cap.W]::SetForegroundWindow($hwnd)
Start-Sleep -Seconds 3

foreach ($shot in $Shots) {
  $done = Join-Path $project 'Temp\Shots.done'
  if (Test-Path $done) { Remove-Item $done }
  [IO.File]::WriteAllText((Join-Path $project 'Temp\Shots.request'), $shot)
  $wait = (Get-Date).AddSeconds(60)
  while ((Get-Date) -lt $wait -and -not (Test-Path $done)) { Start-Sleep -Milliseconds 500 }
  if (-not (Test-Path $done)) { "$shot : director did not answer"; continue }
  if ($shot -eq 'setup') { Start-Sleep -Seconds 3; continue }
  Start-Sleep -Seconds 1
  $rect = New-Object Cap.W+RECT
  [void][Cap.W]::GetWindowRect($hwnd, [ref]$rect)
  $width = $rect.Right - $rect.Left; $height = $rect.Bottom - $rect.Top
  $bitmap = New-Object System.Drawing.Bitmap $width, $height
  $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
  $dc = $graphics.GetHdc()
  [void][Cap.W]::PrintWindow($hwnd, $dc, 2)
  $graphics.ReleaseHdc($dc); $graphics.Dispose()
  $file = Join-Path $out "raw-$shot.png"
  $bitmap.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
  "$shot : $file ($width x $height)"
}
[void][Cap.W]::SetForegroundWindow($previous)
