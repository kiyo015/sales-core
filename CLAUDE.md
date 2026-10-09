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
- **表・列を変えるときは `migration` スキル（`.claude/skills/migration/`）の手順に従う。** 生成 → SQL を `docs/migrations/` に出してレビュー → テスト用DBで確認 → 開発用DBにだけ適用
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
   - 税率の期間の重なりを止める排他制約に、PostgreSQL の `btree_gist` 拡張を使う。マイグレーションが作るので手作業は要らない（PostgreSQL 13 以降、DBの所有者なら作れる。2026-10-07 に `salescore_dev` で確認）

CIでは環境変数 `ConnectionStrings__SalesCoreTest` で渡す。

**結合テストのきまり**
- DBを使うテストは `DatabaseTest` を継承する。テストごとにトランザクションを張り、終わったらロールバックするので、書いたデータは残らない
- 接続先のDB名が `_test` で終わらなければ、テストは始まる前に止まる（開発用DBを壊さないため）
- データは `TestScenario` で組み立てる。番号・得意先・商品・倉庫など、エンティティに無い列（シャドウプロパティ）をまとめて設定する
- 操作の区切りでは `TestScenario.EndRequestAsync()`（保存してメモリ上のオブジェクトを手放す）を呼び、次の操作は DB から読み直したもので行う。保存し忘れや対応付けの漏れが結果に出る
- **CI と同じ結果にするために**: 数を数える・探すときは、そのテストが作った行に絞る（手元のテスト用DBには別の行が残りうるが、CI のDBはまっさら）。日付は固定の値を使い、今日の日付に頼らない
- `MigrationTests` は、まっさらなスキーマに全マイグレーションを最初から当てる。手元のテスト用DBは適用済みなので、ほかのテストは「最初から当てると失敗するマイグレーション」に気付かない（2026-10-08、手で書いた SQL を壊す対照実験で、気付いたのはこのテストだけだった）
- 同時更新は `CreateAnotherSession()`（同じトランザクションを使う2つ目の DbContext）で、2人目の利用者を再現する。2人目の `TestScenario` には番号の接頭辞を付ける（`new TestScenario(other, "B")`）
- 日時を確かめるときは `Db.Clock` に固定の時刻（`TimeProvider` を継承したクラス）を入れる
- `dotnet user-secrets list` は実行しない（パスワードが会話の記録に残る）

**保存のときに自動で行うこと（`SalesCoreDbContext.SaveChangesAsync`）**
- 削除の印の付いた行があれば止める（物理削除しない）
- 作成者・作成日時・更新者・更新日時を入れ、更新なら版数（`row_version`）を1上げる。受注明細が変わったら受注の版数も上げる
- 変えた業務の値を、変更前と変更後の JSON で `audit_logs` に残す（業務ルール集「7. データの扱い」）。業務のデータと監査ログは1つのトランザクションで保存する
- 操作した人は `CurrentUser`、日時は `Clock`。ユーザーを作る段階2までは `CurrentUser` は null

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
| `tests/SalesCore.Architecture.Tests` | `src` の全プロジェクトのコンパイル済みのコードを読み、決まり（物理削除しない）を確かめる。DBを使わない |

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
| 金額に `double`・`float` を使う | 強制（ビルド）＋お願い | `src/Directory.Build.props` が、`src` 配下のソースに `double`・`float` の語があるとビルドエラー（SALES001）にする。文字列の検査なので、コメントに書いても止まる。**型名を書かない double（`var x = 1.5`、`Math.Sqrt` の戻り値）は止まらない**のでレビューで拾う。2026-10-05に対照実験で確認 |
| 端数処理を `Money` 以外の場所で行う | 強制（ビルド）＋お願い | BannedApiAnalyzers（`src/BannedSymbols.txt`）が `Math.Round`・`decimal.Round`・`Floor`・`Ceiling`・`Truncate`・`Convert.ToInt32/64(decimal)` をビルドエラー（RS0030）にする。例外は `Money.cs` の `#pragma` だけで、他のファイルで RS0030 を抑止するとビルドエラー（SALES002）。**キャストによる切り捨て（`(long)amount`）は止まらない**のでレビューで拾う。2026-10-05に対照実験で確認 |
| 消費税を明細ごとに丸めて合計する | 強制（テスト） | `TaxCalculatorTests` が落ちる。明細ごとに丸める実装を入れて、2件（105円×3行の例と、ランダムな請求書1万件）が失敗することを確認済み（2026-10-01） |
| 未出荷のまま請求する（出荷確定を通さずに売上や請求書を作る） | 強制（型＋テスト） | 請求書の入口は売上だけを受け取り、売上は出荷確定と返品からしか生まれない。12通りの書き方がすべてコンパイルエラーになることを確認済み（2026-10-02）。公開コンストラクタや別の入口を足すと `InvoiceTests` が落ちる |
| 受注の状態を飛び越える・逆戻りさせる、受注数量を超えて出荷する | 強制（テスト） | `SalesOrderTests`（状態×操作の全20通りを含む）。誤った実装14種を入れて、すべて検出されることを確認済み（2026-10-02） |
| 受注・請求のデータを物理削除する | 強制（テスト・実行時・DB）＋お願い | 取消は状態か赤黒で表す。3段で止める: **アーキテクチャテスト**（`SalesCore.Architecture.Tests`。`src` の DLL に EF Core の `Remove`・`RemoveRange`・`ExecuteDelete` と `DELETE FROM`・`TRUNCATE` の SQL が無い）、**保存のとき**（`SalesCoreDbContext` が削除の印の付いた行を拒む。子の無い行も）、**DB**（外部キーはすべて RESTRICT。子のある親）。EF Core 以外の削除（ドメインのリストの `Remove` など）は止まらないのでレビューで拾う。2026-10-09 に対照実験で確認 |
| 同じデータを2人が同時に変えて、後の保存が先の変更を上書きする | 強制（実行時） | 全表の `row_version`（楽観ロック）。後から保存した方が `DbUpdateConcurrencyException` で止まる。受注明細が変わったら受注の版数も上げる（別々の明細を同時に出荷しても止まる）。2026-10-09 に結合テストで確認 |
| 接続文字列・秘密情報をファイルに書く | 強制（読み取りを禁止）＋お願い | `.claude/settings.json` の deny。2026-09-25に対照実験で実効性を確認（`.env`は読めず、同じ内容でも名前が違えば読めた）。`dotnet user-secrets` を使い、`appsettings.json` には書かない |
| DBを壊すコマンド（`dotnet ef database drop`・`dotnet ef migrations remove`・SQLの`DROP`/`TRUNCATE`） | 強制 | `dev-guard` 1.2.0 のフックが実行前にブロックする。2026-09-28にこのリポジトリで対照実験付きで確認 |
| force push・`git reset --hard`・再帰かつ強制の削除 | 強制（漏れあり） | `.claude/settings.json` の deny と `dev-guard` のフック |
| テストが落ちたまま次に進む | 強制 | `dev-guard` が編集のたびに対応するテストを実行する |

## 金額のきまりをビルドで止める仕組み

- 設定は `src/Directory.Build.props` と `src/BannedSymbols.txt`。`src` 配下の全プロジェクトに効き、`tests` には効かない（テストは期待値を `long` などの別の方法で出すため）
- **ビルドが RS0030・SALES001・SALES002 で落ちたら、抑止せずに `Money` を使って書き直す。** 抑止してよいのは `Money.cs` の中だけ
- `Microsoft.CodeAnalysis.BannedApiAnalyzers` は **3.3.4 に固定**している。4.x 以降はこの SDK（8.0.421、コンパイラ 4.11）より新しく、読み込まれない。そのとき出る CS9057 は警告のままだと見落とすので、エラーにしてある（2026-10-05 実測：5.6.0 では CS9057 が1件出るだけで、違反22箇所のままビルドが通った）。版を上げるのは SDK を上げるときに一緒に行う

## 安全柵（dev-guard）の入手と更新

- `.claude/settings.json` が、GitHub の `kiyo015/vibe-coding-study` を**タグ固定**（`ref`）で参照している。初めてこのリポジトリでClaude Codeを起動し、**ワークスペースを信頼すると自動で入る**。信頼しないと入らない（黙って無視される）
- 更新するときは `ref` のタグを上げるPRを出す。プラグイン側の変更がレビューなしで流れ込まないように、最新版ではなくタグを指している

## 業務ルール

**業務上の決まりごとの唯一の定義元は次の文書。** ここに書いてあるとおりに実装する。変える時はコード・テスト・文書を必ず一緒に直す（手順は `domain-rule-change` スキル。`.claude/skills/domain-rule-change/`）。

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
   - 定義は `.claude/agents/team-reviewer.md`（このリポジトリ用。業務ルールの観点＝金額の型と端数処理・状態遷移・取消と削除・秘密情報を持つ）
   - 渡すのは `git diff` と「何を実現する変更か」。読み取り専用（Read・Grep・Glob だけ）
   - model は **opus**。2026-10-06 に誤りを11項目含む同じ差分で比べた結果、平均の検出は opus 10.8・sonnet 9.7・haiku 7.0。1回あたり $0.29・60秒（sonnet $0.26・187秒、haiku $0.13・122秒）。haiku は存在しない API を使った修正案が多かった（記録は vibe-coding-study の `evidence/2026-10-06-review-grading.md`）
