using System;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// エディタのガターが比べる相手（<see cref="EditorGutterBase"/>）を、共有の比較基準
/// （<see cref="GitCompareBaseViewModel"/>）から決めて持っておく。部屋の中で「何に対する変更か」は一つ——
/// Git パネルで「分岐点と比較」にすれば、変更ファイル一覧・差分・<b>エディタのガター</b>が同じ基準になる。
/// <para>ガターに載せるのは ref に解決できる基準（ブランチ・分岐点・リビジョン）だけ。作業ツリー基準は
/// 従来どおり HEAD。ワークツリー基準も HEAD のまま——相手の作業ツリーを固める（tree を作る）のは重く、
/// ガターは保存のたびに引き直すので、その都度固め直すわけにはいかない。</para>
/// <para>解決は fetch・コミットで動く（分岐点が変わる）ので、リポジトリの変化でも解決し直す。答えが
/// 変わったときだけ <see cref="Changed"/> を出す——保存のたびに全タブのガターを引き直さない。</para>
/// </summary>
public sealed class EditorGutterBaseTracker
{
    private readonly GitCompareBaseViewModel _compareBase;
    private readonly GitService _git;
    private EditorGutterBase? _current;
    private int _generation;

    public EditorGutterBaseTracker(GitCompareBaseViewModel compareBase, GitService git)
    {
        _compareBase = compareBase;
        _git = git;
        _compareBase.Changed += (_, _) => _ = ResolveAsync();
        _git.ActiveRootChanged += (_, _) => _ = ResolveAsync();
        _git.RepositoryChanged += (_, _) =>
        {
            if (Volatile.Read(ref _current) is not null || UsesRef(_compareBase.Selection.Kind))
                _ = ResolveAsync();
        };
    }

    /// <summary>ガターの比べる相手が変わった。開いているエディタはガターを引き直す。どのスレッドからも来る。</summary>
    public event EventHandler? Changed;

    /// <summary>いまの相手。null なら HEAD。エディタの背景スレッドから読まれる。</summary>
    public EditorGutterBase? Current => Volatile.Read(ref _current);

    /// <summary>ガターに載せる種別か（ref に解決できるもの）。</summary>
    public static bool UsesRef(GitCompareBaseKind kind) =>
        kind is GitCompareBaseKind.Branch or GitCompareBaseKind.MergeBase or GitCompareBaseKind.Revision;

    /// <summary>今の選択から相手を決め直す。後から始めた解決が勝つ（古い答えで上書きしない）。</summary>
    public async Task ResolveAsync()
    {
        var generation = Interlocked.Increment(ref _generation);
        var selection = _compareBase.Selection;
        var folder = _git.RootPath;
        EditorGutterBase? next = null;
        if (UsesRef(selection.Kind) && !string.IsNullOrEmpty(folder))
        {
            try
            {
                var resolution = await _git.ResolveCompareBaseAsync(selection);
                if (resolution is { BaseRef: { } baseRef, HasError: false })
                    next = new EditorGutterBase(folder, baseRef, resolution.Label);
            }
            catch (Exception)
            {
                // 解決に失敗したら HEAD のまま（ガターは補助表示で、理由は比較基準の UI が出す）。
            }
        }

        if (generation != Volatile.Read(ref _generation))
            return;
        var previous = Interlocked.Exchange(ref _current, next);
        if (previous != next)
            Changed?.Invoke(this, EventArgs.Empty);
    }
}
