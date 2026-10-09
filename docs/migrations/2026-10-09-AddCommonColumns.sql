START TRANSACTION;

ALTER TABLE warehouses ADD created_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE warehouses ADD created_by character varying(100);

ALTER TABLE warehouses ADD row_version integer NOT NULL DEFAULT 0;

ALTER TABLE warehouses ADD updated_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE warehouses ADD updated_by character varying(100);

ALTER TABLE tax_rates ADD created_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE tax_rates ADD created_by character varying(100);

ALTER TABLE tax_rates ADD row_version integer NOT NULL DEFAULT 0;

ALTER TABLE tax_rates ADD updated_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE tax_rates ADD updated_by character varying(100);

ALTER TABLE tax_categories ADD created_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE tax_categories ADD created_by character varying(100);

ALTER TABLE tax_categories ADD row_version integer NOT NULL DEFAULT 0;

ALTER TABLE tax_categories ADD updated_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE tax_categories ADD updated_by character varying(100);

ALTER TABLE shipments ADD created_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE shipments ADD created_by character varying(100);

ALTER TABLE shipments ADD row_version integer NOT NULL DEFAULT 0;

ALTER TABLE shipments ADD updated_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE shipments ADD updated_by character varying(100);

ALTER TABLE shipment_lines ADD created_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE shipment_lines ADD created_by character varying(100);

ALTER TABLE shipment_lines ADD row_version integer NOT NULL DEFAULT 0;

ALTER TABLE shipment_lines ADD updated_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE shipment_lines ADD updated_by character varying(100);

ALTER TABLE sales_records ADD created_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE sales_records ADD created_by character varying(100);

ALTER TABLE sales_records ADD row_version integer NOT NULL DEFAULT 0;

ALTER TABLE sales_records ADD updated_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE sales_records ADD updated_by character varying(100);

ALTER TABLE sales_record_lines ADD created_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE sales_record_lines ADD created_by character varying(100);

ALTER TABLE sales_record_lines ADD row_version integer NOT NULL DEFAULT 0;

ALTER TABLE sales_record_lines ADD updated_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE sales_record_lines ADD updated_by character varying(100);

ALTER TABLE sales_orders ADD created_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE sales_orders ADD created_by character varying(100);

ALTER TABLE sales_orders ADD row_version integer NOT NULL DEFAULT 0;

ALTER TABLE sales_orders ADD updated_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE sales_orders ADD updated_by character varying(100);

ALTER TABLE sales_order_lines ADD created_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE sales_order_lines ADD created_by character varying(100);

ALTER TABLE sales_order_lines ADD row_version integer NOT NULL DEFAULT 0;

ALTER TABLE sales_order_lines ADD updated_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE sales_order_lines ADD updated_by character varying(100);

ALTER TABLE products ADD created_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE products ADD created_by character varying(100);

ALTER TABLE products ADD row_version integer NOT NULL DEFAULT 0;

ALTER TABLE products ADD updated_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE products ADD updated_by character varying(100);

ALTER TABLE customers ADD created_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE customers ADD created_by character varying(100);

ALTER TABLE customers ADD row_version integer NOT NULL DEFAULT 0;

ALTER TABLE customers ADD updated_at timestamp with time zone NOT NULL DEFAULT (now());

ALTER TABLE customers ADD updated_by character varying(100);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261009002125_AddCommonColumns', '8.0.31');

COMMIT;

