using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesCore.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 税区分と税率を入れる(本人の決定 2026-11-02)。税率は法令で決まり、どの環境でも同じなので、マイグレーションで入れる
    /// (業務ルール集「2. 消費税」の現在の税率: 標準10%、軽減8%。どちらも 2019-10-01 から)。税区分の登録の操作は無い。
    /// 税率が変わる時は、新しいマイグレーションで今の期間を閉じ、次の期間を足す。
    /// 業務の操作ではないので監査ログは残さない。作った人は migration にする。
    /// 行は物理削除しない決まりなので、巻き戻しても行は残す(<see cref="Down"/>)。もう一度当てても重ならないよう、既にあれば入れない。
    /// </summary>
    public partial class SeedTaxCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO tax_categories (code, name, row_version, created_by, updated_by)
                VALUES ('STD', '標準', 0, 'migration', 'migration'),
                       ('RED', '軽減', 0, 'migration', 'migration')
                ON CONFLICT (code) DO NOTHING;

                INSERT INTO tax_rates (tax_category_id, rate, valid_from, valid_to, row_version, created_by, updated_by)
                SELECT id, CASE code WHEN 'STD' THEN 10.00 ELSE 8.00 END, DATE '2019-10-01', NULL, 0, 'migration', 'migration'
                FROM tax_categories
                WHERE code IN ('STD', 'RED')
                ON CONFLICT (tax_category_id, valid_from) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 何もしない。行は物理削除しない(NoPhysicalDeleteTests。商品が税区分を使っていれば、消すこともできない)
        }
    }
}
