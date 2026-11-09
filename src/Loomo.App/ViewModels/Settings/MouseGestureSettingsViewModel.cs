using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Settings;
using sk0ya.Loomo.Services.Settings;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>設定「ブラウザ」：マウスジェスチャー（§24.105）の ON/OFF と、向き → コマンドの表、
/// それに設定から足すスクリプトのコマンド（§24.106）。
/// 変えたらその場で <c>settings.json</c> へ書く（ジェスチャーもスクリプトも毎回設定を引くので、次の 1 回から効く）。</summary>
public sealed partial class MouseGestureSettingsViewModel : ObservableObject
{
    private readonly LoomoSettings _settings;
    private readonly SettingsStore? _store;
    private readonly KeybindingService? _keybindings;
    private bool _loading;

    public MouseGestureSettingsViewModel(LoomoSettings settings, SettingsStore? store, KeybindingService? keybindings = null)
    {
        _settings = settings;
        _store = store;
        _keybindings = keybindings;
        Load();
    }

    /// <summary>ジェスチャーに結べるコマンド（ブラウザのもの＋設定から足したスクリプト）。</summary>
    public ObservableCollection<GestureCommandChoice> Commands { get; } = new();

    /// <summary>設定から足したスクリプトのコマンド。</summary>
    public ObservableCollection<BrowserScriptRowViewModel> Scripts { get; } = new();

    [ObservableProperty]
    private string _newScriptTitle = "";

    [ObservableProperty]
    private string _newScriptBody = "";

    /// <summary>足すときに一緒に結ぶ向き（任意）。</summary>
    [ObservableProperty]
    private string _newScriptGesture = "";

    public ObservableCollection<MouseGestureRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    private bool _enabled;

    /// <summary>追加欄：向き（↓→ も DR も受ける）。</summary>
    [ObservableProperty]
    private string _newGesture = "";

    [ObservableProperty]
    private GestureCommandChoice? _newCommand;

    [ObservableProperty]
    private string _status = "";

    partial void OnEnabledChanged(bool value)
    {
        if (_loading || _settings.MouseGestures.Enabled == value) return;
        _settings.MouseGestures.Enabled = value;
        Persist(value ? "マウスジェスチャーを使います（次に開くページから）" : "マウスジェスチャーを使いません（次に開くページから）");
    }

    [RelayCommand]
    private void Add()
    {
        if (ParseGesture(NewGesture) is not { } gesture)
        {
            Status = "向きは ↑↓←→ か U/D/L/R で、8 画までで入れてください（例：↓→ / DR）。";
            return;
        }
        if (NewCommand is null)
        {
            Status = "コマンドを選んでください。";
            return;
        }
        _settings.MouseGestures.Bindings.Remove(ExistingKey(gesture) ?? gesture);
        _settings.MouseGestures.Bindings[gesture] = NewCommand.Id;
        NewGesture = "";
        Reload();
        Persist($"{BrowserMouseGesture.Arrows(gesture)} に「{NewCommand.Title}」を割り当てました");
    }

    [RelayCommand]
    private void Remove(MouseGestureRowViewModel? row)
    {
        if (row is null) return;
        _settings.MouseGestures.Bindings.Remove(row.Key);
        Reload();
        Persist($"{row.Arrows} の割り当てを外しました");
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        _settings.MouseGestures.Bindings = MouseGestureSettings.Defaults();
        Reload();
        Persist("既定のジェスチャーに戻しました");
    }

    [RelayCommand]
    private void AddScript()
    {
        if (string.IsNullOrWhiteSpace(NewScriptTitle) || string.IsNullOrWhiteSpace(NewScriptBody))
        {
            Status = "名前とスクリプトを入れてください。";
            return;
        }
        string? gesture = null;
        if (!string.IsNullOrWhiteSpace(NewScriptGesture) && (gesture = ParseGesture(NewScriptGesture)) is null)
        {
            Status = "向きは ↑↓←→ か U/D/L/R で、8 画までで入れてください（例：↓→ / DR）。";
            return;
        }
        var command = new BrowserScriptCommand { Id = BrowserScriptCommand.NewId(), Title = NewScriptTitle.Trim(), Script = NewScriptBody.Trim() };
        _settings.BrowserScripts.Add(command);
        if (gesture is not null)
        {
            _settings.MouseGestures.Bindings.Remove(ExistingKey(gesture) ?? gesture);
            _settings.MouseGestures.Bindings[gesture] = command.Id;
        }
        NewScriptTitle = NewScriptBody = NewScriptGesture = "";
        CommandsChanged();
        Persist(gesture is null
            ? $"コマンド「{command.Title}」を足しました。ジェスチャーやキーに結べます"
            : $"コマンド「{command.Title}」を足して {BrowserMouseGesture.Arrows(gesture)} に結びました");
    }

    [RelayCommand]
    private void RemoveScript(BrowserScriptRowViewModel? row)
    {
        if (row is null) return;
        _settings.BrowserScripts.RemoveAll(c => c.Id == row.Id);
        // 消したコマンドを指す割り当ては、ジェスチャーもキーも一緒に外す（宙に浮いた割り当てを残さない）。
        foreach (var key in _settings.MouseGestures.Bindings.Where(b => b.Value == row.Id).Select(b => b.Key).ToList())
            _settings.MouseGestures.Bindings.Remove(key);
        _settings.Keybindings.Overrides.Remove(row.Id);
        CommandsChanged();
        Persist($"コマンド「{row.Title}」を外しました");
    }

    /// <summary>スクリプトの行の名前・中身を書き換えた。</summary>
    internal void OnScriptEdited(BrowserScriptRowViewModel row)
    {
        if (_loading || _settings.BrowserScripts.FirstOrDefault(c => c.Id == row.Id) is not { } command) return;
        if (string.IsNullOrWhiteSpace(row.Title))
        {
            Status = "名前は空にできません。";
            return;
        }
        var retitled = command.Title != row.Title.Trim();
        command.Title = row.Title.Trim();
        command.Script = row.Script;
        if (retitled)
            CommandsChanged();
        Persist($"コマンド「{command.Title}」を保存しました");
    }

    /// <summary>コマンドの顔ぶれ・題が変わった：選択肢と表を組み直し、キーの割り当てにも知らせる。</summary>
    private void CommandsChanged()
    {
        Reload();
        _keybindings?.Refresh();
    }

    /// <summary>行のコマンドを選び替えた。</summary>
    internal void OnRowCommandChanged(MouseGestureRowViewModel row)
    {
        if (_loading || row.Command is null) return;
        _settings.MouseGestures.Bindings[row.Key] = row.Command.Id;
        Persist($"{row.Arrows} を「{row.Command.Title}」にしました");
    }

    /// <summary>入力欄の向き：矢印（↑↓←→）と U/D/L/R のどちらでも。空白・区切りは無視。</summary>
    public static string? ParseGesture(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var letters = new string(text
            .Where(c => !char.IsWhiteSpace(c) && c is not (',' or '、' or '-' or '>'))
            .Select(c => c switch { '↑' => 'U', '↓' => 'D', '←' => 'L', '→' => 'R', _ => c })
            .ToArray());
        return BrowserMouseGesture.Normalize(letters);
    }

    private string? ExistingKey(string gesture)
        => _settings.MouseGestures.Bindings.Keys.FirstOrDefault(k => BrowserMouseGesture.Normalize(k) == gesture);

    private void Load()
    {
        _loading = true;
        try
        {
            Enabled = _settings.MouseGestures.Enabled;
        }
        finally { _loading = false; }
        Reload();
    }

    private void Reload()
    {
        _loading = true;
        try
        {
            var selected = NewCommand?.Id;
            Commands.Clear();
            foreach (var c in CommandCatalog.WithUserCommands(_settings)
                         .Where(c => c.Id.StartsWith("browser.", StringComparison.Ordinal) || c.Id == "tab.newBrowser"))
                Commands.Add(new GestureCommandChoice(c.Id, c.Title));
            NewCommand = Commands.FirstOrDefault(c => c.Id == selected) ?? Commands.FirstOrDefault();

            Scripts.Clear();
            foreach (var script in _settings.BrowserScripts)
                Scripts.Add(new BrowserScriptRowViewModel(this, script.Id, script.Title, script.Script));

            Rows.Clear();
            foreach (var (key, id) in _settings.MouseGestures.Bindings
                         .OrderBy(b => b.Key.Length).ThenBy(b => b.Key, StringComparer.Ordinal))
            {
                var gesture = BrowserMouseGesture.Normalize(key);
                var choice = Commands.FirstOrDefault(c => c.Id == id) ?? new GestureCommandChoice(id, $"（不明なコマンド：{id}）");
                Rows.Add(new MouseGestureRowViewModel(this, key, gesture is null ? key : BrowserMouseGesture.Arrows(gesture), Commands, choice));
            }
        }
        finally { _loading = false; }
    }

    private void Persist(string message)
    {
        if (_store is null) { Status = message; return; }
        try
        {
            _store.Save(_settings);
            Status = message;
        }
        catch (Exception ex)
        {
            Status = $"保存に失敗しました: {ex.Message}";
        }
    }
}

/// <summary>スクリプトのコマンドの 1 行（名前とスクリプトはその場で書き換えられる）。</summary>
public sealed partial class BrowserScriptRowViewModel : ObservableObject
{
    private readonly MouseGestureSettingsViewModel _owner;

    public BrowserScriptRowViewModel(MouseGestureSettingsViewModel owner, string id, string title, string script)
    {
        _owner = owner;
        Id = id;
        _title = title;
        _script = script;
    }

    public string Id { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _script;

    partial void OnTitleChanged(string value) => _owner.OnScriptEdited(this);
    partial void OnScriptChanged(string value) => _owner.OnScriptEdited(this);
}

/// <summary>ジェスチャーに結べるコマンド 1 つ（コンボボックスの項目）。</summary>
public sealed record GestureCommandChoice(string Id, string Title);

/// <summary>表の 1 行：向き（矢印）とコマンド。</summary>
public sealed partial class MouseGestureRowViewModel : ObservableObject
{
    private readonly MouseGestureSettingsViewModel _owner;

    public MouseGestureRowViewModel(MouseGestureSettingsViewModel owner, string key, string arrows,
        IReadOnlyList<GestureCommandChoice> commands, GestureCommandChoice command)
    {
        _owner = owner;
        Key = key;
        Arrows = arrows;
        // 選択肢は写しを持つ——設定側の一覧は組み直しのたびに空にされるので、共有すると選択が外れる。
        Commands = commands.Contains(command) ? commands.ToList() : commands.Append(command).ToList();
        _command = command;
    }

    /// <summary>設定の表の鍵（書かれたまま）。</summary>
    public string Key { get; }
    public string Arrows { get; }
    public IReadOnlyList<GestureCommandChoice> Commands { get; }

    [ObservableProperty]
    private GestureCommandChoice? _command;

    partial void OnCommandChanged(GestureCommandChoice? value) => _owner.OnRowCommandChanged(this);
}
