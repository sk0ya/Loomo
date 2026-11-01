# 他ツールの新機能からの取り込み候補（2026-11-01・焦点：全領域・期間：2026-05-01〜11-01）

前回（`feature-scout-2026-10-31.md`）から 1 日しか経っておらず、主要製品（VS Code・Zed・Claude Code・Windows Terminal・lazygit・
kitty・Neovim ほか）には 10-31 以降の版が無かった（GitHub API で確かめた）。そこで今回は、これまでの 12 本で**一度も読んでいない製品**
（Gitnuro・jjui・Double Commander・Beekeeper Studio・NetBeans・XYplorer・Chrome 本体・Edge）と、前回まで本文が読めなかった
IntelliJ IDEA／Rider 2026.3 EAP の記事を読んだ。収穫は少なく、推すのは 1 件。期間の主な製品はほぼ読み尽くしている。

## 推す

### 1. git が資格情報を求めたら、部屋の中で聞く（元：jjui v0.10.11・2026-09-21／Gitnuro 2.0.0・2026-10-26）
- 出典：https://github.com/idursun/jjui/releases/tag/v0.10.11 、https://github.com/JetpackDuba/Gitnuro/releases/tag/2.0.0
- 元の機能：jjui は git の資格情報の問い合わせ（ユーザー名・パスワード・SSH 鍵のパスフレーズ）を `GIT_ASKPASS`／`SSH_ASKPASS` で
  自分の画面に受け、ユーザー名は見せ、パスワードは伏せて入力させる（`askpass.enabled` で切れる）。Gitnuro 2.0 は Git Credential Manager と
  SSH 鍵での署名に対応し、「パスフレーズの無い鍵では聞かない」「間違えたら聞き直す」を入れた。
- Loomo でやると：Git ペインのプッシュ・プル・フェッチ・クローンで git が資格情報を求めたとき、`GIT_ASKPASS`／`SSH_ASKPASS`
  （＋`SSH_ASKPASS_REQUIRE=force`）を Loomo 同梱の小さな exe に向け、その exe が名前付きパイプで Loomo に問い合わせを渡す。Loomo は
  Git ペインの上の帯（§24.100 の bisect の帯と同じ場所）か非モーダルの入力欄に「origin（https://…）のパスワード」と出し、答えを返す。
  いまは対話が要るとその場で失敗して git の英文がそのまま出るので、**SSH 鍵にパスフレーズがある人・GCM を入れていない人は、
  プッシュのたびにターミナルへ移って打ち直す**ことになる。部屋の外（ターミナル）へ素材を持ち出さずに済むのが効き目。
  裏の fetch（§24.92）は今まで通り聞かない（`GCM_INTERACTIVE=never`・`SSH_ASKPASS_REQUIRE=never` のまま）。
  あわせて、認証で落ちたときの文面を §24.99 の `GitRefusal` に足し、「資格情報が要ります——入力する／ターミナルで続ける」と次の一手を添える。
  答えは保存しない（保存は GCM・ssh-agent の役目）。§24.66 と同じく、入力した文字をコマンドの記録・ログ・通知に残さない。
- いまの Loomo：部分的にある。git は `GIT_TERMINAL_PROMPT=0` で走り（`src/Loomo.Services/Git/GitCommandRunner.cs:161`）、
  GCM が入っていればその窓は出るが、それ以外は「対話が要るとその場で失敗する」と決めている（`src/Loomo.Services/Git/GitCloneService.cs:16-18`）。
  裏の fetch は対話を明示的に切っている（`src/Loomo.Services/Git/GitBranchService.cs:141-145`）。`GIT_ASKPASS`／`SSH_ASKPASS` を向ける箇所は無く、
  `GitRefusal`（`src/Loomo.Services/Git/GitRefusal.cs`）が読み取るのはプッシュの拒否とチェックアウトの上書きだけで、認証の失敗は無い。
- 作業の置き場：Loomo のみ（askpass 用の小さな exe を新しいプロジェクトとして足し、App の出力へコピーする。`Loomo.NetFxDebug` と同じ運び方）。
- 規模：M（askpass の exe とパイプ、Git ペインの入力欄、認証失敗の文面の読み取りとテスト。Windows の OpenSSH と Git for Windows 同梱の ssh で
  `SSH_ASKPASS` の効き方が違うので、両方で実機確認が要る）
- 関連：§24.99・§24.92・§24.66・§34.12

## 検討したが見送り

| 機能 | 製品・版 | 見送り理由 |
|---|---|---|
| エディタで `..` と打つと、その場でできる操作（ログポイント・リファクタリング・AI）が出る（command completion） | IntelliJ IDEA 2026.3 EAP（記事 2026-10-07） | 入口がエディタの補完ポップアップで、Loomo の Alt+Enter（LSP の code action＋ホスト項目、§36）とパレットに重なる。部屋の性質（周辺視野・素材の流れ・復元・軌跡）に効かない。機能自体は 2025 年からあり、期間内の追加はログポイントと AI の口 |
| `print` 文をログポイントに置き換える提案 | IntelliJ IDEA 2026.3 EAP（2026-10-07） | デバッガの道具の品質。ログポイント自体は既にある（下の表） |
| ラムダの中に止めるブレークポイント（行の中の位置で止める） | NetBeans 31（2026-08-10、#9285） | デバッガの道具の品質で部屋に効かない。netcoredbg が行の中の位置（`breakpointLocations`）に応えるかも未確認 |
| Git の差分のファイル一覧の並び順を覚える | NetBeans 31（2026-08-10、#9430） | 小さすぎる（Loomo の差分の一覧は §24.50 の通し読みが主） |
| 縦のタブ・没入型の読書モード | Chrome（2026-04-07 の公式ブログ） | 期間外（4 月） |
| ドラッグでタブをまとめてグループに | Edge 142 | 2025-10 で期間外 |
| プラグイン・テーマ・Windows ARM／MSI | Beekeeper Studio 6.2.0（2026-10-28） | DB クライアント。拡張機能のエコシステムは非目標（§19.9） |
| 共有ダイアログ（Share） | XYplorer 28.30（2026-03-26） | 期間外。部屋に効かない |
| evolog から変更を 2 つに割る | jjui v0.10.11（2026-09-21） | jj 専用の操作。Loomo は git |
| ブランチの改名・SSH 鍵での署名・状態の複数選択 | Gitnuro 2.0.0（2026-10-26） | Git の道具の品質。部屋に効かない |

## 既にあった（誤認防止）

| 機能 | 製品 | Loomo の該当（§・コード） |
|---|---|---|
| 変更が無いときに直前のコミットを修正（amend）する状態を出す | Gitnuro 2.0.0 | Git パネルの amend（`src/Loomo.App/ViewModels/Git/GitPanelViewModel.cs:184`・`:292`） |
| ログポイント | IntelliJ IDEA 2026.3 EAP | ブレークポイントの `LogMessage`（`feature-scout-2026-10-18-未調査製品.md` の既にあった欄） |
| 括弧の色分け・データブレークポイント・補完の種類で絞る | Rider 2026.3 EAP（2026-09-07） | 前回までに見送り済み（`feature-scout-2026-10-18-残りの製品.md`・`-2026-10-19-ペインとサイドバー.md`）。今回 RSS で本文を初めて読めたが、判断は変わらない |

## 調べた範囲

- 10-31 以降の版が無いことを GitHub API で確かめたもの：VS Code（1.144.0・10-28 が最新）、Claude Code（2.1.316・10-31）、Zed（1.26.2／1.27.0-pre・10-28）、
  Windows Terminal（10-02）、lazygit（0.66.2・10-16）、kitty（0.49.2・10-01）、Neovim（0.12.6・10-09。nightly のみ）、Ghostty・WezTerm・Helix（期間内の正式版なし）。
- 初めて一次情報を読んだ製品：Gitnuro の GitHub Releases（2.0.0-beta01〜2.0.0・2026-08-11〜10-26）、jjui の GitHub Releases（v0.10.5〜v0.10.12・05-09〜10-17）、
  Beekeeper Studio（v6.1.1〜v6.2.0）、Apache NetBeans の GitHub Releases（30・2026-05-11、31・2026-08-10）、JetBrains の .NET ブログと IntelliJ IDEA ブログの RSS
  （Rider 2026.3 EAP の記事本文・IntelliJ IDEA 2026.3 EAP の command completion の記事 10-07）。
- 部屋に関わる項目が無かったもの：Double Commander（v1.2.6〜v1.2.9 は 1.2 系の修正版で、本文は wiki へのリンクだけ）、Beekeeper Studio、NetBeans 30。
- 期間内の版が見つからなかった・期間外だったもの：XYplorer（28.30・2026-03-26。以降の公式の changelog は見つからず）、OneCommander（3.107.2・2026-02-06 が
  見つかった最新）、Chrome の縦のタブ（2026-04-07）、Edge 141／142（2025 年）、Visual Studio 2026 18.12（検索に出ず）。
- 一次情報を読めなかったもの：Rider 2026.3 EAP の記事ページ（403。RSS の本文で代えた）、Ptyxis（GNOME GitLab の版の告知を見つけられず）、
  HeidiSQL v12.18〜v12.22・DBeaver 26.1〜26.2（版と日付だけ。DB クライアントなので本文は読んでいない）。
- 見ていないもの：Chrome／Edge DevTools、Cursor／Windsurf（AI が主役）、Arc（開発停止）、Notion（クラウド前提）——前回までと同じ理由。
- 前回までの 12 本で推した・見送った・既にあった項目は重ねて挙げていない。

## 実装済み（2026-11-01）

推した 1 件を §24.101 として設計・実装した（`docs/設計/03-UIとレイアウト.md`）。

| 推す | 設計 | 確かめたこと |
|---|---|---|
| 1. git が資格情報を求めたら、部屋の中で聞く | §24.101 | `GitAskPassGitTests`（本物の git が 401 を返す手元の遠隔に対し、ユーザー名とパスワードを部屋で聞いて Authorization として送る／裏の取得は聞かない）、`GitAskPassSshTests`（Git 同梱の ssh-keygen 8.8 と Windows の OpenSSH 9.5 の両方で、パスフレーズ付きの鍵を部屋で聞いて開ける）、`GitAskPassServerTests`（本物の exe で往復・取り消し・Loomo 不在・git が居なくなったら入力欄を閉じる）、`GitCredentialDialogTests`（入力欄の形と中止で閉じる）。**Loomo を起動しての実機の目視はまだ**（GitHub など本物の遠隔への push では未確認） |
