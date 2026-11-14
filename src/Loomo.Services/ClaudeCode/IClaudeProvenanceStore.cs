using System.Collections.Generic;
using sk0ya.Loomo.Core.Harness;

namespace sk0ya.Loomo.Services.ClaudeCode;

/// <summary>
/// 結んだ出自の置き場（§43.6 A2b）：コミット済みの行 → それを書いた頼みごと。Claude Code は古い記録を消す（既定 30 日）し、
/// 索引が見るのは直近 14 日だけなので、blame を引いて結べたときに小さく残しておく——記録が消えたあとも、その行の
/// 頼みごとの本文は出せる。鍵は blame の行と同じ（ファイル・短いハッシュ・そのコミットでの行番号）。
/// <para>呼ぶのは blame を引くスレッド（背景）。UI スレッドから呼ばないこと。失敗は握りつぶす（出自は無くても困らない飾り）。</para>
/// </summary>
public interface IClaudeProvenanceStore
{
    /// <summary>そのファイルについて覚えている行。返す <see cref="ClaudeWrite"/> は <see cref="ClaudeWrite.Stored"/>
    /// （足した行を持たない——頼みごとの本文・セッション・位置だけ）。</summary>
    IReadOnlyDictionary<(string Hash, int OriginalLine), ClaudeWrite> Load(string filePath);

    /// <summary>結べた行を覚える。同じ鍵は上書き。</summary>
    void Save(string filePath, IReadOnlyDictionary<(string Hash, int OriginalLine), ClaudeWrite> lines);
}
