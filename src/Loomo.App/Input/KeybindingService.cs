using System;
using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.Ai;

namespace sk0ya.Loomo.App.Input;

/// <summary>1 コマンドの現在状態（設定画面・パレット表示用）。</summary>
/// <param name="Descriptor">コマンドのメタデータ。</param>
/// <param name="Effective">実効ジェスチャ（未割当なら null）。</param>
/// <param name="IsCustom">ユーザーが既定から変更しているか。</param>
/// <param name="ConflictId">同じジェスチャを持つ別コマンドの Id（競合）。無ければ null。</param>
public sealed record KeybindingRow(
    CommandDescriptor Descriptor,
    KeySequence? Effective,
    bool IsCustom,
    string? ConflictId);

/// <summary>
/// コマンド既定（<see cref="CommandCatalog"/>）にユーザー上書き（<see cref="AiSettings.Keybindings"/>）を
/// 重ねて「実効バインド」を解決する中央サービス。キーディスパッチ・コマンドパレット・設定画面が共有する。
/// 変更は共有 <see cref="AiSettings"/> へ書き戻して即永続化し、<see cref="Changed"/> で購読側へ通知する。
/// </summary>
public sealed class KeybindingService
{
    private readonly AiSettings _settings;
    private readonly AiSettingsStore _store;

    /// <summary>実効バインドが変わったとき（再割り当て・リセット）に発火する。</summary>
    public event Action? Changed;

    private Dictionary<string, KeySequence> _effective = new();

    public KeybindingService(AiSettings settings, AiSettingsStore store)
    {
        _settings = settings;
        _store = store;
        Rebuild();
    }

    /// <summary>実効バインド表（未割当コマンドは含めない）。Id → ジェスチャ。</summary>
    public IReadOnlyDictionary<string, KeySequence> Effective => _effective;

    /// <summary>コマンドの実効ジェスチャ（未割当なら null）。</summary>
    public KeySequence? For(string id) => _effective.TryGetValue(id, out var seq) ? seq : null;

    /// <summary>そのジェスチャに割り当たっているコマンド Id（無ければ null）。<paramref name="exceptId"/> は除外。</summary>
    public string? CommandAt(KeySequence sequence, string? exceptId = null)
    {
        foreach (var (id, seq) in _effective)
            if (!string.Equals(id, exceptId, StringComparison.Ordinal) && seq.Equals(sequence))
                return id;
        return null;
    }

    public bool IsCustom(string id) => _settings.Keybindings.Overrides.ContainsKey(id);

    /// <summary>設定画面用に全コマンドの現在状態を列挙する（カタログ順）。</summary>
    public IEnumerable<KeybindingRow> Rows()
    {
        foreach (var descriptor in CommandCatalog.All)
        {
            var effective = For(descriptor.Id);
            var conflict = effective is null ? null : CommandAt(effective, descriptor.Id);
            yield return new KeybindingRow(descriptor, effective, IsCustom(descriptor.Id), conflict);
        }
    }

    /// <summary>コマンドに新しいジェスチャを割り当てる。null は「未割当にする」。
    /// 既定と一致するなら上書きを消して既定へ戻す。永続化して <see cref="Changed"/> を発火する。</summary>
    public void Rebind(string id, KeySequence? sequence)
    {
        if (CommandCatalog.Find(id) is not { } descriptor) return;
        var defaultSeq = KeySequence.TryParse(descriptor.DefaultBinding);
        var overrides = _settings.Keybindings.Overrides;

        if (SequenceEquals(sequence, defaultSeq))
            overrides.Remove(id);                       // 既定と同じ → 上書き不要
        else if (sequence is null)
            overrides[id] = "";                         // 既定を明示的に外す（未割当）
        else
            overrides[id] = sequence.Format();

        Persist();
    }

    /// <summary>1 コマンドを既定へ戻す。</summary>
    public void Reset(string id)
    {
        if (_settings.Keybindings.Overrides.Remove(id))
            Persist();
    }

    /// <summary>すべてのコマンドを既定へ戻す。</summary>
    public void ResetAll()
    {
        if (_settings.Keybindings.Overrides.Count == 0) return;
        _settings.Keybindings.Overrides.Clear();
        Persist();
    }

    private void Persist()
    {
        Rebuild();
        try { _store.Save(_settings); } catch { /* 保存失敗でも in-memory は反映済み */ }
        Changed?.Invoke();
    }

    private void Rebuild()
    {
        var map = new Dictionary<string, KeySequence>(StringComparer.Ordinal);
        var overrides = _settings.Keybindings.Overrides;
        foreach (var descriptor in CommandCatalog.All)
        {
            KeySequence? seq;
            if (overrides.TryGetValue(descriptor.Id, out var custom))
                seq = KeySequence.TryParse(custom);     // 空・不正 → 未割当
            else
                seq = KeySequence.TryParse(descriptor.DefaultBinding);

            if (seq is not null)
                map[descriptor.Id] = seq;
        }
        _effective = map;
    }

    private static bool SequenceEquals(KeySequence? a, KeySequence? b)
        => a is null ? b is null : a.Equals(b);
}
