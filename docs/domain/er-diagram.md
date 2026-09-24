# ER図（販売・購買・請求の基幹システム）

全段階（1〜5）で使うテーブルの構造。**段階ごとに作るのは、その段階で使う表だけ**（先に全部は作らない）。各表の見出しに、作り始める段階を書いている。

- 図は領域ごとに分けている。領域をまたぐ関係は「0. 全体」で見る
- 属性は主キー（PK）・外部キー（FK）・一意キー（UK）と、業務上の要になる列だけを載せる。全列の定義はマイグレーションが正とする
- 型は仮置き。金額・数量の桁と端数処理は業務ルール集（`docs/domain/business-rules.md`、Day16で作成）で確定させる

## この形にした業務上の決定（2026-09-18）

| 論点 | 決定 | 構造への影響 |
|---|---|---|
| 請求の方式 | **締め請求**。出荷先と**請求先を分ける**（支店に出荷し、本社に請求する） | `customers`が請求先を自己参照する。請求書は請求先・締め日の単位で作る |
| 分納 | **あり**（1件の受注を複数回に分けて出荷する） | 受注明細に出荷済み数量を持ち、出荷明細が受注明細を参照する |
| 倉庫 | **複数** | 在庫を「商品×倉庫」で持つ |
| 販売単価 | **得意先別単価あり**（期間つき） | `customer_prices`。受注時の単価は受注明細に写して残す（後で単価表が変わっても過去の受注は変わらない） |

## 全表に共通する列（図では省略）

| 列 | 目的 |
|---|---|
| `created_at` / `created_by` / `updated_at` / `updated_by` | 誰がいつ作成・更新したか（`created_by`などは`users`への参照） |
| `row_version` | 楽観ロック（同時更新の衝突検知） |

**物理削除はしない。** マスタは`is_active`で無効化し、取引データ（受注・売上・請求・入金など）の取消は、状態の変更か**赤黒**（打ち消しの伝票を立てる）で表す。

---

## 0. 全体（領域をまたぐ主要な関係）

```mermaid
erDiagram
    customers ||--o{ sales_orders : "受注する"
    sales_orders ||--o{ shipments : "分納で複数回出荷"
    shipments ||--o{ sales_records : "出荷確定で売上計上"
    invoices ||--o{ sales_records : "締めでまとめる"
    customers ||--o{ invoices : "請求先として受け取る"
    invoices ||--o{ receipt_allocations : "消し込まれる"
    receipts ||--o{ receipt_allocations : "入金を割り当てる"
    suppliers ||--o{ purchase_orders : "発注される"
    purchase_orders ||--o{ goods_receipts : "分割で入荷"
    goods_receipts ||--o{ purchase_records : "検収で仕入計上"
    payments ||--o{ payment_allocations : "支払を割り当てる"
    purchase_records ||--o{ payment_allocations : "支払われる"
    products ||--o{ stock_movements : "入出庫"
    warehouses ||--o{ stock_movements : "入出庫"
    shipments ||--o{ stock_movements : "出庫を起こす"
    goods_receipts ||--o{ stock_movements : "入庫を起こす"
```

---

## 1. マスタ（段階1：得意先・商品・税率・倉庫／段階3：得意先別単価／段階4：仕入先）

倉庫は出荷が参照するので段階1で作る（1件だけ登録して使う。複数倉庫で在庫を動かすのは段階4）。段階1の受注は商品の標準単価を使い、得意先別単価は段階3で足す。受注明細は受注時点の単価を写すので、後から単価表を足しても過去の受注は変わらない。

```mermaid
erDiagram
    customers ||--o{ customers : "請求先になる"
    customers ||--o{ customer_prices : "専用単価を持つ"
    products ||--o{ customer_prices : "専用単価がある"
    products }o--|| tax_categories : "税区分"
    tax_categories ||--o{ tax_rates : "期間ごとの税率"
    suppliers ||--o{ supplier_prices : "仕入単価を持つ"
    products ||--o{ supplier_prices : "仕入単価がある"

    customers {
        bigint id PK
        varchar code UK "得意先コード"
        varchar name "得意先名"
        bigint billing_customer_id FK "請求先。自分自身なら自身のid"
        smallint closing_day "締め日。請求先のときだけ意味を持つ(31=月末)"
        varchar collection_terms "回収条件(翌月末など)"
        boolean is_active
    }
    products {
        bigint id PK
        varchar code UK "商品コード"
        varchar name
        varchar unit "単位(個・箱など)"
        bigint tax_category_id FK
        numeric standard_price "標準販売単価"
        numeric standard_cost "標準原価"
        boolean is_active
    }
    tax_categories {
        bigint id PK
        varchar code UK "標準・軽減・非課税など"
        varchar name
    }
    tax_rates {
        bigint id PK
        bigint tax_category_id FK
        numeric rate "10.00 / 8.00"
        date valid_from "税率改定に備えて期間で持つ"
        date valid_to
    }
    customer_prices {
        bigint id PK
        bigint customer_id FK
        bigint product_id FK
        numeric unit_price
        date valid_from
        date valid_to
    }
    suppliers {
        bigint id PK
        varchar code UK "仕入先コード"
        varchar name
        smallint closing_day "仕入先の締め日"
        varchar payment_terms "支払条件"
        boolean is_active
    }
    supplier_prices {
        bigint id PK
        bigint supplier_id FK
        bigint product_id FK
        numeric unit_cost
        date valid_from
        date valid_to
    }
```

---

## 2. 販売（段階1：受注・出荷・売上・請求／段階2：承認／段階3：見積・入金・消込・月次締め）

```mermaid
erDiagram
    customers ||--o{ quotations : "見積を受け取る"
    quotations ||--o{ quotation_lines : "明細"
    quotations |o--o{ sales_orders : "見積から受注"
    customers ||--o{ sales_orders : "受注する"
    sales_orders ||--|{ sales_order_lines : "明細"
    sales_orders ||--o{ sales_order_approvals : "承認の記録"
    sales_orders ||--o{ shipments : "分納"
    warehouses ||--o{ shipments : "出荷元"
    shipments ||--|{ shipment_lines : "明細"
    sales_order_lines ||--o{ shipment_lines : "分納で複数回"
    shipments ||--o{ sales_records : "売上計上"
    sales_records ||--|{ sales_record_lines : "明細"
    sales_records |o--o| sales_records : "赤伝が元伝を打ち消す"
    invoices ||--o{ sales_records : "締めでまとめる"
    invoices ||--|{ invoice_tax_summaries : "税率ごとの集計"
    customers ||--o{ invoices : "請求先"
    customers ||--o{ receipts : "入金元(請求先)"
    receipts ||--o{ receipt_allocations : "割当"
    invoices ||--o{ receipt_allocations : "消込"
    customers ||--o{ receivable_balances : "月次の残高"

    quotations {
        bigint id PK
        varchar number UK "見積番号"
        bigint customer_id FK
        date quoted_on
        date valid_until
        varchar status "作成中・提出済・受注済・失注"
    }
    quotation_lines {
        bigint id PK
        bigint quotation_id FK
        bigint product_id FK
        numeric quantity
        numeric unit_price
    }
    sales_orders {
        bigint id PK
        varchar number UK "受注番号"
        bigint customer_id FK "受注先(出荷先)"
        bigint quotation_id FK "見積から作った場合"
        date ordered_on
        varchar status "受付・承認待ち・承認済・一部出荷・出荷済・取消"
    }
    sales_order_lines {
        bigint id PK
        bigint sales_order_id FK
        bigint product_id FK
        numeric quantity "受注数量"
        numeric shipped_quantity "出荷済み数量(分納)"
        numeric unit_price "受注時点の単価を写す"
        numeric tax_rate "受注時点の税率を写す"
    }
    sales_order_approvals {
        bigint id PK
        bigint sales_order_id FK
        bigint approver_id FK "users"
        varchar decision "承認・却下"
        timestamp decided_at
        varchar comment
    }
    shipments {
        bigint id PK
        varchar number UK "出荷番号"
        bigint sales_order_id FK
        bigint warehouse_id FK
        date shipped_on
        varchar status "指示・確定・取消"
    }
    shipment_lines {
        bigint id PK
        bigint shipment_id FK
        bigint sales_order_line_id FK
        numeric quantity
    }
    sales_records {
        bigint id PK
        varchar number UK "売上番号"
        bigint shipment_id FK
        bigint customer_id FK
        bigint billing_customer_id FK "計上時点の請求先を写す"
        bigint invoice_id FK "締めで請求書に入るまでNULL"
        bigint reverses_id FK "赤伝のとき元の売上"
        date recorded_on "売上日"
    }
    sales_record_lines {
        bigint id PK
        bigint sales_record_id FK
        bigint shipment_line_id FK
        numeric quantity
        numeric unit_price
        numeric tax_rate
        numeric amount "税抜金額"
    }
    invoices {
        bigint id PK
        varchar number UK "請求書番号"
        bigint billing_customer_id FK
        date closing_date "締め日"
        date period_from
        date period_to
        numeric previous_balance "前回請求額"
        numeric received_amount "前回からの入金額"
        numeric carried_over "繰越額"
        numeric sales_amount "今回売上(税抜)"
        numeric tax_amount "消費税(税率ごとに1回端数処理した合計)"
        numeric total_amount "今回請求額"
        date due_date "支払期限"
        varchar status "確定・発行済・取消"
    }
    invoice_tax_summaries {
        bigint id PK
        bigint invoice_id FK
        numeric tax_rate
        numeric taxable_amount "税率ごとの対象額"
        numeric tax_amount "税率ごとに1回だけ端数処理"
    }
    receipts {
        bigint id PK
        varchar number UK "入金番号"
        bigint billing_customer_id FK
        date received_on
        numeric amount
        varchar method "振込・手形・相殺など"
        bigint bank_statement_line_id FK "銀行データから作った場合"
    }
    receipt_allocations {
        bigint id PK
        bigint receipt_id FK
        bigint invoice_id FK
        numeric amount "この請求に充てた額"
    }
    receivable_balances {
        bigint id PK
        bigint billing_customer_id FK
        char year_month UK "YYYY-MM"
        numeric opening_balance
        numeric sales_amount
        numeric receipt_amount
        numeric closing_balance
    }
```

---

## 3. 購買（段階4）

```mermaid
erDiagram
    suppliers ||--o{ purchase_orders : "発注先"
    purchase_orders ||--|{ purchase_order_lines : "明細"
    purchase_orders ||--o{ goods_receipts : "分割で入荷"
    warehouses ||--o{ goods_receipts : "入荷先"
    goods_receipts ||--|{ goods_receipt_lines : "明細"
    purchase_order_lines ||--o{ goods_receipt_lines : "分割で複数回"
    goods_receipts ||--o{ purchase_records : "検収で仕入計上"
    purchase_records ||--|{ purchase_record_lines : "明細"
    purchase_records |o--o| purchase_records : "赤伝が元伝を打ち消す"
    suppliers ||--o{ supplier_invoices : "請求してくる"
    supplier_invoices ||--o{ supplier_invoice_matches : "照合"
    purchase_records ||--o{ supplier_invoice_matches : "照合"
    suppliers ||--o{ payments : "支払先"
    payments ||--o{ payment_allocations : "割当"
    supplier_invoices ||--o{ payment_allocations : "支払われる"
    suppliers ||--o{ payable_balances : "月次の残高"

    purchase_orders {
        bigint id PK
        varchar number UK "発注番号"
        bigint supplier_id FK
        date ordered_on
        date expected_on "入荷予定日"
        varchar status "発注済・一部入荷・入荷済・取消"
    }
    purchase_order_lines {
        bigint id PK
        bigint purchase_order_id FK
        bigint product_id FK
        numeric quantity
        numeric received_quantity "入荷済み数量(分割入荷)"
        numeric unit_cost "発注時点の仕入単価を写す"
        numeric tax_rate
    }
    goods_receipts {
        bigint id PK
        varchar number UK "入荷番号"
        bigint purchase_order_id FK
        bigint warehouse_id FK
        date received_on
        varchar status "入荷・検収済・取消"
    }
    goods_receipt_lines {
        bigint id PK
        bigint goods_receipt_id FK
        bigint purchase_order_line_id FK
        numeric quantity "入荷数"
        numeric accepted_quantity "検収で合格した数"
    }
    purchase_records {
        bigint id PK
        varchar number UK "仕入番号"
        bigint goods_receipt_id FK
        bigint supplier_id FK
        bigint reverses_id FK "赤伝のとき元の仕入"
        date recorded_on "仕入日"
    }
    purchase_record_lines {
        bigint id PK
        bigint purchase_record_id FK
        bigint goods_receipt_line_id FK
        numeric quantity
        numeric unit_cost
        numeric tax_rate
        numeric amount
    }
    supplier_invoices {
        bigint id PK
        bigint supplier_id FK
        varchar supplier_invoice_number "仕入先が付けた請求書番号"
        date closing_date "仕入先の締め日"
        numeric total_amount "仕入先が請求してきた額"
        varchar status "受領・照合済・不一致・支払済"
    }
    supplier_invoice_matches {
        bigint id PK
        bigint supplier_invoice_id FK
        bigint purchase_record_id FK
        numeric amount "この仕入に対応づけた額"
    }
    payments {
        bigint id PK
        varchar number UK "支払番号"
        bigint supplier_id FK
        date paid_on
        numeric amount
        varchar method
    }
    payment_allocations {
        bigint id PK
        bigint payment_id FK
        bigint supplier_invoice_id FK "照合済みの請求書に対して支払う"
        numeric amount
    }
    payable_balances {
        bigint id PK
        bigint supplier_id FK
        char year_month UK "YYYY-MM"
        numeric opening_balance
        numeric purchase_amount
        numeric payment_amount
        numeric closing_balance
    }
```

---

## 4. 在庫（倉庫マスタのみ段階1、それ以外は段階4）

在庫の数は**入出庫の履歴（`stock_movements`）を正**とし、`stocks`はその集計を持つだけ（すぐ引けるように保持する写し）。数が合わなくなったら履歴から作り直せる。

```mermaid
erDiagram
    products ||--o{ stocks : "倉庫ごとの在庫"
    warehouses ||--o{ stocks : "商品ごとの在庫"
    products ||--o{ stock_movements : "入出庫"
    warehouses ||--o{ stock_movements : "入出庫"
    sales_order_lines ||--o{ stock_allocations : "引当"
    warehouses ||--o{ stock_allocations : "引当元"
    warehouses ||--o{ stocktakes : "棚卸"
    stocktakes ||--|{ stocktake_lines : "明細"

    warehouses {
        bigint id PK
        varchar code UK "倉庫コード"
        varchar name
        boolean is_active
    }
    stocks {
        bigint id PK
        bigint product_id FK
        bigint warehouse_id FK
        numeric on_hand_quantity "実在庫(履歴の集計)"
        numeric allocated_quantity "引当済み"
    }
    stock_movements {
        bigint id PK
        bigint product_id FK
        bigint warehouse_id FK
        numeric quantity "入庫は正、出庫は負"
        varchar reason "入荷・出荷・棚卸調整・倉庫間移動"
        bigint shipment_line_id FK "出荷によるとき"
        bigint goods_receipt_line_id FK "入荷によるとき"
        bigint stocktake_line_id FK "棚卸調整によるとき"
        timestamp moved_at
    }
    stock_allocations {
        bigint id PK
        bigint sales_order_line_id FK
        bigint warehouse_id FK
        numeric quantity "受注に対して確保した数"
    }
    stocktakes {
        bigint id PK
        bigint warehouse_id FK
        date counted_on
        varchar status "実施中・確定"
    }
    stocktake_lines {
        bigint id PK
        bigint stocktake_id FK
        bigint product_id FK
        numeric book_quantity "帳簿上の数"
        numeric counted_quantity "実際に数えた数"
    }
```

---

## 5. 帳票と外部連携（段階5）

```mermaid
erDiagram
    users ||--o{ document_issues : "発行した"
    journal_export_batches ||--|{ journal_lines : "仕訳"
    bank_statement_imports ||--|{ bank_statement_lines : "明細"
    bank_statement_lines |o--o| receipts : "入金として登録"

    document_issues {
        bigint id PK
        varchar document_type "請求書・納品書・発注書"
        varchar target_number "対象の伝票番号"
        integer issue_count "何回目の発行か(再発行の管理)"
        varchar file_hash "発行した内容のハッシュ"
        bigint issued_by FK
        timestamp issued_at
    }
    journal_export_batches {
        bigint id PK
        char year_month "対象月"
        timestamp exported_at
        varchar status "出力済・取消"
    }
    journal_lines {
        bigint id PK
        bigint batch_id FK
        date entry_date
        varchar debit_account "借方科目"
        varchar credit_account "貸方科目"
        numeric amount
        varchar source_type "売上・仕入・入金・支払"
        varchar source_number "元の伝票番号"
    }
    bank_statement_imports {
        bigint id PK
        varchar file_name
        varchar file_hash UK "同じファイルの二重取込を防ぐ"
        timestamp imported_at
    }
    bank_statement_lines {
        bigint id PK
        bigint import_id FK
        date transaction_date
        numeric amount
        varchar payer_name_kana "振込依頼人名(カナ)"
        bigint matched_receipt_id FK "消込で入金にした場合"
        varchar match_status "未照合・自動照合・手動照合・対象外"
    }
```

---

## 6. 権限と監査（段階1：監査ログ／段階2：ユーザー・役割）

ユーザーと役割はASP.NET Core Identityの表（`AspNetUsers`・`AspNetRoles`・`AspNetUserRoles`）を使う。ここでは業務側から参照する形だけを示す。

```mermaid
erDiagram
    users ||--o{ user_roles : "持つ"
    roles ||--o{ user_roles : "割り当てられる"
    users ||--o{ audit_logs : "操作した"

    users {
        varchar id PK "Identityのユーザー"
        varchar user_name UK
        varchar display_name
        boolean is_active
    }
    roles {
        varchar id PK
        varchar name UK "営業・経理・管理者"
    }
    user_roles {
        varchar user_id FK
        varchar role_id FK
    }
    audit_logs {
        bigint id PK
        varchar user_id FK
        varchar entity_type "対象の表"
        bigint entity_id
        varchar action "作成・更新・状態変更・取消"
        jsonb before_values
        jsonb after_values
        timestamp occurred_at
    }
```

---

## 業務上の決定（2026-09-24・Day16で確定）

詳細は`business-rules.md`が唯一の定義元。構造に効くものだけをここに再掲する。

| 決定 | 構造への影響 |
|---|---|
| 金額は円単位、単価は小数2桁、数量は小数3桁 | `numeric(15,0)` / `numeric(15,2)` / `numeric(15,3)` |
| 端数は四捨五入。消費税は**税率ごとに1回だけ** | `invoice_tax_summaries`で税率ごとに持つ |
| 売上は**出荷基準**（出荷確定日に計上） | `sales_records.recorded_on`＝出荷確定日。検収の受け取りは持たない |
| 締め日は得意先ごとに選ぶ（5/10/15/20/25/月末） | `customers.closing_day` |
| 締め後の訂正は**翌月に赤黒** | `sales_records.reverses_id`。確定した請求書は変えない |
| 承認は税抜100万円以上（基準額は設定で持つ） | `sales_order_approvals` |
| **仕入先請求書と照合してから支払う** | `supplier_invoices`と`supplier_invoice_matches`を追加。支払は照合済みの請求書に割り当てる |

## まだ決めていないこと

- 値引き・返品の入力方法（赤黒で表す方針は決定済み）
- 与信限度額のチェック
- 倉庫間の在庫移動の承認
- 締め日を途中で変更した場合の扱い
