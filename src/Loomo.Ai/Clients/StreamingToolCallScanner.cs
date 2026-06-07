using System;
using System.Collections.Generic;
using System.Text;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// モデル出力の<b>ストリーム中</b>に、ツール呼び出しの JSON 配列
/// <c>[{"name":…,"arguments":{…}}, …]</c> からトップレベルのオブジェクトが閉じるたびに
/// <see cref="ToolUse"/> を取り出す逐次パーサ。全文確定を待たずに「実行できるツールから実行する」ための早期検知に使う。
///
/// <para>動作: チャンクを <see cref="Feed"/> で食わせると、そのチャンクで新たに完成したツール呼び出しを返す。
/// 文字列リテラルを意識した括弧マッチで、配列直下の <c>{…}</c> 境界を検出する
/// （<see cref="ToolCallTextParser.TryExtractFirstObject"/> と同方針）。各オブジェクト文字列は
/// <see cref="ToolCallTextParser.Parse"/> に通して 1 ツールへ変換する（別名キー・arguments/parameters ラップ等の
/// 揺れ吸収は同パーサに集約）。</para>
///
/// <para>適用条件: <b>最初の非空白文字が <c>[</c> のときだけ</b>有効化する（Phi-4-mini のツール呼び出し形式）。
/// それ以外（<c>{…}</c> 単体・<c>run_powershell(...)</c>・コードフェンス・先頭欠落の復元形・通常テキスト）は
/// <see cref="Disabled"/> にして何も返さず、リスクの高い復元・サルベージは終端の
/// <see cref="ToolCallTextParser.Parse"/>（実績あり）に委ねる。</para>
///
/// <para>不正要素の扱い: あるオブジェクトが解釈不能（<see cref="ToolCallTextParser.Parse"/> が空）なら、そこで
/// 走査を止め以降を捨てる（＝先頭から連続する正しいツールだけを活かす）。配列のトップレベル <c>]</c> を見ても
/// 走査を終える（後続テキストは無視）。</para>
/// </summary>
public sealed class StreamingToolCallScanner
{
    private readonly StringBuilder _buf = new();
    private int _pos;                 // _buf 内の次に処理する位置
    private bool _decided;            // 配列モードか否かを判定済みか
    private bool _stopped;            // 走査終了（配列を閉じた／不正要素で打ち切り／非配列で無効化）

    private bool _inString;
    private bool _esc;
    private int _objectDepth;
    private int _objStart = -1;       // 現在のトップレベルオブジェクト開始位置（無ければ -1）

    /// <summary>これまでに検出して返したツール呼び出しの総数。0 なら配列モードで拾えなかった
    /// （＝終端の <see cref="ToolCallTextParser.Parse"/> に判定を委ねるべき）ことを意味する。</summary>
    public int EmittedCount { get; private set; }

    /// <summary>ツール呼び出し配列を開く <c>[</c> の絶対位置（全文先頭からのインデックス）。配列モードに
    /// 入っていなければ -1。手前は空白のみのはずだが、配列外テキストの境界を測るために公開する。</summary>
    public int JsonStartIndex { get; private set; } = -1;

    /// <summary>トップレベル配列を閉じた <c>]</c> の<b>次</b>の絶対位置（end-exclusive）。配列を綺麗に閉じて
    /// いない（未完／不正要素で打ち切り）場合は -1。これが 0 以上のときだけ、配列の外側に書かれたテキストを
    /// 確定本文として安全に取り出せる（途中の壊れた残骸を本文へ混ぜないため）。</summary>
    public int JsonEndIndex { get; private set; } = -1;

    /// <summary>チャンクを与え、そのチャンクで新たに完成したツール呼び出しを順序どおり返す。</summary>
    public IReadOnlyList<ToolUse> Feed(string chunk)
    {
        if (_stopped || string.IsNullOrEmpty(chunk)) return Array.Empty<ToolUse>();
        _buf.Append(chunk);

        if (!_decided && !DetermineMode()) return Array.Empty<ToolUse>();

        List<ToolUse>? results = null;
        for (; _pos < _buf.Length; _pos++)
        {
            var c = _buf[_pos];
            if (_inString)
            {
                if (_esc) _esc = false;
                else if (c == '\\') _esc = true;
                else if (c == '"') _inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    _inString = true;
                    break;
                case '{':
                    if (_objectDepth == 0) _objStart = _pos;
                    _objectDepth++;
                    break;
                case '}':
                    _objectDepth--;
                    if (_objectDepth == 0 && _objStart >= 0)
                    {
                        var objStr = _buf.ToString(_objStart, _pos - _objStart + 1);
                        _objStart = -1;
                        var parsed = ToolCallTextParser.Parse(objStr);
                        if (parsed.Count == 0)
                        {
                            // 不正なオブジェクト：以降を捨てて打ち切る（先頭の正しい連続分だけ活かす）。
                            _stopped = true;
                            return (IReadOnlyList<ToolUse>?)results ?? Array.Empty<ToolUse>();
                        }
                        (results ??= new List<ToolUse>()).AddRange(parsed);
                        EmittedCount += parsed.Count;
                    }
                    break;
                case ']':
                    if (_objectDepth == 0)
                    {
                        // トップレベル配列を閉じた：走査終了。後続テキストはツール抽出には使わないが、
                        // 境界（end-exclusive）を記録して、呼び出し側が配列外の自然文を確定本文として拾えるようにする。
                        JsonEndIndex = _pos + 1;
                        _stopped = true;
                        _pos++;
                        return (IReadOnlyList<ToolUse>?)results ?? Array.Empty<ToolUse>();
                    }
                    break;
            }
        }
        return (IReadOnlyList<ToolUse>?)results ?? Array.Empty<ToolUse>();
    }

    /// <summary>最初の非空白文字を調べて配列モードか判定する。確定できたら true。
    /// まだ空白しか無ければ false（次のチャンクを待つ）。<c>[</c> 以外なら無効化する。</summary>
    private bool DetermineMode()
    {
        while (_pos < _buf.Length && char.IsWhiteSpace(_buf[_pos])) _pos++;
        if (_pos >= _buf.Length) return false;        // まだ空白のみ

        if (_buf[_pos] != '[')
        {
            _stopped = true;                          // 配列でない → 終端パーサに委ねる
            return false;
        }
        JsonStartIndex = _pos;                        // 開き '[' の絶対位置（配列外テキストの境界に使う）
        _decided = true;
        _pos++;                                       // 開き '[' を消費
        return true;
    }
}
