<#
.SYNOPSIS
  変更した XAML の「足した行」だけを見て、これまで何度も指摘された書き方を拾う（.claude/skills/ui-usability）。
.DESCRIPTION
  既存の違反（数十件ある）は対象外。今回の変更で増やしていないかだけを見る。
  - 生の FontSize（文字サイズ設定に追随しない。{DynamicResource Fs*} にする）
  - 直書きの色（#…・White 等。テーマに追随しない。パレットの鍵 {DynamicResource Fg/FgDim/Accent/Error/…} にする）
  - Style の無い <Button>（既定の白いボタンになる。FlatButton / SecondaryButton / IconButton …）
  - 文字の無い IconButton に ToolTip が無い
  - 固定の Width/Height を持つ TextBlock・Button（文字サイズを上げると欠ける。MinWidth か Auto に）
.PARAMETER Base
  比べる先（既定 HEAD＝コミット前の作業ツリーの変更）。コミット済みの範囲を見るなら 'HEAD~1' など。
.EXAMPLE
  pwsh tools/ui/Test-XamlChanges.ps1
  pwsh tools/ui/Test-XamlChanges.ps1 -Base HEAD~3
#>
param([string]$Base = 'HEAD')

Set-StrictMode -Version Latest
# git の出力（日本語のパス・行）を UTF-8 として読み、結果も UTF-8 で出す（既定の cp932 だと化ける）。
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$root = git rev-parse --show-toplevel
Push-Location $root
try {
    $files = @(@(git diff --name-only $Base -- '*.xaml') + @(git ls-files --others --exclude-standard -- '*.xaml') |
        Where-Object { $_ -and $_ -notmatch '/Themes/(Palette|Typography)' -and (Test-Path $_) } | Sort-Object -Unique)

    $rules = @(
        @{ Kind = '生の文字サイズ'; Pattern = 'FontSize="\d'; Hint = 'FontSize="{DynamicResource Fs13}" のようにトークンで' }
        @{ Kind = '直書きの色'; Pattern = '(Foreground|Background|BorderBrush|Fill|Stroke|Color)="(#[0-9A-Fa-f]{3,8}|White|Black|Red|Gray|Grey|Blue|Green|Yellow|Orange|LightGray|DarkGray)"'; Hint = 'パレットの鍵（Fg / FgDim / Accent / Error / Success / Border …）を DynamicResource で' }
        @{ Kind = '固定の幅・高さ'; Pattern = '<(TextBlock|Button|Label)\b[^>]*\s(Width|Height)="\d'; Hint = '文字サイズを上げると欠ける。MinWidth/MinHeight か Auto に（アイコンだけのボタンは除く）' }
    )

    $found = 0
    foreach ($f in $files) {
        $added = @()
        $isNew = -not (git ls-files -- $f)
        if ($isNew) {
            $n = 0
            $added = Get-Content -LiteralPath $f -Encoding utf8 | ForEach-Object { $n++; [pscustomobject]@{ Line = $n; Text = $_ } }
        } else {
            $cur = 0
            foreach ($l in (git diff -U0 $Base -- $f)) {
                if ($l -match '^@@ -\d+(?:,\d+)? \+(\d+)') { $cur = [int]$Matches[1]; continue }
                if ($l.StartsWith('+++')) { continue }
                if ($l.StartsWith('+')) { $added += [pscustomobject]@{ Line = $cur; Text = $l.Substring(1) }; $cur++ }
            }
        }
        if ($added.Count -eq 0) { continue }

        foreach ($a in $added) {
            foreach ($r in $rules) {
                if ($a.Text -match $r.Pattern) {
                    $found++
                    "{0}:{1}: [{2}] {3}`n    → {4}" -f $f, $a.Line, $r.Kind, $a.Text.Trim(), $r.Hint
                }
            }
        }

        # <Button …> の開始タグ単位で見る（複数行にまたがる属性に対応）。
        $text = ($added | ForEach-Object Text) -join "`n"
        foreach ($m in [regex]::Matches($text, '<Button\b(?<attrs>[^>]*)>', 'Singleline')) {
            $attrs = $m.Groups['attrs'].Value
            $first = ($added | Where-Object { $_.Text -match '<Button\b' } | Select-Object -First 1).Line
            if ($attrs -notmatch '\bStyle=') {
                $found++
                "{0}:~{1}: [スタイル漏れ] Style の無い Button`n    → FlatButton / SecondaryButton / IconButton / PaneHeaderToolButton … を当てる（style-gallery シーンで見比べる）" -f $f, $first
            }
            if ($attrs -match 'IconButton' -and $attrs -notmatch '\bToolTip=' -and $attrs -notmatch '\bContent="[^"]*\w') {
                $found++
                "{0}:~{1}: [名前なし] 文字の無いアイコンボタンに ToolTip が無い`n    → 何をするかを ToolTip に書く" -f $f, $first
            }
        }
    }

    # 見本・プレビューの中で「実物の px を再現する」ための直書き（WebView2 側の CSS を写す等）は、理由をコメントに書いて残してよい。
    if ($files.Count -eq 0) { '変更した XAML なし' }
    elseif ($found -eq 0) { "変更した XAML $($files.Count) 件：指摘なし" }
    else { "`n指摘 $found 件（変更した XAML $($files.Count) 件）" }
    exit ([int]($found -gt 0))
}
finally { Pop-Location }
