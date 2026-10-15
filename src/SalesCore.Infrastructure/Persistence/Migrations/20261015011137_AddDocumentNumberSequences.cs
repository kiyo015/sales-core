using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentNumberSequences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "sales_order_number_seq",
                maxValue: 999999L);

            migrationBuilder.CreateSequence(
                name: "sales_record_number_seq",
                maxValue: 999999L);

            migrationBuilder.CreateSequence(
                name: "shipment_number_seq",
                maxValue: 999999L);

            migrationBuilder.AlterColumn<string>(
                name: "number",
                table: "shipments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValueSql: "'SH-' || lpad(nextval('shipment_number_seq')::text, 6, '0')",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<string>(
                name: "number",
                table: "sales_records",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValueSql: "'SR-' || lpad(nextval('sales_record_number_seq')::text, 6, '0')",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<string>(
                name: "number",
                table: "sales_orders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValueSql: "'SO-' || lpad(nextval('sales_order_number_seq')::text, 6, '0')",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);
        }

        /// <inheritdoc />
        // 生成されたままでは、シーケンスを消してから既定値を外す順で、PostgreSQL が 2BP01(依存しているものがある)で止めた。
        // 既定値を外してからシーケンスを消す順に並べ替えた(2026-10-15。適用済みだが、Up は変えていないので各DBの状態は変わらない)
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "number",
                table: "shipments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldDefaultValueSql: "'SH-' || lpad(nextval('shipment_number_seq')::text, 6, '0')");

            migrationBuilder.AlterColumn<string>(
                name: "number",
                table: "sales_records",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldDefaultValueSql: "'SR-' || lpad(nextval('sales_record_number_seq')::text, 6, '0')");

            migrationBuilder.AlterColumn<string>(
                name: "number",
                table: "sales_orders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldDefaultValueSql: "'SO-' || lpad(nextval('sales_order_number_seq')::text, 6, '0')");

            migrationBuilder.DropSequence(
                name: "sales_order_number_seq");

            migrationBuilder.DropSequence(
                name: "sales_record_number_seq");

            migrationBuilder.DropSequence(
                name: "shipment_number_seq");
        }
    }
}
