using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SalesCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_prices",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    customer_id = table.Column<long>(type: "bigint", nullable: false),
                    product_id = table.Column<long>(type: "bigint", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    row_version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_prices", x => x.id);
                    table.CheckConstraint("ck_customer_prices_valid_period", "valid_to IS NULL OR valid_to >= valid_from");
                    table.ForeignKey(
                        name: "FK_customer_prices_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_prices_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_prices_customer_id",
                table: "customer_prices",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_customer_prices_product_id",
                table: "customer_prices",
                column: "product_id");

            // 手で足した部分。同じ得意先・商品で、有効な単価の期間が重なるのを止める(税率の ex_tax_rates_no_overlap と同じ形)。
            // 無効にした単価は外す(正しい単価を同じ期間で登録し直せる。業務ルール集「見積と得意先別単価」2026-11-16)。
            // valid_to が NULL なら無期限。'[]' は初日・末日の両方を含む。btree_gist は AddStageOneTables で入れてある
            migrationBuilder.Sql(
                "ALTER TABLE customer_prices ADD CONSTRAINT ex_customer_prices_no_overlap " +
                "EXCLUDE USING gist (customer_id WITH =, product_id WITH =, daterange(valid_from, valid_to, '[]') WITH &&) WHERE (is_active);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_prices");
        }
    }
}
