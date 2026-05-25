using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace IdentityApp.Pages.Auth.Api;

/// <summary>
/// <c>POST /api/auth/login</c> — handles the form submit from <c>Pages/Login.razor</c>.
/// Reads form fields (not JSON, since it's a browser form) and signs the user in by setting
/// the auth cookie via <see cref="SignInManager{TUser}"/>. Redirects on success/failure.
/// </summary>
public class LoginEndpoint(SignInManager<IdentityUser> signIn)
{
    public async Task<IResult> POST(HttpContext ctx)
    {
        var form = await ctx.Request.ReadFormAsync();
        var email = form["email"].ToString();
        var password = form["password"].ToString();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return Results.Redirect("/auth/login?error=" + Uri.EscapeDataString("Email and password required."));

        var result = await signIn.PasswordSignInAsync(
            userName: email,
            password: password,
            isPersistent: true,
            lockoutOnFailure: false);

        if (!result.Succeeded)
        {
            var reason = result.IsLockedOut ? "Account locked." :
                         result.IsNotAllowed ? "Account not allowed to sign in." :
                                                "Invalid email or password.";
            return Results.Redirect("/auth/login?error=" + Uri.EscapeDataString(reason));
        }
        return Results.Redirect("/dashboard/profile");
    }
}
