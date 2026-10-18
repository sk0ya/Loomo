# 他ツールの新機能からの取り込み候補（2026-10-18・焦点：前回見ていない製品・期間：2026-04〜2026-10）

同じ日の全領域の調査（`feature-scout-2026-10-18.md`）で「見ていない」「要約しか読めなかった」とした製品を見た補足。
物差しは前回と同じ（主役は人間の部屋。§19.9 の「やらない」とクラウド必須の機能は外す）。
「いまの Loomo」はコードを grep して確かめた結果。

## 推す

> 2026-10-18 追記：2 を実装した（§24.31）。

### 1. 手で閉じたタブを開き直す（元：Fork for Windows 2.20・2026-06-05／Zen Browser 1.22.2b・2026-09-15）
- 出典：https://fork.dev/releasenoteswin （2.20「Restore last closed tab with Ctrl+Shift+T」）、
  https://github.com/zen-browser/desktop/releases （1.22.2b のコマンドバーに「reopen closed tab」）
- 元の機能：うっかり閉じたタブを 1 打で元に戻す。
- Loomo でやると：エディタとブラウザのタブを手で閉じたとき、§24.29 の棚が下げるときに残すのと同じ「場所」
  （ファイル＋キャレット＋スクロール／URL＋題）を、ワークスペースごとの「閉じたタブ」に積む。コマンド
  「閉じたタブを開き直す」で新しいものから順に戻し、TABS の棚と同じ並びに「最近閉じた」を出す。
  間違えて閉じても、読んでいた位置ごと戻せる（§24.4 復元の完全性）。
- いまの Loomo：**部分的にある**。自動で下げたタブは棚から戻せる（§24.29）が、手で閉じたタブは何も残らない。
  `CloseEditorTab`（`src/Loomo.App/Views/Shell/Navigation/ShellWindow.Tabs.cs:152`）と
  `CloseBrowserTabAsync`（`src/Loomo.App/Views/Shell/Browser/ShellWindow.Browser.cs:248`）はどちらも破棄して取り除くだけ。
  ファイルなら「最近のファイル」（`nav.recentFiles`）から開き直せるが、キャレットとスクロールは戻らない。
- 規模：S〜M（Loomo 内だけ。棚の行の型と「戻す」処理が使える。キーは要検討——`Ctrl+Shift+T` は
  `mode.toggle` が使っている。`src/Loomo.App/Input/CommandCatalog.cs:242`）
- 関連：§24.29、§24.4、§24.2
- 注意：ターミナルは対象外（閉じるとシェルを殺している・§34）。名前の無いタブは中身を持たないので積まない（棚と同じ）。

### 2. スタッシュの中身を見る・パッチとして書き出す（元：Fork for Windows 2.21・2026-07-16）
- 出典：https://fork.dev/releasenoteswin （2.21「Add Save as Patch option to stash context menu」）
- 元の機能：スタッシュの右クリックから、その変更を `.patch` ファイルとして保存する。
- Loomo でやると：Git ペインのスタッシュの行を押すと、Diff ペインで中身を開く（既存の左右差分・§24.16 で読む）。
  右クリックに「パッチとして保存」を足し、保存したファイルをエディタで開く。退避した作業の中身を、適用せずに読んだり
  ファイルとして渡したりできる（§24.3 素材の流れ：Git → ファイル）。
- いまの Loomo：**部分的にある**。スタッシュの一覧と適用・取り出し・削除はある
  （`src/Loomo.App/Views/Git/GitPanelView.xaml:567-582`、`src/Loomo.Services/Git/GitStashService.cs:51-57`）が、中身を見る道が無い。
  コミット一覧は `--all` で引いている（`src/Loomo.Services/Git/GitLogQuery.cs:79`）ので、いちばん新しいスタッシュだけは
  `stash` の印つきでコミットとして出るが、2 つ目以降は出ない。
- 規模：S（`git stash show -p --include-untracked <ref>` を Diff の取り込み口に渡すのと、ファイルへの書き出し）
- 関連：§24.5、§24.16、§24.3

## 検討したが見送り

| 機能 | 製品・版 | 見送り理由 |
|---|---|---|
| reset のときにローカルの変更を残す（`reset --keep`） | Sublime Merge Build 2131（2026-09-16） | 安くできる（`GitResetMode` は Soft／Mixed／Hard の 3 つだけ・`src/Loomo.Services/Git/GitOperationOptions.cs:4`）が、Git 操作を網羅する話で部屋の性質には効かない。Hard の取り違えを防ぐ安全策として足すなら別枠で |
| 複数コミットをチェリーピックするとき空のコミットを飛ばす | Sublime Merge Build 2126（2026-07-30） | 同上（Git 操作の網羅） |
| 積み重ねたブランチをまとめて push / pull する | lazygit 0.66.0（2026-10-05） | 価値はあるが `rebase --update-refs` の扱いから始める大きな話（L）。部屋への効き目は小さい |
| ワークツリーの一覧をブランチ名で絞る | lazygit 0.65.0（2026-09-05） | ワークツリーは数本のことが多く、絞る必要が薄い |
| 同じアプリのウィンドウ間を Alt+` で回る（Window Hopper） | PowerToys 0.101.2362.0（2026-08-25） | 切り離したペインのウィンドウ同士を回るのは OS 側（Alt+Tab・この機能そのもの）で足りる |
| 履歴・ダウンロード・閉じたタブ・スペースを 1 か所にまとめる「Library」 | Zen Browser 1.23b（2026-10-02） | 履歴とダウンロードは Loomo のブラウザに既にある（ツールバーのドロップダウン・`ShellWindow.BrowserChrome.cs:47`）。閉じたタブの部分は推す 1 に入れた |
| アドレスバーで計算する／PWA のインストール | Vivaldi 8.2（2026-09-03 頃） | 部屋に効かない |
| 外部のターミナルを開くコマンド | Sublime Text Build 4205（2026-04-23） | Loomo はターミナルを部屋の中に持っている |
| 画面の内容を AI に渡す・定期実行・自前モデル | Raycast for Windows 0.71〜2.8（2026-08〜10） | AI とクラウド（サブスクリプション）が主役 |
| クラウドのエージェント・Marketplace・スマホからの遠隔操作 | Cursor（2026-09〜10） | クラウド必須／非目標（拡張エコシステム） |
| スペースの端末間同期・共有リンク | Zen Browser 1.22b／1.22.1b（2026-09） | クラウド必須 |

## 既にあった（誤認防止）

| 機能 | 製品 | Loomo の該当（§・コード） |
|---|---|---|
| ログポイント | IntelliJ IDEA 2026.2 | ブレークポイントの `LogMessage`（`src/Loomo.App/ViewModels/Debugging/DebugItemViewModels.cs:170`）・ガターの描き分け（`DebugBreakpointMapper.cs:8`）・NetFxDebug は `supportsLogPoints` |
| 差分で前／次の変更へ（Ctrl+Up/Down） | Fork 2.20 | 差分の「次/前の変更」（`src/Loomo.App/Services/Diff/DiffSideEditorPresenter.cs:24`・§24.25） |
| 差分の中でステージする | lazygit 0.66.0 | §24.18 |
| ブランチの一覧を絞る | Fork 2.23 | Git セッションのブランチ絞り込み（`GitSessionView` の `BranchFilterBox`） |
| 上流が消えたブランチの印 | Fork 2.21 | `GitReferenceModels.UpstreamGone`（`src/Loomo.Services/Git/GitReferenceModels.cs:10`）・`BranchTreeBuilder.IsUpstreamGone` |
| ワークツリーの帯を常に出す | GitHub Desktop 3.6.7 | §24.17 の WT タブ |
| ワークツリーの管理 | Sublime Merge Build 2126 | §24.17 |
| コマンドラインで特定のスペースを開く（`--space`） | Zen Browser 1.23.3b | `--workspace <folder>`（`src/Loomo.App/Services/Startup/StartupArguments.cs:20`） |
| 縦に並んだタブ | Fork 2.22 | TABS（サイドバーのタブ一覧） |

## 調べた範囲

- 一次情報を読んだもの：Sublime Text dev builds（4205〜4214）、Sublime Merge dev builds（2126〜2131）、
  Fork for Windows release notes（2.20〜2.23）、Raycast for Windows changelog（0.71〜2.8）、Zen Browser GitHub Releases（1.22b〜1.23.3b）、
  Cursor changelog（2026-09〜10。4〜8 月の版はページに出ていなかった）、GitHub Desktop GitHub Releases（3.6.5〜3.6.8-beta2）、
  lazygit GitHub Releases（0.65.0〜0.66.2）、PowerToys GitHub Releases（0.101〜0.102 preview）、Vivaldi desktop blog（2026-09〜10 の小更新）。
- 期間内のリリースが無かったもの：WezTerm（最新の正式版が 2024-02-03）。Neovim はニュースページに 2026 年の版の告知が無く、
  GitHub の Releases は見ていない。
- 検索結果の要約だけで、一次情報を読めなかったもの：Rider 2026.2・IntelliJ IDEA 2026.2（2026-07-16。JetBrains の公式ページが
  403 か本文なし）、Vivaldi 8.2 の本体リリースの記事（ニュース記事で確認）。IntelliJ の「Git の衝突解消の流れを整理」は中身を確認できていない。
- 見ていないもの：Arc、Windsurf、Fork の Mac 版（Windows 版だけ見た）。
