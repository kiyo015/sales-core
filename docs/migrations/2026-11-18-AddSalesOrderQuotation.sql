START TRANSACTION;

ALTER TABLE sales_orders ADD quotation_id bigint;

CREATE UNIQUE INDEX "IX_sales_orders_quotation_id" ON sales_orders (quotation_id);

ALTER TABLE sales_orders ADD CONSTRAINT "FK_sales_orders_quotations_quotation_id" FOREIGN KEY (quotation_id) REFERENCES quotations (id) ON DELETE RESTRICT;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261118003815_AddSalesOrderQuotation', '8.0.31');

COMMIT;

