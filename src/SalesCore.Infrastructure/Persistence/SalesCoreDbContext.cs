using Microsoft.EntityFrameworkCore;

namespace SalesCore.Infrastructure.Persistence;

/// <summary>
/// 基幹システムのDB。テーブルの対応付けは段階1のDay25で行う。今は接続とマイグレーションの経路だけを通す。
/// </summary>
public sealed class SalesCoreDbContext(DbContextOptions<SalesCoreDbContext> options) : DbContext(options);
