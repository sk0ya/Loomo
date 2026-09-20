using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Ai.Clients;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;
/// <summary>AiBarViewModel のコマンドパート：入力履歴（↑/↓）、スラッシュコマンド（/model・/clear・/resume）、
/// コマンド補完ポップアップ、入力欄の差し替え。</summary>
public sealed partial class AiBarViewModel
{

    // ===== 入力履歴（↑/↓ で呼び出し） =====

    /// <summary>送信したプロンプトを履歴に積む（直前と同一なら積まない）。</summary>
    private void PushHistory(string text) => _inputHistory.Push(text);

    /// <summary>↑：ひとつ前の入力履歴を呼び出す。履歴があれば true（キーを消費）。</summary>
    public bool RecallPreviousHistory()
    {
        if (!_inputHistory.RecallPrevious(Input, out var recalled)) return false;
        if (recalled is not null) SetInput(recalled);
        return true;
    }

    /// <summary>↓：ひとつ後の入力履歴（末尾を超えたら下書きへ戻す）。ナビ中なら true。</summary>
    public bool RecallNextHistory()
    {
        if (!_inputHistory.RecallNext(out var recalled)) return false;
        SetInput(recalled!);
        return true;
    }

    // ===== スラッシュコマンド =====

    /// <summary>入力がスラッシュコマンドなら実行して true。未知の「/...」は通常送信に委ねる。</summary>
    private bool TryRunChatCommand(string text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '/') return false;
        var name = text.Split(' ', 2)[0].ToLowerInvariant();
        switch (name)
        {
            case "/model": RunModelCommand(text); return true;
            case "/clear": ClearSession(); return true;
            case "/resume": ResumeLastSession(); return true;
            default: return false; // 既知コマンドでなければ通常メッセージとして送る
        }
    }

    /// <summary>/model コマンド。引数なしで一覧（現在のモデルに●）、引数ありで切替える。
    /// モデル状態は <see cref="SettingsViewModel"/> を単一の真実として共有し、切替は次のターンから効く。</summary>
    private void RunModelCommand(string text)
    {
        IsExpanded = true;
        _settingsVm.EnsureModelsLoaded();   // ローカルのモデル一覧を最新化（同期的に埋まる）
        var models = _settingsVm.AvailableModels.ToList();
        var current = _settingsVm.Model;
        var arg = text.Length > "/model".Length ? text["/model".Length..].Trim() : "";

        if (arg.Length == 0)
        {
            if (models.Count == 0)
            {
                Add(EntryKind.Info, "🧩 モデル",
                    "ローカルにモデルがありません。設定（⚙）でダウンロード／追加してください。");
                return;
            }
            // クリックで切替できる一覧を出す（● が現在のモデル）。/model <名前> でも切替可能。
            var entry = Add(EntryKind.Info, "🧩 モデル", "クリックで切替できます（● が現在のモデル）。");
            entry.SetModelChoices(models, current, SelectModelByName);
            return;
        }

        // 完全一致を優先し、無ければ部分一致（先頭から1件）で拾う。
        var match = models.FirstOrDefault(m => string.Equals(m, arg, StringComparison.OrdinalIgnoreCase))
            ?? models.FirstOrDefault(m => m.Contains(arg, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            Add(EntryKind.Error, "モデルが見つかりません",
                models.Count == 0
                    ? $"'{arg}' に一致するモデルがありません（ローカルにモデルがありません）。"
                    : $"'{arg}' に一致するモデルがありません。候補: {string.Join(", ", models)}");
            return;
        }

        SelectModelByName(match);
    }

    /// <summary>モデルを切替える（一覧クリック・/model 名前 で共用）。状態は SettingsViewModel が持ち、
    /// パス解決と settings.json 永続化を行う（OnModelChanged）。適用は次のターンから。</summary>
    private void SelectModelByName(string name)
    {
        _settingsVm.Model = name;
        Add(EntryKind.Info, "🧩 モデルを切替えました", $"{name}（次のターンから適用されます）");
    }

    /// <summary>入力内容に応じてコマンド補完候補を更新する。</summary>
    private void UpdateCommandSuggestions(string value)
    {
        // 「/」始まりで、まだ空白を含まない（=コマンド名入力中）のときだけ候補を出す。
        if (value.Length > 0 && value[0] == '/' && !value.Contains(' '))
        {
            var matches = AllCommands
                .Where(c => c.Name.StartsWith(value, StringComparison.OrdinalIgnoreCase))
                .ToList();

            CommandSuggestions.Clear();
            foreach (var c in matches) CommandSuggestions.Add(c);
            IsCommandPopupOpen = matches.Count > 0;
            SelectedCommandIndex = matches.Count > 0 ? 0 : -1;
        }
        else
        {
            CloseCommandPopup();
        }
    }

    public void CloseCommandPopup()
    {
        IsCommandPopupOpen = false;
        CommandSuggestions.Clear();
        SelectedCommandIndex = -1;
    }

    /// <summary>補完候補の選択を上下に移動する（端でラップ）。</summary>
    public void MoveCommandSelection(int delta)
    {
        if (CommandSuggestions.Count == 0) return;
        var i = SelectedCommandIndex + delta;
        if (i < 0) i = CommandSuggestions.Count - 1;
        if (i >= CommandSuggestions.Count) i = 0;
        SelectedCommandIndex = i;
    }

    private ChatCommand? CurrentSuggestion()
        => SelectedCommandIndex >= 0 && SelectedCommandIndex < CommandSuggestions.Count
            ? CommandSuggestions[SelectedCommandIndex]
            : null;

    /// <summary>選択中の候補で入力を補完する（Tab）。実行はしない。補完したら true。</summary>
    public bool CompleteSelectedCommand()
    {
        if (CurrentSuggestion() is not { } cmd) return false;
        SetInput(cmd.Name + " ");
        CloseCommandPopup();
        return true;
    }

    /// <summary>選択中の候補を確定して実行する（Enter / クリック）。実行したら true。</summary>
    public bool AcceptAndRunSelectedCommand()
    {
        if (CurrentSuggestion() is not { } cmd) return false;
        CloseCommandPopup();
        SetInput(cmd.Name);
        if (SendCommand.CanExecute(null)) SendCommand.Execute(null);
        return true;
    }

    /// <summary>補完を発火させずに入力欄を書き換える。</summary>
    private void SetInput(string value)
    {
        _suppressSuggestions = true;
        Input = value;
        _suppressSuggestions = false;
    }
}

