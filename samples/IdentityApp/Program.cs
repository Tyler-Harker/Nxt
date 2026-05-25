using IdentityApp.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Nxt;

await NxtHost.RunAsync(args, opts =>
{
    opts.ConfigureServices = builder =>
    {
        // SQLite — file lives in the app's content root (which is the project dir at dev time,
        // not the random working directory the process happens to be launched from).
        var dbPath = Path.Combine(builder.Environment.ContentRootPath, "identity-app.db");
        builder.Services.AddDbContext<AppDbContext>(o =>
            o.UseSqlite($"Data Source={dbPath}"));

        // Cookie-based Identity. RequireConfirmedAccount=false keeps the demo simple — flip
        // it on in real apps and wire up email confirmation.
        builder.Services
            .AddIdentity<IdentityUser, IdentityRole>(o =>
            {
                o.SignIn.RequireConfirmedAccount = false;
                o.Password.RequiredLength = 6;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequireUppercase = false;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        builder.Services.ConfigureApplicationCookie(o =>
        {
            o.LoginPath = "/auth/login";
            o.LogoutPath = "/auth/logout";
            o.AccessDeniedPath = "/auth/login";
        });

        builder.Services.AddAuthorization();
        // Blazor needs an AuthenticationStateProvider to surface ClaimsPrincipal to components.
        builder.Services.AddCascadingAuthenticationState();
    };

    // Inject Authentication + Authorization middleware between UseRouting and the endpoint
    // mappings. Nxt's pipeline exposes this exact slot via NxtHostOptions.ConfigureMiddleware
    // so apps can plug in auth (or any other middleware) without touching framework code.
    opts.ConfigureMiddleware = app =>
    {
        app.UseAuthentication();
        app.UseAuthorization();
    };

    opts.ConfigureAfterEndpoints = app =>
    {
        // Ensure the SQLite database + tables exist. EnsureCreated is fine for a demo;
        // real apps should use migrations:
        //     dotnet ef migrations add Initial
        //     dotnet ef database update
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
    };
});
