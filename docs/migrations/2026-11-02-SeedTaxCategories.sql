START TRANSACTION;

INSERT INTO tax_categories (code, name, row_version, created_by, updated_by)
VALUES ('STD', '標準', 0, 'migration', 'migration'),
       ('RED', '軽減', 0, 'migration', 'migration')
ON CONFLICT (code) DO NOTHING;

INSERT INTO tax_rates (tax_category_id, rate, valid_from, valid_to, row_version, created_by, updated_by)
SELECT id, CASE code WHEN 'STD' THEN 10.00 ELSE 8.00 END, DATE '2019-10-01', NULL, 0, 'migration', 'migration'
FROM tax_categories
WHERE code IN ('STD', 'RED')
ON CONFLICT (tax_category_id, valid_from) DO NOTHING;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261102032134_SeedTaxCategories', '8.0.31');

COMMIT;

