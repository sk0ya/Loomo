using System.Windows;

namespace ViewHarness;

/// <summary>描く画面 1 つ分。実物の View と実物の VM（足りない依存だけスタブ）を組んで返す。
/// 新しいシーンは Scenes/ にこのインターフェースを実装したクラスを置くだけで <c>--scene 名前</c> で呼べる
/// （リフレクションで集める）。</summary>
public interface IScene
{
    /// <summary><c>--scene</c> に渡す名前（小文字・ハイフン区切り）。</summary>
    string Name { get; }

    /// <summary>何を見るためのシーンか（一覧表示用・1 行）。</summary>
    string Description { get; }

    /// <summary>描く大きさ（DIP）。実際の置き場所の大きさに合わせる（設定窓の中なら設定窓の中身の大きさ）。</summary>
    Size Size => new(1000, 700);

    /// <summary>描く要素を作る。テーマ・文字サイズはハーネスが当ててから呼ぶ。</summary>
    FrameworkElement Build();

    /// <summary>操作の手順。各手順のあとに撮り直して点検する（タブを切り替える・ピッカーを開く・
    /// 長い名前を入れる・エラーにする など）。<paramref name="root"/> は <see cref="Build"/> が返した要素。
    /// 状態は VM のコマンドや公開プロパティで動かす（合成マウスイベントは実機の検証にならない）。</summary>
    IEnumerable<SceneStep> Steps(FrameworkElement root) => [];
}

/// <summary>操作の 1 手順。<paramref name="Label"/> はファイル名にも使う（短く・空白なし）。</summary>
public sealed record SceneStep(string Label, Action Do);
