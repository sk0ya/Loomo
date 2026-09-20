namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ピン留めフォルダーの<b>唯一の持ち主</b>への窓口。実装は <see cref="FolderTreeViewModel"/>
/// ただ1つで、DI では「自分自身」と「この interface」の両方に同じインスタンスを登録する
/// （CLAUDE.md の「具象＋interface の二重登録」と同じ手）。
///
/// <para>ファイル一覧ペイン（§26.10）にもピンの一覧・追加・解除が要るが、ピンは
/// <c>WorkspaceSnapshot.PinnedFolders</c>／<c>AdditionalFolders[].PinnedFolders</c> として
/// <b>ツリー側が永続化している</b>。持ち主を2つにすると「片方で留めたのに、もう片方には出ない」
/// が必ず起きる——LSP サーバー表を1つに寄せたのと同じ理由で、置き場所は増やさず窓口だけ開ける。</para></summary>
public interface IFolderPinStore
{
    /// <summary>全ワークスペースフォルダーぶんのピン（フルパス）。マルチルートでは
    /// フォルダーをまたいで集めたもの。</summary>
    IReadOnlyList<string> AllPins { get; }

    bool IsPinned(string fullPath);

    /// <summary>ピン留めできるか（ワークスペース配下の実在フォルダーで、まだ留めていない・
    /// ワークスペースフォルダー自身でもないとき）。ワークスペース外は対象外——ピンは
    /// ワークスペースの持ち物として保存されるため。外の場所は Windows のクイックアクセスで辿る。</summary>
    bool CanPin(string fullPath);

    void Pin(string fullPath);

    void Unpin(string fullPath);

    /// <summary>ピンの増減（表示を引き直すため）。</summary>
    event EventHandler? PinsChanged;
}
