using System;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>「進行状況」エントリへ、永続化用テキストと表示用の段階タイムラインを同時に積むヘルパー。
/// チャット（<see cref="AiBarViewModel"/>）とワークフロー（<see cref="WorkflowViewModel"/>）で共有する。
/// 恒久ログ（<see cref="Append"/>）は <see cref="TranscriptEntry.Text"/>（保存対象）と
/// <see cref="TranscriptEntry.Steps"/>（表示）の両方を更新し、揮発プレビュー（<see cref="SetLive"/>）は
/// 末尾の「ライブ段」だけを差し替える＝保存テキストには一切残さない。</summary>
internal sealed class ActivityLog
{
    private readonly TranscriptEntry _entry;
    private readonly Func<TimeSpan> _elapsed;
    private ActivityStep? _live;

    public ActivityLog(TranscriptEntry entry, Func<TimeSpan> elapsed)
    {
        _entry = entry;
        _elapsed = elapsed;
    }

    /// <summary>恒久ログを1件積む（永続化テキスト＋タイムライン段）。直前のライブ段は畳んでから足す。</summary>
    public void Append(ActivityKind kind, string message)
        => Append(kind, message, "");

    /// <summary>恒久ログを1件積む。detail は表示専用の折りたたみ詳細で、保存テキストには含めない。</summary>
    public void Append(ActivityKind kind, string message, string detail)
    {
        ClearLive();
        var time = TranscriptFormatting.FormatDuration(_elapsed());
        var prefix = _entry.Text.Length == 0 ? "" : Environment.NewLine;
        _entry.Text += $"{prefix}[{time}] {message}";
        _entry.Steps.Add(ActivityStep.Create(kind, time, message, detail));
    }

    /// <summary>生成中などの揮発プレビューを末尾のライブ段として見せる（保存しない）。空文字なら消す。</summary>
    public void SetLive(ActivityKind kind, string message)
    {
        if (string.IsNullOrEmpty(message)) { ClearLive(); return; }
        var built = ActivityStep.Create(kind, "", message);
        if (_live is null)
        {
            _live = built;
            _live.IsLive = true;
            _entry.Steps.Add(_live);
        }
        else
        {
            _live.Message = built.Message;
        }
    }

    /// <summary>ライブ段を取り除く（恒久ログ追加前・ターン終了時に呼ぶ）。</summary>
    public void ClearLive()
    {
        if (_live is null) return;
        _entry.Steps.Remove(_live);
        _live = null;
    }
}
