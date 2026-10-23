using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace SalesCore.Infrastructure.Persistence.Identity;

/// <summary>
/// 利用者と役割(ASP.NET Core Identity の表)。業務の <see cref="SalesCoreDbContext"/> とは分けている。
/// 業務の保存の決まり(削除を拒む、変更前後を監査ログに JSON で残す、全表に共通の列)を当てると、
/// パスワードのハッシュが監査ログに写り、役割を外す操作が止められるため(er-diagram.md「6. 権限と監査」)。
/// 表と列の名前は Identity の既定のまま。足した列だけ snake_case にする。
/// </summary>
public sealed class SalesCoreIdentityDbContext(DbContextOptions<SalesCoreIdentityDbContext> options)
    : IdentityDbContext<SalesCoreUser>(options)
{
    /// <summary>マイグレーションの履歴の表。業務の DbContext の履歴(__EFMigrationsHistory)と混ぜない。</summary>
    public const string MigrationsHistoryTable = "__identity_migrations_history";

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<SalesCoreUser>(b =>
        {
            b.Property(u => u.DisplayName).HasColumnName("display_name").HasMaxLength(100).IsRequired();
            b.Property(u => u.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        });

        // Identity の既定は ON DELETE CASCADE(利用者や役割を消すと、割り当て・トークンなどが黙って一緒に消える)。
        // 利用者も物理削除せず無効にする決まりなので、業務の表と同じく RESTRICT にそろえる(2026-10-23、SQL レビューで変更)。
        // 役割を外す操作は割り当ての行を直接消すので、RESTRICT でも止まらない
        foreach (var foreignKey in builder.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
        {
            foreignKey.DeleteBehavior = DeleteBehavior.Restrict;
        }
    }
}
