using System.IO;
using System.Windows;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>コピー／移動時の同名競合を解決するモーダルダイアログ。</summary>
public partial class FileConflictDialog : Window
{
    private readonly FileConflictContext _context;

    private FileConflictDialog(FileConflictContext context)
    {
        InitializeComponent();
        _context = context;
        var operation = context.IsMove ? "移動" : "コピー";
        ConflictText.Text = $"{operation}: {context.SourcePath}\n既存: {context.DestinationPath}";
        NameBox.Text = Path.GetFileName(context.DestinationPath);
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    public FileConflictDecision Decision { get; private set; } = new(FileConflictAction.Cancel);

    public static FileConflictDecision Show(Window? owner, FileConflictContext context)
    {
        var dialog = new FileConflictDialog(context) { Owner = owner };
        _ = dialog.ShowDialog();
        return dialog.Decision;
    }

    private void SetDecision(FileConflictAction action, string? name = null)
    {
        Decision = FileConflictDecisionPolicy.Create(action, name, ApplyAllBox.IsChecked == true);
        DialogResult = true;
    }

    private void OnOverwrite(object sender, RoutedEventArgs e) => SetDecision(FileConflictAction.Overwrite);
    private void OnSkip(object sender, RoutedEventArgs e) => SetDecision(FileConflictAction.Skip);

    private void OnRename(object sender, RoutedEventArgs e)
    {
        var name = FileConflictDecisionPolicy.NormalizeRenameName(NameBox.Text);
        if (name is null)
        {
            NameBox.Focus();
            return;
        }
        SetDecision(FileConflictAction.Rename, name);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Decision = new FileConflictDecision(FileConflictAction.Cancel);
        DialogResult = false;
    }
}
