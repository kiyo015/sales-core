using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SalesCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "invoice_number_seq",
                maxValue: 999999L);

            migrationBuilder.AddColumn<long>(
                name: "invoice_id",
                table: "sales_records",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "invoices",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    closing_date = table.Column<DateOnly>(type: "date", nullable: false),
                    period_from = table.Column<DateOnly>(type: "date", nullable: false),
                    period_to = table.Column<DateOnly>(type: "date", nullable: false),
                    previous_balance = table.Column<decimal>(type: "numeric(15,0)", precision: 15, scale: 0, nullable: false),
                    received_amount = table.Column<decimal>(type: "numeric(15,0)", precision: 15, scale: 0, nullable: false),
                    carried_over = table.Column<decimal>(type: "numeric(15,0)", precision: 15, scale: 0, nullable: false),
                    sales_amount = table.Column<decimal>(type: "numeric(15,0)", precision: 15, scale: 0, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(15,0)", precision: 15, scale: 0, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(15,0)", precision: 15, scale: 0, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    billing_customer_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValueSql: "'IN-' || lpad(nextval('invoice_number_seq')::text, 6, '0')"),
                    row_version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoices", x => x.id);
                    table.ForeignKey(
                        name: "FK_invoices_customers_billing_customer_id",
                        column: x => x.billing_customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invoice_tax_summaries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tax_rate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    taxable_amount = table.Column<decimal>(type: "numeric(15,0)", precision: 15, scale: 0, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(15,0)", precision: 15, scale: 0, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    invoice_id = table.Column<long>(type: "bigint", nullable: false),
                    row_version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_tax_summaries", x => x.id);
                    table.ForeignKey(
                        name: "FK_invoice_tax_summaries_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sales_records_invoice_id",
                table: "sales_records",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "IX_invoice_tax_summaries_invoice_id",
                table: "invoice_tax_summaries",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_billing_customer_id_closing_date",
                table: "invoices",
                columns: new[] { "billing_customer_id", "closing_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoices_number",
                table: "invoices",
                column: "number",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_sales_records_invoices_invoice_id",
                table: "sales_records",
                column: "invoice_id",
                principalTable: "invoices",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_sales_records_invoices_invoice_id",
                table: "sales_records");

            migrationBuilder.DropTable(
                name: "invoice_tax_summaries");

            migrationBuilder.DropTable(
                name: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_sales_records_invoice_id",
                table: "sales_records");

            migrationBuilder.DropColumn(
                name: "invoice_id",
                table: "sales_records");

            migrationBuilder.DropSequence(
                name: "invoice_number_seq");
        }
    }
}
