# 段階2「画面と権限」の設計（2026-10-22）

段階1（API で受注 → 出荷 → 締め請求）の上に、画面とログイン・役割ごとの権限を作る。期間は Day35〜49（15日）。日ごとの計画は vibe-coding-study の `study_plan_next_month.md`。全体の段階構成は `2026-09-18-scope-and-phases-design.md`。

## 本人の決定（2026-10-21）

| 論点 | 決定 | 理由 |
|---|---|---|
| 描画方式 | Blazor の **Interactive Server** | 画面の処理をサーバーで動かし、DB や業務の処理をブラウザに出さない。認証も ASP.NET Core Identity の標準のまま使える。WebAssembly は API 経由とトークンの認証が要り、15日には重い |
| 画面から業務の処理の呼び方 | **Application のユースケースを同じプロセスで直接呼ぶ** | 権限の検査をユースケースの入口の1か所に置けば、API と画面の両方の経路に効く。HTTP の層を1つ増やさずに済む |
| 受注の承認 | **税抜合計100万円以上は管理者**。未満は営業でも承認できる。基準額は設定で持つ | 「税抜合計100万円以上は管理者」は業務ルール集の「承認」（Day16 に決定済み）。「未満は営業でも承認できる」と役割の表は本人の決定（2026-10-21・2026-10-26）。2026-10-26 に業務ルール集の「役割」に入れた（以後はそちらが唯一の定義元） |
| ログインのロックとパスワード（2026-10-23） | 5回続けて間違えると15分ロック。パスワードは12文字以上、種類は問わない | 本人の決定。業務ルール集の「8. 利用者とログイン」に入れた |
| Identity の置き場所（2026-10-23） | 業務の DbContext とは別の `SalesCoreIdentityDbContext`（Infrastructure）。ログインの入口は Application の `AccountUseCases` | 業務の保存の決まり（削除の禁止・監査ログの JSON・共通の列）を Identity に当てると、パスワードのハッシュが監査ログに写り、役割を外す操作が止められる。画面は Application だけを使う（`WebLayerTests`） |

## 作るもの

- `src/SalesCore.Web`（Blazor Web App、Interactive Server）
  - 参照するのは `SalesCore.Application` だけ
  - DB とユースケースの登録は `SalesCoreServices.AddSalesCore`（Application）を呼ぶ。API も同じものを使う
- `tests/SalesCore.Web.Tests`（bUnit）。画面の部品を、ブラウザなしで描画して確かめる
- ASP.NET Core Identity（利用者・役割の表は同じ DB に置く。migration スキルで追加）
- 役割は3つ（営業・経理・管理者）。役割×操作の表は業務ルール集の「役割」（2026-10-26）が唯一の定義元で、ここには写さない
- 監査ログと作成者・更新者に、ログインした利用者を入れる（段階1では `CurrentUser` が null）

- 操作した人（2026-10-26）: 口 `ICurrentUserSource` は Infrastructure（業務の DbContext が読む）、中身 `LoggedInUser` は Application に置いた。
  - Application は ASP.NET Core の部品（`Microsoft.AspNetCore.App`。Identity・HTTP・Blazor の認証の状態）に依存する。これは意図したもの。
  - 理由: 画面の対話中は Blazor の認証の状態、API は HTTP の利用者から読むので、両方を Application の1か所で扱う。画面が Infrastructure の型を触らずに済む。

## 作らないもの（段階2では）

- 社内アカウント連携（Microsoft Entra ID など）。Identity で始め、必要になったら差し替える
- 帳票（PDF）・外部連携（段階5）
- 見積・入金・消込（段階3）、購買・在庫（段階4）の画面
- 画面での業務の判断（金額の計算、状態の判定、承認の基準の判定）。すべて Domain と Application に置く

## 強制すること

| 決まり | どこで止めるか | 確かめた日 |
|---|---|---|
| 画面から Domain の操作（メソッド・プロパティ）や Infrastructure を直接使わない | アーキテクチャテスト `WebLayerTests`（Web の DLL を読む。.razor から生成されたコードも含む） | 2026-10-22。対照実験3通り：Domain のメソッドを呼ぶ → 失敗、DbContext を注入する → 失敗、Domain の例外型で判定するだけ → 通る（許す） |
| 画面の部品（.razor）でも金額に double・float を使わない | `src/Directory.Build.props` の SALES001 が .razor も調べる | 2026-10-22。.razor に `double` を書くと、直す前はビルドが通り、直した後は SALES001 で止まった |
| 画面の部品でも端数処理を Money の外で行わない | BannedApiAnalyzers（RS0030）。.razor から生成されたコードも見る | 2026-10-22。.razor の `Math.Round` が RS0030 で止まった（もともと効いていた） |
| .razor を編集したら、画面のテストを走らせる | dev-guard 1.3.2（編集後のテストの対象に .razor を足した） | 2026-10-22 |
| 権限の無い操作を、API でも画面でもできない | ユースケースの入口の検査（Day38）。API（Day39）と画面（Day46）で、検査を外すと通ってしまう対照実験 | 段階2で確かめる |

**画面でボタンを隠すのは使い勝手のためで、守りにはしない。** 守りはユースケースの入口の検査だけに置く。

## 業務の画面を作る前に決めること

- **DbContext の寿命（PR #8 の AI レビューの Important）**
  - Interactive Server では、scoped の登録（DbContext・ユースケース）が「1回の要求」ではなく、利用者がタブを開いている間の回線（circuit）1本の間ずっと生き続ける。
  - そのまま画面から注入すると、1つの DbContext が同じ利用者の複数の操作にまたがって使われる。古い版数のエンティティが残って楽観ロックが期待どおりに働かない、失敗した操作の変更が次の保存に混ざる、操作が重なると同時実行の例外になる、が起こりうる。
  - 直し方は、操作ごとにスコープを作るか（`OwningComponentBase<T>` か `IServiceScopeFactory`）、Application 側で `IDbContextFactory` を使う形にする。ユースケースを呼ぶ最初の画面（Day41 のマスタ）より前に決め、二重に操作した時に版数で止まることをテストで確かめる。
- **`.razor` の SALES001 を自動のテストで守る（PR #8 の Minor）**
  - 今日は手で対照実験をしただけ。SDK の更新で `Content` の扱いが変わると、気付かずに素通りに戻る。`double` を含む `.razor` のビルドが失敗することを、CI か健康診断で毎回確かめる。
