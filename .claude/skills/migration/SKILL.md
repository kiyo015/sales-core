---
name: migration
description: sales-core で、DBの表・列・制約を足す・変える(EF Core のエンティティや構成を変えた、マイグレーションを作る・適用する)ときに必ず使う。「表を追加して」「列を足して」「マイグレーションを作って」「DBに反映して」のような依頼が来たら、dotnet ef を実行する前にこの手順に従う。生成された SQL を読まずに適用して、データを消す・型を変える・削除を連鎖させる事故を防ぐ。DBに触れない変更には使わない。
---

# マイグレーションの手順

**生成 → SQLを出してレビュー → テスト用DBで確かめる → 開発用DBにだけ適用。** EF Core が生成する SQL は、モデルの書き方しだいで表を作り直したり、削除を連鎖させたりする。C# の差分だけを見ても分からないので、必ず SQL を読んでから適用する。

## 1. モデルを変える前に

- `docs/domain/er-diagram.md` を先に直す(表・列・型・NULL可否・一意制約)。ER図とマイグレーションが食い違ったら、マイグレーションが正(ER図の冒頭の約束)なので、そろえ直す
- 業務ルールに関わる変更なら、先に `domain-rule-change` スキルに従う

## 2. マイグレーションを作る

ビルドとテストが通る状態で:

```
dotnet ef migrations add <名前> --project src/SalesCore.Infrastructure --startup-project src/SalesCore.Api --output-dir Persistence/Migrations
```

名前は英語で、何をするかが分かるもの(`AddSalesOrders`、`AddRowVersionToAllTables`)。

## 3. SQL を出してレビューする

```
dotnet ef migrations script <直前のマイグレーション> <今回のマイグレーション> --project src/SalesCore.Infrastructure --startup-project src/SalesCore.Api --output docs/migrations/<YYYY-MM-DD>-<名前>.sql
```

最初のマイグレーションの直後なら、`<直前>` は `Initial`。出した SQL はリポジトリに残す(何を確かめて適用したかの記録)。

次の表で1行ずつ確かめる。**1つでも引っかかったら適用しない。** モデルを直して作り直す(手順6)。

| 確かめること | 見つけ方 | 理由 |
|---|---|---|
| データを消す操作が無い | `DROP TABLE`・`DROP COLUMN`・`TRUNCATE` | 既存のデータが消える。表や列の名前の変更が、EF Core には「消して作る」に見えることがある |
| 型の変更が無い | `ALTER COLUMN … TYPE` | 桁が減ると値が丸められる・失敗する |
| **削除の連鎖が無い** | `ON DELETE CASCADE` | 取引データは物理削除しない決まり。親を消すと子が黙って消える。外部キーは `RESTRICT` |
| 金額・数量の型 | `numeric(15,0)`(金額)・`numeric(15,2)`(単価)・`numeric(15,3)`(数量)・`numeric(5,2)`(税率) | 業務ルール集「1. 金額と数量」。`double precision`・`real` があってはならない |
| NULL 可否 | `NOT NULL` | 業務上必須の列が NULL を許していないか。既存の行がある表に NOT NULL の列を足すなら、既定値か移行の手順が要る |
| 一意制約 | `CREATE UNIQUE INDEX` | 番号(`number`)・コード(`code`)が重複できないか |
| 名前 | 表・列 | snake_case(`sales_order_lines.shipped_quantity`) |
| 余計なものが紛れていない | `INSERT`・接続文字列・パスワード | マイグレーションにデータや秘密情報を入れない |

変更が大きいとき(表を足す・外部キーを変える)は、SQL と「何のための変更か」を `team-reviewer` にも渡す。

## 4. テスト用DBで確かめる

```
dotnet test tests/SalesCore.Integration.Tests
```

結合テストのフィクスチャが、テスト用DB(`_test` で終わる名前)にマイグレーションを適用してから走る。新しい表には、保存して読み直すテストを足す(手順の前に書いて、表が無いことで落ちるのを見ておく)。

## 5. 開発用DBにだけ適用する

```
dotnet ef migrations list --project src/SalesCore.Infrastructure --startup-project src/SalesCore.Api
dotnet ef database update --project src/SalesCore.Infrastructure --startup-project src/SalesCore.Api
```

- 適用先は user-secrets の `ConnectionStrings:SalesCore`(`salescore_dev`)。**それ以外の環境には、この手順で適用しない**(段階1に本番は無い。CIはテスト用DBを自分で用意する)
- `list` で、未適用(Pending)が今回の分だけであることを確かめてから `update` する
- 接続文字列を表示するコマンド(`dotnet user-secrets list`)は使わない

## 6. 直したくなったら

- **適用済みのマイグレーションは、書き換えない・消さない。** 直すマイグレーションを新しく足す(テスト用DB・開発用DB・ほかの人の環境に、もう適用されているため)
- 未適用なら作り直してよい。`dotnet ef migrations remove` は `dev-guard` が止めるので、生成された2つのファイル(`<日時>_<名前>.cs` と `.Designer.cs`)を消し、スナップショット(`SalesCoreDbContextModelSnapshot.cs`)を `git checkout` で戻してから、手順2からやり直す
- 開発用DBを作り直したいとき(`dotnet ef database drop`)は、`dev-guard` が止める。本人に頼む

## 7. 記録する

コミットに、マイグレーション・SQL ファイル・ER図の変更を一緒に入れる。コミットメッセージに、手順3で引っかかったものと直し方を書く。
