# CLAUDE.md

販売・購買・請求の基幹システム（`sales-core`）で作業する際のガイド。

> このファイルはgit管理され、リポジトリを開いた全員のClaude Codeに読み込まれる。
> 個人の好み（口調・言語など）はここに書かず、各自の `~/.claude/CLAUDE.md` に書く。

## このプロジェクト

受注 → 出荷 → 請求書発行を扱う基幹システム。最初の1か月は、この流れを1本通すことを目標にしている（画面・認証基盤・帳票PDF・外部連携は範囲外）。

## コマンド

- ビルド: `dotnet build`
- 全テスト: `dotnet test`（編集直後は30秒〜1分半かかる）
- 単一プロジェクトのテスト: `dotnet test tests/SalesCore.Domain.Tests --no-restore`（約12秒）
- API起動: `dotnet run --project src/SalesCore.Api`
- ツールの復元（clone直後に1回）: `dotnet tool restore`（dotnet-ef 8.0.31 が `.config/dotnet-tools.json` から入る）
- マイグレーションの追加: `dotnet ef migrations add <名前> --project src/SalesCore.Infrastructure --startup-project src/SalesCore.Api --output-dir Persistence/Migrations`
- 適用前にSQLを確認: `dotnet ef migrations script --project src/SalesCore.Infrastructure --startup-project src/SalesCore.Api`
- 開発用DBに適用: `dotnet ef database update --project src/SalesCore.Infrastructure --startup-project src/SalesCore.Api`

## DBの準備

開発用とテスト用の2つのDBを、専用ロール `salescore_dev` で使う。**スーパーユーザー（`postgres`）では接続しない。** ロールの作成とパスワードの設定は各自が行う（パスワードは人によって違ってよい）。

1. `postgres` で `CREATE ROLE salescore_dev LOGIN`、`CREATE DATABASE salescore_dev OWNER salescore_dev`、`CREATE DATABASE salescore_test OWNER salescore_dev` を実行し、`\password salescore_dev` でパスワードを設定する
2. 接続文字列を user-secrets に入れる（リポジトリには書かない）
   - `ConnectionStrings:SalesCore` … `Host=localhost;Port=5432;Database=salescore_dev;Username=salescore_dev;Password=...`
   - `ConnectionStrings:SalesCoreTest` … 同じ形で `Database=salescore_test`
   - 設定先は `dotnet user-secrets set <キー> <値> --project src/SalesCore.Api`。結合テストも同じIDの user-secrets を読む
3. `dotnet ef database update ...`（上のコマンド）で開発用DBにマイグレーションを適用する。テスト用DBには結合テストが実行時に自動で適用する

CIでは環境変数 `ConnectionStrings__SalesCoreTest` で渡す。

**結合テストのきまり**
- DBを使うテストは `DatabaseTest` を継承する。テストごとにトランザクションを張り、終わったらロールバックするので、書いたデータは残らない
- 接続先のDB名が `_test` で終わらなければ、テストは始まる前に止まる（開発用DBを壊さないため）
- `dotnet user-secrets list` は実行しない（パスワードが会話の記録に残る）

**テストが `FileLoadException` で落ちたら（Windows）**
`アプリケーション制御ポリシーによってこのファイルがブロックされました。 (0x800711C7)` はコードの誤りではない。Windows の Smart App Control が、ビルドした署名なしの DLL を止めている。判定は DLL の中身ごとに決まり、同じ中身なら何度ビルドしても同じ結果になる（2026-09-30 実測）。**このエラーのときはテストの成否を判断に使わない。** コードを直そうとせず、人に知らせる。対処はSmart App Controlをオフにすること（本人が「Windows セキュリティ > アプリとブラウザー コントロール」で行う。個別に除外する方法は無い）。作者の開発機では2026-09-30にオフにし、止められていた同じDLLがそのまま読み込めることを確認した。

## アーキテクチャ

.NET 8。データベースはPostgreSQL 18、EF Core（Npgsql）を使う。

| プロジェクト | 役割 |
|---|---|
| `src/SalesCore.Domain` | 業務ルール。DB・HTTP・外部ライブラリを知らない。金額計算と状態遷移はここに置く |
| `src/SalesCore.Application` | ユースケース。Domainを組み合わせる |
| `src/SalesCore.Infrastructure` | EF Coreなど外部との接続 |
| `src/SalesCore.Api` | HTTPの入口。業務判断を書かない |
| `tests/SalesCore.Domain.Tests` | Domainの単体テスト。DBを使わない |
| `tests/SalesCore.Integration.Tests` | DBを使う結合テスト |

テストプロジェクトの名前は `<対象プロジェクト名>.Tests` にする。**この命名で`dev-guard`が編集後に走らせるテストを決めている**ので、崩さないこと。

## 書き方の決まり（チーム共通）

- 文書・コードコメント・コミットメッセージは日本語で書く。識別子（クラス名・メソッド名・テーブル名など）は英語
- 用語は `docs/domain/glossary.md` の表記に揃える

誰が書いても揃っている必要があるので、個人の好みではなくチームの決まりとしてここに置く。

## ルールの書き方の約束

各ルールには **強制**（技術的に止めている）か **お願い**（書いてあるだけ）を付ける。機械で判定できるものは、できるだけ強制側に移す。

## 禁止事項

| ルール | 種別 | どこで止めているか |
|---|---|---|
| 金額に `double`・`float` を使う | お願い（→ 2週目にアナライザで強制にする） | レビューで拾う |
| 端数処理を `Money` 以外の場所で行う | お願い（→ Day23にアナライザで強制にする） | 今は `Money.Round` の1か所だけ（Day20・21に検索で確認）。レビューで拾う |
| 消費税を明細ごとに丸めて合計する | 強制（テスト） | `TaxCalculatorTests` が落ちる。明細ごとに丸める実装を入れて、2件（105円×3行の例と、ランダムな請求書1万件）が失敗することを確認済み（2026-10-01） |
| 受注・請求のデータを物理削除する | お願い（→ 3週目にアーキテクチャテストで強制） | 取消は状態で表す。削除しない |
| 接続文字列・秘密情報をファイルに書く | 強制（読み取りを禁止）＋お願い | `.claude/settings.json` の deny。2026-09-25に対照実験で実効性を確認（`.env`は読めず、同じ内容でも名前が違えば読めた）。`dotnet user-secrets` を使い、`appsettings.json` には書かない |
| DBを壊すコマンド（`dotnet ef database drop`・`dotnet ef migrations remove`・SQLの`DROP`/`TRUNCATE`） | 強制 | `dev-guard` 1.2.0 のフックが実行前にブロックする。2026-09-28にこのリポジトリで対照実験付きで確認 |
| force push・`git reset --hard`・再帰かつ強制の削除 | 強制（漏れあり） | `.claude/settings.json` の deny と `dev-guard` のフック |
| テストが落ちたまま次に進む | 強制 | `dev-guard` が編集のたびに対応するテストを実行する |

## 安全柵（dev-guard）の入手と更新

- `.claude/settings.json` が、GitHub の `kiyo015/vibe-coding-study` を**タグ固定**（`ref`）で参照している。初めてこのリポジトリでClaude Codeを起動し、**ワークスペースを信頼すると自動で入る**。信頼しないと入らない（黙って無視される）
- 更新するときは `ref` のタグを上げるPRを出す。プラグイン側の変更がレビューなしで流れ込まないように、最新版ではなくタグを指している

## 業務ルール

**業務上の決まりごとの唯一の定義元は次の文書。** ここに書いてあるとおりに実装する。変える時はコード・テスト・文書を必ず一緒に直す。

@docs/domain/business-rules.md

上だけは常に読み込む（金額の端数処理や状態遷移を間違えると、請求額の誤りに直結するため）。次の3つは分量があるので、必要な時に読む。

| 文書 | いつ読むか |
|---|---|
| `docs/domain/glossary.md` | 業務の言葉（受注・売上・締め・引当など）を扱うとき |
| `docs/domain/business-flow.md` | 業務の順序や状態遷移を扱うとき |
| `docs/domain/er-diagram.md` | テーブルやエンティティを追加・変更するとき |

## 開発の進め方

1. 仕様を決める（何を作るか、**何を作らないか**を書く）
2. テストを先に書き、**狙った理由で落ちること**を確認してから実装する
3. マージ前に `team-reviewer` でレビューする。指摘は実測してから直す・反論する
