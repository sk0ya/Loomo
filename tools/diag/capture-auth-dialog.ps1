<#
.SYNOPSIS
  端末（Claude Code の MCP サーバーなど）から出た認証ダイアログが Loomo の後ろに隠れる件の記録用。
  走らせている間に新しく現れたトップレベル窓を、プロセス・親の連なり・クラス・題・持ち主・Z 順・クローク状態ごと書き出す。

.USAGE
  pwsh -NoProfile -File tools\diag\capture-auth-dialog.ps1            # 既定 180 秒
  pwsh -NoProfile -File tools\diag\capture-auth-dialog.ps1 -Seconds 300
  走らせたら Loomo のターミナルでダイアログが出る操作をし、出たら（後ろに隠れたまま）しばらく待ってから、
  前に出して閉じる。終わると記録ファイルの場所を表示する。
#>
param([int]$Seconds = 180)

$src = @'
using System; using System.Runtime.InteropServices; using System.Text; using System.Collections.Generic;
public static class AuthDiag {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int size);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

  // 上（手前）から順のトップレベル窓
  public static List<IntPtr> TopLevel() {
    var r = new List<IntPtr>();
    EnumWindows((h, l) => { r.Add(h); return true; }, IntPtr.Zero);
    return r;
  }
  public static uint Pid(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
  public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static string Title(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
  public static bool Cloaked(IntPtr h) { int v; return DwmGetWindowAttribute(h, 14, out v, 4) == 0 && v != 0; }
  public static long ExStyle(IntPtr h) { return (long)GetWindowLongPtr(h, -20); }
  public static string Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return string.Format("{0},{1} {2}x{3}", r.L, r.T, r.R - r.L, r.B - r.T); }
}
'@
Add-Type $src

$log = Join-Path $env:TEMP ("loomo-auth-dialog-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
function W($s) { $line = "{0:HH:mm:ss.fff} {1}" -f (Get-Date), $s; Add-Content -Path $log -Value $line -Encoding utf8; Write-Host $line }

function ProcInfo([uint32]$id) {
  $chain = @()
  $cur = $id
  for ($i = 0; $i -lt 8 -and $cur; $i++) {
    $p = Get-CimInstance Win32_Process -Filter "ProcessId=$cur" -ErrorAction SilentlyContinue
    if (-not $p) { $chain += "$cur(?)"; break }
    $chain += "$($p.ProcessId) $($p.Name)"
    $cur = $p.ParentProcessId
  }
  $first = Get-CimInstance Win32_Process -Filter "ProcessId=$id" -ErrorAction SilentlyContinue
  [pscustomobject]@{ Chain = ($chain -join ' <- '); Path = $first.ExecutablePath; CommandLine = $first.CommandLine }
}

function Describe([IntPtr]$h, [System.Collections.Generic.List[IntPtr]]$order, $loomo) {
  $id = [AuthDiag]::Pid($h)
  $owner = [AuthDiag]::GetWindow($h, 4)
  $ownerPid = if ($owner -ne [IntPtr]::Zero) { [AuthDiag]::Pid($owner) } else { 0 }
  $ex = [AuthDiag]::ExStyle($h)
  $z = $order.IndexOf($h)
  $loomoZ = ($loomo | % { $order.IndexOf($_) } | ? { $_ -ge 0 } | Measure-Object -Minimum).Minimum
  $pi = ProcInfo $id
  "hwnd={0:X} class='{1}' title='{2}' visible={3} cloaked={4} rect={5} exStyle=0x{6:X} topmost={7} toolwindow={8} owner={9:X}(pid {10}) z={11} loomoZ={12} behindLoomo={13} fg={14}`n    proc: {15}`n    path: {16}`n    cmd : {17}" -f `
    [int64]$h, [AuthDiag]::Cls($h), [AuthDiag]::Title($h), [AuthDiag]::IsWindowVisible($h), [AuthDiag]::Cloaked($h), [AuthDiag]::Rect($h),
    $ex, (($ex -band 0x8) -ne 0), (($ex -band 0x80) -ne 0), [int64]$owner, $ownerPid, $z, $loomoZ,
    ($null -ne $loomoZ -and $z -gt $loomoZ), ([AuthDiag]::GetForegroundWindow() -eq $h), $pi.Chain, $pi.Path, $pi.CommandLine
}

$loomoPids = @(Get-Process -Name 'sk0ya.Loomo.App' -ErrorAction SilentlyContinue | % Id)
W "記録開始 $Seconds 秒 / Loomo pid: $($loomoPids -join ',') / OS: $([Environment]::OSVersion.VersionString)"
if (-not $loomoPids) { W "※ Loomo が動いていない" }

# 初めから在る窓は覚えるだけ（見えている状態も）
$state = @{}
foreach ($h in [AuthDiag]::TopLevel()) { $state[[int64]$h] = "$([AuthDiag]::IsWindowVisible($h))/$([AuthDiag]::Cloaked($h))" }
$lastFg = [AuthDiag]::GetForegroundWindow()

$deadline = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $deadline) {
  $order = [System.Collections.Generic.List[IntPtr]]::new([AuthDiag]::TopLevel())
  $loomo = @($order | ? { $loomoPids -contains [AuthDiag]::Pid($_) -and [AuthDiag]::IsWindowVisible($_) })
  foreach ($h in $order) {
    $key = [int64]$h
    $now = "$([AuthDiag]::IsWindowVisible($h))/$([AuthDiag]::Cloaked($h))"
    $prev = $state[$key]
    if ($prev -ne $now) {
      $state[$key] = $now
      # 見えるようになった（新規・表示・クローク解除）ものだけ。消えた・隠れたは数が多いので書かない
      if ([AuthDiag]::IsWindowVisible($h) -and $loomoPids -notcontains [AuthDiag]::Pid($h)) {
        $what = if ($null -eq $prev) { "新しい窓" } else { "見えるようになった窓 ($prev -> $now)" }
        W "$what $(Describe $h $order $loomo)"
      }
    }
  }
  $fg = [AuthDiag]::GetForegroundWindow()
  if ($fg -ne $lastFg) {
    $lastFg = $fg
    W ("前面が替わった fg={0:X} pid={1} class='{2}' title='{3}'" -f [int64]$fg, [AuthDiag]::Pid($fg), [AuthDiag]::Cls($fg), [AuthDiag]::Title($fg))
  }
  Start-Sleep -Milliseconds 250
}
W "記録終了"
Write-Host ""
Write-Host "記録ファイル: $log"
