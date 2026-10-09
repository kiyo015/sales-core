using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCommonColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "warehouses",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "created_by",
                table: "warehouses",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "row_version",
                table: "warehouses",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "warehouses",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "updated_by",
                table: "warehouses",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "tax_rates",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "created_by",
                table: "tax_rates",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "row_version",
                table: "tax_rates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "tax_rates",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "updated_by",
                table: "tax_rates",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "tax_categories",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "created_by",
                table: "tax_categories",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "row_version",
                table: "tax_categories",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "tax_categories",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "updated_by",
                table: "tax_categories",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "shipments",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "created_by",
                table: "shipments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "row_version",
                table: "shipments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "shipments",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "updated_by",
                table: "shipments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "shipment_lines",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "created_by",
                table: "shipment_lines",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "row_version",
                table: "shipment_lines",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "shipment_lines",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "updated_by",
                table: "shipment_lines",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "sales_records",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "created_by",
                table: "sales_records",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "row_version",
                table: "sales_records",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "sales_records",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "updated_by",
                table: "sales_records",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "sales_record_lines",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "created_by",
                table: "sales_record_lines",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "row_version",
                table: "sales_record_lines",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "sales_record_lines",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "updated_by",
                table: "sales_record_lines",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "sales_orders",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "created_by",
                table: "sales_orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "row_version",
                table: "sales_orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "sales_orders",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "updated_by",
                table: "sales_orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "sales_order_lines",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "created_by",
                table: "sales_order_lines",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "row_version",
                table: "sales_order_lines",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "sales_order_lines",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "updated_by",
                table: "sales_order_lines",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "products",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "created_by",
                table: "products",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "row_version",
                table: "products",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "products",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "updated_by",
                table: "products",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "customers",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "created_by",
                table: "customers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "row_version",
                table: "customers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "customers",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "updated_by",
                table: "customers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "created_at",
                table: "warehouses");

            migrationBuilder.DropColumn(
                name: "created_by",
                table: "warehouses");

            migrationBuilder.DropColumn(
                name: "row_version",
                table: "warehouses");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "warehouses");

            migrationBuilder.DropColumn(
                name: "updated_by",
                table: "warehouses");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "tax_rates");

            migrationBuilder.DropColumn(
                name: "created_by",
                table: "tax_rates");

            migrationBuilder.DropColumn(
                name: "row_version",
                table: "tax_rates");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "tax_rates");

            migrationBuilder.DropColumn(
                name: "updated_by",
                table: "tax_rates");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "tax_categories");

            migrationBuilder.DropColumn(
                name: "created_by",
                table: "tax_categories");

            migrationBuilder.DropColumn(
                name: "row_version",
                table: "tax_categories");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "tax_categories");

            migrationBuilder.DropColumn(
                name: "updated_by",
                table: "tax_categories");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "shipments");

            migrationBuilder.DropColumn(
                name: "created_by",
                table: "shipments");

            migrationBuilder.DropColumn(
                name: "row_version",
                table: "shipments");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "shipments");

            migrationBuilder.DropColumn(
                name: "updated_by",
                table: "shipments");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "shipment_lines");

            migrationBuilder.DropColumn(
                name: "created_by",
                table: "shipment_lines");

            migrationBuilder.DropColumn(
                name: "row_version",
                table: "shipment_lines");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "shipment_lines");

            migrationBuilder.DropColumn(
                name: "updated_by",
                table: "shipment_lines");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "sales_records");

            migrationBuilder.DropColumn(
                name: "created_by",
                table: "sales_records");

            migrationBuilder.DropColumn(
                name: "row_version",
                table: "sales_records");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "sales_records");

            migrationBuilder.DropColumn(
                name: "updated_by",
                table: "sales_records");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "sales_record_lines");

            migrationBuilder.DropColumn(
                name: "created_by",
                table: "sales_record_lines");

            migrationBuilder.DropColumn(
                name: "row_version",
                table: "sales_record_lines");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "sales_record_lines");

            migrationBuilder.DropColumn(
                name: "updated_by",
                table: "sales_record_lines");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "created_by",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "row_version",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "updated_by",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "sales_order_lines");

            migrationBuilder.DropColumn(
                name: "created_by",
                table: "sales_order_lines");

            migrationBuilder.DropColumn(
                name: "row_version",
                table: "sales_order_lines");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "sales_order_lines");

            migrationBuilder.DropColumn(
                name: "updated_by",
                table: "sales_order_lines");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "products");

            migrationBuilder.DropColumn(
                name: "created_by",
                table: "products");

            migrationBuilder.DropColumn(
                name: "row_version",
                table: "products");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "products");

            migrationBuilder.DropColumn(
                name: "updated_by",
                table: "products");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "created_by",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "row_version",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "updated_by",
                table: "customers");
        }
    }
}
