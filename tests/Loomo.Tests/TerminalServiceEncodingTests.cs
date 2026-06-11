using System;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// TerminalService の独立プロセス実行が日本語を化かさず往復できることの回帰ガード。
/// rg 等のネイティブツールは UTF-8 バイト列を stdout/stderr へ直接書くが、子シェルが
/// 既定の cp932 で誤デコードすると（特に stderr の）失敗理由が文字化けし、エージェントが
/// 自己修正できなくなる（ハーネス find-text の hard-fail 原因）。実プロセスを起動する
/// 数秒級のテストだが、エンコーディングの整合は単体では再現できないため実機で検証する。
/// </summary>
public sealed class TerminalServiceEncodingTests
{
    /// <summary>ネイティブツール相当：エンコード変換を経ない UTF-8 バイト列の直書きが
    /// stdout / stderr とも化けずに読めること（rg のエラー出力と同じ経路）。</summary>
    [Fact]
    public async Task Native_utf8_bytes_on_stdout_and_stderr_roundtrip()
    {
        var svc = new TerminalService();
        const string cmd =
            "$o=[System.Text.Encoding]::UTF8.GetBytes(\"標準出力の日本語`n\");" +
            "$s=[Console]::OpenStandardOutput();$s.Write($o,0,$o.Length);$s.Flush();" +
            "$e=[System.Text.Encoding]::UTF8.GetBytes(\"標準エラーの日本語`n\");" +
            "$t=[Console]::OpenStandardError();$t.Write($e,0,$e.Length);$t.Flush()";

        var result = await svc.RunCommandAsync(cmd, CancellationToken.None);

        Assert.True(result.Success, result.Output);
        Assert.Contains("標準出力の日本語", result.Output);
        Assert.Contains("標準エラーの日本語", result.Output);
    }

    /// <summary>失敗コマンドの stderr に含まれる日本語が読めること。Windows PowerShell 5.1 は
    /// エラーを cp932 で書くため、Utf8Preamble なしでは「�p�X...」状の文字化けになり、
    /// エージェントが失敗理由を読めず自己修正できなかった（ハーネス find-text の実敗因）。
    /// エラーメッセージ中にパスがエコーされることを使い、PS バージョン（5.1 日本語/7 英語）に
    /// 依存しない日本語文字列で判定する。</summary>
    [Fact]
    public async Task Failed_command_stderr_japanese_is_readable()
    {
        var svc = new TerminalService();
        var result = await svc.RunCommandAsync(
            "Get-Item 'C:\\存在しない日本語ファイル名.txt'", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("存在しない日本語ファイル名", result.Output);
    }

    /// <summary>PowerShell 自身の出力（Write-Output / コンソール stderr）も化けないこと。
    /// 子シェル側のエンコード（Utf8Preamble）と呼び出し側のデコード（StandardOutputEncoding）の
    /// 整合が崩れる退行を検知する。</summary>
    [Fact]
    public async Task Powershell_text_output_roundtrips_japanese()
    {
        var svc = new TerminalService();
        var result = await svc.RunCommandAsync(
            "Write-Output 'こんにちは世界'; [Console]::Error.WriteLine('エラー側も日本語')",
            CancellationToken.None);

        Assert.True(result.Success, result.Output);
        Assert.Contains("こんにちは世界", result.Output);
        Assert.Contains("エラー側も日本語", result.Output);
    }
}
