using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectionTerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "collection_day",
                table: "customers",
                type: "integer",
                nullable: false,
                defaultValue: 31);

            migrationBuilder.AddColumn<int>(
                name: "collection_months",
                table: "customers",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddCheckConstraint(
                name: "ck_customers_collection",
                table: "customers",
                sql: "collection_months BETWEEN 0 AND 3 AND collection_day IN (5, 10, 15, 20, 25, 31) AND (collection_months > 0 OR collection_day > closing_day)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_customers_collection",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "collection_day",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "collection_months",
                table: "customers");
        }
    }
}
