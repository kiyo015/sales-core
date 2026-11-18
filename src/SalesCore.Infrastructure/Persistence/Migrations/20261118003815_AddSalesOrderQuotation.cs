using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesOrderQuotation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "quotation_id",
                table: "sales_orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_orders_quotation_id",
                table: "sales_orders",
                column: "quotation_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_sales_orders_quotations_quotation_id",
                table: "sales_orders",
                column: "quotation_id",
                principalTable: "quotations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_sales_orders_quotations_quotation_id",
                table: "sales_orders");

            migrationBuilder.DropIndex(
                name: "IX_sales_orders_quotation_id",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "quotation_id",
                table: "sales_orders");
        }
    }
}
