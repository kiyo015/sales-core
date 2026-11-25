START TRANSACTION;

ALTER TABLE customers ADD collection_day integer NOT NULL DEFAULT 31;

ALTER TABLE customers ADD collection_months integer NOT NULL DEFAULT 1;

ALTER TABLE customers ADD CONSTRAINT ck_customers_collection CHECK (collection_months BETWEEN 0 AND 3 AND collection_day IN (5, 10, 15, 20, 25, 31) AND (collection_months > 0 OR collection_day > closing_day));

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261125043745_AddCollectionTerms', '8.0.31');

COMMIT;

