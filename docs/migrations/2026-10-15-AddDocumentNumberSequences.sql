START TRANSACTION;

CREATE SEQUENCE sales_order_number_seq START WITH 1 INCREMENT BY 1 NO MINVALUE MAXVALUE 999999 NO CYCLE;

CREATE SEQUENCE sales_record_number_seq START WITH 1 INCREMENT BY 1 NO MINVALUE MAXVALUE 999999 NO CYCLE;

CREATE SEQUENCE shipment_number_seq START WITH 1 INCREMENT BY 1 NO MINVALUE MAXVALUE 999999 NO CYCLE;

ALTER TABLE shipments ALTER COLUMN number SET DEFAULT ('SH-' || lpad(nextval('shipment_number_seq')::text, 6, '0'));

ALTER TABLE sales_records ALTER COLUMN number SET DEFAULT ('SR-' || lpad(nextval('sales_record_number_seq')::text, 6, '0'));

ALTER TABLE sales_orders ALTER COLUMN number SET DEFAULT ('SO-' || lpad(nextval('sales_order_number_seq')::text, 6, '0'));

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261015011137_AddDocumentNumberSequences', '8.0.31');

COMMIT;

