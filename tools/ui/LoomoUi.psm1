# 起動中の Loomo を「人と同じ入力」で動かして撮る道具（.claude/skills/ui-usability の「本体で確かめる」）。
#
#   Import-Module ./tools/ui/LoomoUi.psm1 -Force
#   Get-LoomoWindow                                   # Loomo の窓の一覧（設定・別窓も）
#   Save-LoomoShot -Title '設定' -Path shot.png       # 窓を撮る（前面化しない・座標はこの画像の px）
#   Find-LoomoElement -Title '設定' -Name '自分で作る…' # UIA で名前から探して窓内の座標を返す
#   Invoke-LoomoClick -Title '設定' -X 420 -Y 310      # 画像の px で実クリック（SendInput）
#   Invoke-LoomoClick -Title '設定' -Name '自分で作る…' # 名前で探してその中央を実クリック
#   Send-LoomoKeys -Title 'Loomo' -Keys 'Ctrl+Shift+P' # キーを送る（-Text で文字列）
#
# 決まりごと（メモリの罠から）:
# - 合成イベント（RaiseEvent・UIA の Invoke）は実マウスの検証にならない。クリックは SendInput で撃つ。
# - 入力を送る前に前面の窓のプロセスが Loomo か照合し、違えば送らずに止める（他のアプリへの誤爆防止）。
# - 座標は物理 px。このモジュールは読み込み時にプロセスを Per-Monitor DPI にするので、撮った画像の px がそのまま使える。
# - WebView2（ブラウザ・プレビュー・面）の中は UIA で名前が取れないことが多い。画像から座標を読む。
# - 素の ItemsControl の行は UIA に出ない。座標で押す。
# - 人が Loomo を使っている最中は、入力を送る前にひと声かける（作業中の窓を奪う）。撮るだけなら前面化しない。

Set-StrictMode -Version Latest

if (-not ('LoomoUi.Native' -as [type])) {
    Add-Type -ReferencedAssemblies System.Collections, System.Threading.Thread, System.Runtime.InteropServices, System.Runtime -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LoomoUi
{
    public sealed class WindowInfo
    {
        public IntPtr Handle;
        public int ProcessId;
        public string Title = "";
        public int Left, Top, Width, Height;
        public override string ToString() { return string.Format("{0} [{1}x{2} at {3},{4}] hwnd=0x{5:X}", Title, Width, Height, Left, Top, Handle.ToInt64()); }
    }

    public static class Native
    {
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT r, int size);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; }

        public static void MakeDpiAware() { SetProcessDpiAwarenessContext(new IntPtr(-4)); }

        public static List<WindowInfo> Windows(int[] pids)
        {
            var set = new HashSet<int>(pids);
            var list = new List<WindowInfo>();
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                int pid; GetWindowThreadProcessId(h, out pid);
                if (!set.Contains(pid)) return true;
                var sb = new StringBuilder(512); GetWindowText(h, sb, 512);
                var r = Bounds(h);
                if (r.R - r.L < 50 || r.B - r.T < 50) return true;
                list.Add(new WindowInfo { Handle = h, ProcessId = pid, Title = sb.ToString(), Left = r.L, Top = r.T, Width = r.R - r.L, Height = r.B - r.T });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        // 影を含まない見た目どおりの枠（DWM）。取れなければ GetWindowRect。
        static RECT Bounds(IntPtr h)
        {
            RECT r;
            if (DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out r, Marshal.SizeOf(typeof(RECT))) != 0) GetWindowRect(h, out r);
            return r;
        }

        // [影込みの GetWindowRect の L,T,R,B, 見た目の枠（DWM）の L,T,R,B]。撮るのは PowerShell 側（System.Drawing は Add-Type から参照しにくい）。
        public static int[] Rects(IntPtr h)
        {
            RECT wr; GetWindowRect(h, out wr);
            var b = Bounds(h);
            return new[] { wr.L, wr.T, wr.R, wr.B, b.L, b.T, b.R, b.B };
        }

        public static bool PrintTo(IntPtr h, IntPtr hdc) { return PrintWindow(h, hdc, 2 /* PW_RENDERFULLCONTENT：WebView2 も写る */); }

        public static void Foreground(IntPtr h, int pid)
        {
            if (IsIconic(h)) ShowWindow(h, 9 /* SW_RESTORE */);
            var fg = GetForegroundWindow();
            int dummy;
            var fgThread = GetWindowThreadProcessId(fg, out dummy);
            var me = GetCurrentThreadId();
            AttachThreadInput(me, fgThread, true);
            SetForegroundWindow(h);
            AttachThreadInput(me, fgThread, false);
            Thread.Sleep(150);
            EnsureForeground(pid);
        }

        public static void EnsureForeground(int pid)
        {
            int fgPid; GetWindowThreadProcessId(GetForegroundWindow(), out fgPid);
            if (fgPid != pid)
                throw new InvalidOperationException("前面の窓が Loomo ではない（pid " + fgPid + "）。誤爆を避けるため入力を送らない。");
        }

        public static void Click(int x, int y, bool right, bool dbl)
        {
            SetCursorPos(x, y);
            Thread.Sleep(60);
            uint down = right ? 0x0008u : 0x0002u, up = right ? 0x0010u : 0x0004u;
            for (var i = 0; i < (dbl ? 2 : 1); i++)
            {
                Send(MouseInput(down)); Thread.Sleep(30); Send(MouseInput(up)); Thread.Sleep(60);
            }
        }

        public static void Move(int x, int y) { SetCursorPos(x, y); }

        /// <summary>押したまま動かして離す（OLE のドラッグ＆ドロップも通るよう、刻んで動かし、離す前に待つ）。</summary>
        public static void Drag(int x1, int y1, int x2, int y2, bool shift)
        {
            SetCursorPos(x1, y1);
            Thread.Sleep(80);
            Send(MouseInput(0x0002));
            Thread.Sleep(80);
            const int steps = 24;
            for (var i = 1; i <= steps; i++)
            {
                SetCursorPos(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps);
                Send(MouseInput(0x0001));
                Thread.Sleep(20);
            }
            if (shift) { Send(Key(0x10, 0, 0)); Thread.Sleep(60); Send(MouseInput(0x0001)); }
            Thread.Sleep(300);
            Send(MouseInput(0x0004));
            if (shift) Send(Key(0x10, 0, 2));
            Thread.Sleep(120);
        }

        static INPUT MouseInput(uint flags) { var i = new INPUT { type = 0 }; i.u.mi.dwFlags = flags; return i; }
        static INPUT Key(ushort vk, ushort scan, uint flags) { var i = new INPUT { type = 1 }; i.u.ki.wVk = vk; i.u.ki.wScan = scan; i.u.ki.dwFlags = flags; return i; }
        static void Send(params INPUT[] inputs) { SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT))); }

        public static void Chord(ushort[] modifiers, ushort vk)
        {
            foreach (var m in modifiers) Send(Key(m, 0, 0));
            Send(Key(vk, 0, 0)); Send(Key(vk, 0, 2));
            for (var i = modifiers.Length - 1; i >= 0; i--) Send(Key(modifiers[i], 0, 2));
            Thread.Sleep(60);
        }

        public static void Text(string s)
        {
            foreach (var ch in s)
            {
                Send(Key(0, ch, 4 /* UNICODE */), Key(0, ch, 4 | 2));
                Thread.Sleep(10);
            }
        }
    }
}
'@
}
[LoomoUi.Native]::MakeDpiAware()

function Get-LoomoWindow {
    <# Loomo（sk0ya.Loomo.App）の見えている窓。-Title は部分一致（既定は全部）。 #>
    param([string]$Title)
    $pids = @(Get-Process -Name 'sk0ya.Loomo.App' -ErrorAction SilentlyContinue | ForEach-Object Id)
    if ($pids.Count -eq 0) { throw 'Loomo が起動していない（sk0ya.Loomo.App のプロセスが無い）。' }
    $all = [LoomoUi.Native]::Windows([int[]]$pids)
    if ($Title) { $all = @($all | Where-Object { $_.Title -like "*$Title*" }) }
    return $all
}

function Resolve-LoomoWindow([string]$Title) {
    $w = @(Get-LoomoWindow -Title $Title)
    if ($w.Count -eq 0) { throw "窓 '$Title' が見つからない。Get-LoomoWindow で一覧を見る。" }
    if ($w.Count -gt 1 -and $Title) {
        $exact = @($w | Where-Object Title -eq $Title)
        if ($exact.Count -eq 1) { return $exact[0] }
    }
    # 主窓（タイトル末尾が「Loomo」）を優先する
    $main = @($w | Where-Object { $_.Title -like '*Loomo' })
    if ($main.Count -ge 1 -and -not $Title) { return $main[0] }
    return $w[0]
}

function Save-LoomoShot {
    <# 窓を PNG に撮る。前面化しない（人の作業を邪魔しない）。画像の px がクリック座標になる。 #>
    param([string]$Title, [Parameter(Mandatory)][string]$Path)
    $w = Resolve-LoomoWindow $Title
    $full = [System.IO.Path]::GetFullPath($Path)
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($full)) | Out-Null
    Add-Type -AssemblyName System.Drawing
    $r = [LoomoUi.Native]::Rects($w.Handle)
    $full_bmp = [System.Drawing.Bitmap]::new($r[2] - $r[0], $r[3] - $r[1])
    try {
        $g = [System.Drawing.Graphics]::FromImage($full_bmp)
        $hdc = $g.GetHdc()
        [void][LoomoUi.Native]::PrintTo($w.Handle, $hdc)
        $g.ReleaseHdc($hdc); $g.Dispose()
        # GetWindowRect は見えない影の分だけ広いので、見た目の枠に切り詰める（クリック座標と原点を揃える）。
        $crop = [System.Drawing.Rectangle]::new($r[4] - $r[0], $r[5] - $r[1], $r[6] - $r[4], $r[7] - $r[5])
        $cut = $full_bmp.Clone($crop, $full_bmp.PixelFormat)
        $cut.Save($full, [System.Drawing.Imaging.ImageFormat]::Png); $cut.Dispose()
    } finally { $full_bmp.Dispose() }
    [pscustomobject]@{ Path = $full; Window = $w.Title; Width = $w.Width; Height = $w.Height }
}

function Find-LoomoElement {
    <# UIA で名前（部分一致）から要素を探し、窓内の座標（画像の px）を返す。WebView2 の中や素の ItemsControl の行は出ない。 #>
    param([string]$Title, [Parameter(Mandatory)][string]$Name, [int]$Max = 20)
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $w = Resolve-LoomoWindow $Title
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($w.Handle)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $hits = foreach ($e in $all) {
        $n = $e.Current.Name
        if ($n -and $n -like "*$Name*") {
            $r = $e.Current.BoundingRectangle
            if ($r.IsEmpty) { continue }
            [pscustomobject]@{
                Name = $n; Type = $e.Current.ControlType.ProgrammaticName -replace '^ControlType\.', ''
                X = [int]($r.X - $w.Left); Y = [int]($r.Y - $w.Top); Width = [int]$r.Width; Height = [int]$r.Height
                CenterX = [int]($r.X - $w.Left + $r.Width / 2); CenterY = [int]($r.Y - $w.Top + $r.Height / 2)
            }
        }
    }
    @($hits | Select-Object -First $Max)
}

function Invoke-LoomoClick {
    <# 窓内の座標（Save-LoomoShot の画像の px）か、-Name（UIA の名前）の中央を実クリックする。 #>
    param([string]$Title, [int]$X = -1, [int]$Y = -1, [string]$Name, [switch]$Right, [switch]$Double)
    $w = Resolve-LoomoWindow $Title
    if ($Name) {
        $hit = @(Find-LoomoElement -Title $Title -Name $Name -Max 2)
        if ($hit.Count -eq 0) { throw "'$Name' が UIA に見つからない（WebView2 の中・素の ItemsControl なら座標で）。" }
        if ($hit.Count -gt 1) { Write-Warning "'$Name' が複数ある。最初の $($hit[0].Type) を押す。" }
        $X = $hit[0].CenterX; $Y = $hit[0].CenterY
    }
    if ($X -lt 0 -or $Y -lt 0) { throw '-X/-Y か -Name を指定する。' }
    [LoomoUi.Native]::Foreground($w.Handle, $w.ProcessId)
    [LoomoUi.Native]::Click($w.Left + $X, $w.Top + $Y, [bool]$Right, [bool]$Double)
}

function Move-LoomoPointer {
    <# ホバーだけ（ツールチップ・ホバー強調を見る）。前面化はしない。 #>
    param([string]$Title, [Parameter(Mandatory)][int]$X, [Parameter(Mandatory)][int]$Y)
    $w = Resolve-LoomoWindow $Title
    [LoomoUi.Native]::Move($w.Left + $X, $w.Top + $Y)
}

function Invoke-LoomoDrag {
    <# 窓内の座標（Save-LoomoShot の画像の px）から別の座標へ、押したまま動かして離す。-Shift で離す前に Shift を押す。 #>
    param([string]$Title, [Parameter(Mandatory)][int]$FromX, [Parameter(Mandatory)][int]$FromY,
          [Parameter(Mandatory)][int]$ToX, [Parameter(Mandatory)][int]$ToY, [switch]$Shift)
    $w = Resolve-LoomoWindow $Title
    [LoomoUi.Native]::Foreground($w.Handle, $w.ProcessId)
    [LoomoUi.Native]::EnsureForeground($w.ProcessId)
    [LoomoUi.Native]::Drag($w.Left + $FromX, $w.Top + $FromY, $w.Left + $ToX, $w.Top + $ToY, [bool]$Shift)
}

function Send-LoomoKeys {
    <# -Keys 'Ctrl+Shift+P' / 'Esc' / 'Tab' のような組み合わせ、または -Text で文字列（IME を通さず Unicode で）を送る。 #>
    param([string]$Title, [string]$Keys, [string]$Text)
    $w = Resolve-LoomoWindow $Title
    [LoomoUi.Native]::Foreground($w.Handle, $w.ProcessId)
    if ($Keys) {
        foreach ($chord in ($Keys -split '\s+')) {
            $parts = $chord -split '\+'
            $mods = [System.Collections.Generic.List[uint16]]::new()
            foreach ($m in $parts[0..($parts.Count - 2)]) {
                if ($parts.Count -lt 2) { break }
                switch ($m.ToLower()) { 'ctrl' { $mods.Add(0x11) } 'shift' { $mods.Add(0x10) } 'alt' { $mods.Add(0x12) } 'win' { $mods.Add(0x5B) } default { throw "知らない修飾キー: $m" } }
            }
            $k = $parts[-1]
            $vk = switch ($k.ToLower()) {
                'esc' { 0x1B } 'enter' { 0x0D } 'tab' { 0x09 } 'space' { 0x20 } 'back' { 0x08 } 'delete' { 0x2E }
                'up' { 0x26 } 'down' { 0x28 } 'left' { 0x25 } 'right' { 0x27 } 'home' { 0x24 } 'end' { 0x23 }
                'pageup' { 0x21 } 'pagedown' { 0x22 } 'f6' { 0x75 } 'f2' { 0x71 } 'f5' { 0x74 } 'f12' { 0x7B }
                default {
                    if ($k.Length -eq 1) { [int][char]$k.ToUpper() }
                    elseif ($k -match '^[Ff](\d{1,2})$') { 0x6F + [int]$Matches[1] }
                    else { throw "知らないキー: $k" }
                }
            }
            [LoomoUi.Native]::EnsureForeground($w.ProcessId)
            [LoomoUi.Native]::Chord($mods.ToArray(), [uint16]$vk)
        }
    }
    if ($Text) {
        [LoomoUi.Native]::EnsureForeground($w.ProcessId)
        [LoomoUi.Native]::Text($Text)
    }
}

Export-ModuleMember -Function Get-LoomoWindow, Save-LoomoShot, Find-LoomoElement, Invoke-LoomoClick, Invoke-LoomoDrag, Move-LoomoPointer, Send-LoomoKeys
