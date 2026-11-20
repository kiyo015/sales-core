using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SalesCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "receipt_number_seq",
                maxValue: 999999L);

            migrationBuilder.AddColumn<decimal>(
                name: "allocated_amount",
                table: "invoices",
                type: "numeric(15,0)",
                precision: 15,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "receipts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    billing_customer_id = table.Column<long>(type: "bigint", nullable: false),
                    received_on = table.Column<DateOnly>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(15,0)", precision: 15, scale: 0, nullable: false),
                    allocated_amount = table.Column<decimal>(type: "numeric(15,0)", precision: 15, scale: 0, nullable: false),
                    reverses_id = table.Column<long>(type: "bigint", nullable: true),
                    is_cancelled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValueSql: "'RC-' || lpad(nextval('receipt_number_seq')::text, 6, '0')"),
                    row_version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receipts", x => x.id);
                    table.CheckConstraint("ck_receipts_allocated", "(amount > 0 AND allocated_amount BETWEEN 0 AND amount) OR (amount < 0 AND allocated_amount BETWEEN amount AND 0)");
                    table.ForeignKey(
                        name: "FK_receipts_customers_billing_customer_id",
                        column: x => x.billing_customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_receipts_receipts_reverses_id",
                        column: x => x.reverses_id,
                        principalTable: "receipts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "receipt_allocations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    invoice_id = table.Column<long>(type: "bigint", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(15,0)", precision: 15, scale: 0, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    receipt_id = table.Column<long>(type: "bigint", nullable: false),
                    row_version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receipt_allocations", x => x.id);
                    table.ForeignKey(
                        name: "FK_receipt_allocations_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_receipt_allocations_receipts_receipt_id",
                        column: x => x.receipt_id,
                        principalTable: "receipts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoices_allocated",
                table: "invoices",
                sql: "allocated_amount BETWEEN 0 AND sales_amount + tax_amount");

            migrationBuilder.CreateIndex(
                name: "IX_receipt_allocations_invoice_id",
                table: "receipt_allocations",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "IX_receipt_allocations_receipt_id",
                table: "receipt_allocations",
                column: "receipt_id");

            migrationBuilder.CreateIndex(
                name: "IX_receipts_billing_customer_id",
                table: "receipts",
                column: "billing_customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_receipts_number",
                table: "receipts",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_receipts_reverses_id",
                table: "receipts",
                column: "reverses_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "receipt_allocations");

            migrationBuilder.DropTable(
                name: "receipts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_invoices_allocated",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "allocated_amount",
                table: "invoices");

            migrationBuilder.DropSequence(
                name: "receipt_number_seq");
        }
    }
}
