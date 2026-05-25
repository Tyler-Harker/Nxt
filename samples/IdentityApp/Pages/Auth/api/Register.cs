using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace IdentityApp.Pages.Auth.Api;

/// <summary>
/// <c>POST /api/auth/register</c> — handles the form submit from <c>Pages/Register.razor</c>.
/// Creates the user via <see cref="UserManager{TUser}"/>, then signs them in.
/// </summary>
public class RegisterEndpoint(
    UserManager<IdentityUser> users,
    SignInManager<IdentityUser> signIn)
{
    public async Task<IResult> POST(HttpContext ctx)
    {
        var form = await ctx.Request.ReadFormAsync();
        var email = form["email"].ToString();
        var password = form["password"].ToString();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return Results.Redirect("/auth/register?error=" + Uri.EscapeDataString("Email and password required."));

        var user = new IdentityUser { UserName = email, Email = email };
        var create = await users.CreateAsync(user, password);
        if (!create.Succeeded)
        {
            var reason = string.Join("; ", create.Errors.Select(e => e.Description));
            return Results.Redirect("/auth/register?error=" + Uri.EscapeDataString(reason));
        }

        await signIn.SignInAsync(user, isPersistent: true);
        return Results.Redirect("/dashboard/profile");
    }
}
