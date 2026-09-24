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

## ルールの書き方の約束

各ルールには **強制**（技術的に止めている）か **お願い**（書いてあるだけ）を付ける。機械で判定できるものは、できるだけ強制側に移す。

## 禁止事項

| ルール | 種別 | どこで止めているか |
|---|---|---|
| 金額に `double`・`float` を使う | お願い（→ 2週目にアナライザで強制にする） | レビューで拾う |
| 端数処理を `Money` 以外の場所で行う | お願い（→ 2週目に強制） | レビューで拾う |
| 受注・請求のデータを物理削除する | お願い（→ 3週目にアーキテクチャテストで強制） | 取消は状態で表す。削除しない |
| 接続文字列・秘密情報をファイルに書く | 強制（読み取りを禁止）＋お願い | `dotnet user-secrets` を使う。`appsettings.json` に書かない |
| DBを壊すコマンド（`dotnet ef database drop` など）を実行する | **未対応**（1週目の残り作業） | `dev-guard` のフックに追加予定 |
| force push・`git reset --hard`・再帰かつ強制の削除 | 強制（漏れあり） | `.claude/settings.json` の deny と `dev-guard` のフック |
| テストが落ちたまま次に進む | 強制 | `dev-guard` が編集のたびに対応するテストを実行する |

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
