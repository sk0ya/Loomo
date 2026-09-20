using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.App.Views;

/// <summary>シグネチャ変更ダイアログの1行。<see cref="OriginalIndex"/> は元の並びでの位置で、
/// 並べ替えても持ち回る——これが「どの実引数を運ぶか」の唯一の手がかり。</summary>
public sealed partial class SignatureParameterRowVm : ObservableObject
{
    public SignatureParameterRowVm(int originalIndex, SignatureParameter parameter)
    {
        OriginalIndex = originalIndex;
        _modifiers = parameter.Modifiers;
        _type = parameter.Type;
        _name = parameter.Name;
        _defaultValue = parameter.DefaultValue ?? "";
    }

    public int OriginalIndex { get; }
    public bool IsNew => OriginalIndex == SignatureParameterChange.Added;

    [ObservableProperty] private string _modifiers;
    [ObservableProperty] private string _type;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _defaultValue;

    /// <summary>追加したパラメーターに対して、既存の呼び出し元へ書き込む式。</summary>
    [ObservableProperty] private string _callSiteArgument = "";

    public SignatureParameterChange ToChange() => new(
        OriginalIndex,
        new SignatureParameter(
            Name.Trim(), Type.Trim(), Modifiers.Trim(),
            DefaultValue.Trim() is { Length: > 0 } value ? value : null),
        CallSiteArgument.Trim() is { Length: > 0 } argument ? argument : null);
}

/// <summary>
/// C# の「シグネチャの変更」を組み立てるモーダルダイアログ（設計書 §32.5）。
/// 並べ替え・改名・型変更・既定値・追加・削除だけを扱い、**実際の書き換えは行わない**——
/// 宣言と呼び出し元の編集は <see cref="CSharpSignatureRefactoring"/> が作る。
/// </summary>
public partial class ChangeSignatureDialog : Window
{
    private readonly ObservableCollection<SignatureParameterRowVm> _rows;
    private readonly MethodSignature _signature;

    public ChangeSignatureDialog(MethodSignature signature)
    {
        InitializeComponent();
        _signature = signature;
        _rows = [.. signature.Parameters.Select((p, i) => new SignatureParameterRowVm(i, p))];

        CurrentSignatureText.Text = signature.Display;
        ReturnTypeBox.Text = signature.ReturnType;
        // コンストラクターに戻り値型は無い。空欄を編集させて無効な入力を作らせない。
        ReturnTypeBox.IsEnabled = !signature.IsConstructor;
        ParameterList.DataContext = _rows;
    }

    /// <summary>OK で確定した変更内容。キャンセル時は null。</summary>
    public SignatureChange? Result { get; private set; }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        if (ParameterList.SelectedItem is not SignatureParameterRowVm row) return;
        int index = _rows.IndexOf(row);
        int target = index + delta;
        if (target < 0 || target >= _rows.Count) return;
        _rows.Move(index, target);
        ParameterList.SelectedItem = row;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var row = new SignatureParameterRowVm(
            SignatureParameterChange.Added, new SignatureParameter("value", "int"));
        _rows.Add(row);
        ParameterList.SelectedItem = row;
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (ParameterList.SelectedItem is SignatureParameterRowVm row) _rows.Remove(row);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var change = new SignatureChange(
            _signature.IsConstructor ? "" : ReturnTypeBox.Text.Trim(),
            [.. _rows.Select(r => r.ToChange())]);

        if (Validate(change) is { } error)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Result = change;
        DialogResult = true;
    }

    /// <summary>「押してから失敗が返ってくる」のを減らすための手前の検証。
    /// 呼び出し元の書き換え可否は計画時（<see cref="CSharpSignatureRefactoring.PlanAsync"/>）にしか
    /// 分からないので、ここでは入力そのものの整合だけを見る。</summary>
    private static string? Validate(SignatureChange change)
    {
        foreach (var parameter in change.Parameters)
        {
            if (parameter.Parameter.Name.Length == 0) return "名前が空のパラメーターがあります。";
            if (parameter.Parameter.Type.Length == 0) return "型が空のパラメーターがあります。";
            if (parameter.IsNew &&
                parameter.CallSiteArgument is null &&
                parameter.Parameter.DefaultValue is null)
                return $"追加したパラメーター '{parameter.Parameter.Name}' には、既定値か呼び出し側の値のどちらかが必要です。";
        }
        var names = change.Parameters.Select(p => p.Parameter.Name).ToList();
        return names.Distinct(StringComparer.Ordinal).Count() != names.Count
            ? "パラメーター名が重複しています。"
            : null;
    }
}
