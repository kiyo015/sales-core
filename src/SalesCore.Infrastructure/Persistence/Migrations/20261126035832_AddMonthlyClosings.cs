using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SalesCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMonthlyClosings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "monthly_closings",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    month = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    row_version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monthly_closings", x => x.id);
                    table.CheckConstraint("ck_monthly_closings_first_day", "extract(day FROM month) = 1");
                });

            migrationBuilder.CreateTable(
                name: "receivable_balances",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    billing_customer_id = table.Column<long>(type: "bigint", nullable: false),
                    opening = table.Column<decimal>(type: "numeric(15,0)", precision: 15, scale: 0, nullable: false),
                    sales = table.Column<decimal>(type: "numeric(15,0)", precision: 15, scale: 0, nullable: false),
                    tax = table.Column<decimal>(type: "numeric(15,0)", precision: 15, scale: 0, nullable: false),
                    received = table.Column<decimal>(type: "numeric(15,0)", precision: 15, scale: 0, nullable: false),
                    closing = table.Column<decimal>(type: "numeric(15,0)", precision: 15, scale: 0, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    monthly_closing_id = table.Column<long>(type: "bigint", nullable: false),
                    row_version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receivable_balances", x => x.id);
                    table.CheckConstraint("ck_receivable_balances_closing", "closing = opening + sales + tax - received");
                    table.ForeignKey(
                        name: "FK_receivable_balances_customers_billing_customer_id",
                        column: x => x.billing_customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_receivable_balances_monthly_closings_monthly_closing_id",
                        column: x => x.monthly_closing_id,
                        principalTable: "monthly_closings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_monthly_closings_month",
                table: "monthly_closings",
                column: "month",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_receivable_balances_billing_customer_id",
                table: "receivable_balances",
                column: "billing_customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_receivable_balances_monthly_closing_id_billing_customer_id",
                table: "receivable_balances",
                columns: new[] { "monthly_closing_id", "billing_customer_id" },
                unique: true);

            // 月次締めした月の末日以前の日付の売上・入金・請求書を止める(2026-11-12 本人の決定。Domain の MonthlyClosing.EnsureOpen と同じ)。
            // ユースケースが確かめた後、保存までの間に月次締めが先に済んだ時の最後の柵。返品のように操作の無い経路も止める。
            // 関数は VOLATILE(既定)なので、締めの表のロックを待った後の問い合わせは、先に済んだ月次締めを読む。
            // 日付の列は引数で受ける(to_jsonb で読むので、表ごとに関数を作らない)。日付を変える操作は無いので INSERT だけ見る
            migrationBuilder.Sql("""
                CREATE FUNCTION reject_closed_month() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    target date := (to_jsonb(NEW) ->> TG_ARGV[0])::date;
                    closed date := (SELECT max(month) FROM monthly_closings);
                BEGIN
                    IF closed IS NOT NULL AND target < (closed + interval '1 month')::date THEN
                        RAISE EXCEPTION '%.% % は、月次締めした % 以前の日付', TG_TABLE_NAME, TG_ARGV[0], target, to_char(closed, 'YYYY-MM')
                            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_closed_month', TABLE = TG_TABLE_NAME;
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER tr_sales_records_closed_month BEFORE INSERT ON sales_records FOR EACH ROW EXECUTE FUNCTION reject_closed_month('recorded_on');
                CREATE TRIGGER tr_receipts_closed_month BEFORE INSERT ON receipts FOR EACH ROW EXECUTE FUNCTION reject_closed_month('received_on');
                CREATE TRIGGER tr_invoices_closed_month BEFORE INSERT ON invoices FOR EACH ROW EXECUTE FUNCTION reject_closed_month('closing_date');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER tr_invoices_closed_month ON invoices;
                DROP TRIGGER tr_receipts_closed_month ON receipts;
                DROP TRIGGER tr_sales_records_closed_month ON sales_records;
                DROP FUNCTION reject_closed_month();
                """);

            migrationBuilder.DropTable(
                name: "receivable_balances");

            migrationBuilder.DropTable(
                name: "monthly_closings");
        }
    }
}
