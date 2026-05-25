using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace IdentityApp.Data;

/// <summary>
/// EF Core context for the Identity store. <see cref="IdentityDbContext"/> already defines all
/// the user/role/claim/token tables; we just point it at a SQLite file in Program.cs.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext(options)
{
}
