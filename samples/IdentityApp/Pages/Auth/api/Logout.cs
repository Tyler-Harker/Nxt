using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace IdentityApp.Pages.Auth.Api;

/// <summary>
/// <c>POST /api/auth/logout</c> — clears the auth cookie. Posted from the layout's sign-out form.
/// </summary>
public class LogoutEndpoint(SignInManager<IdentityUser> signIn)
{
    public async Task<IResult> POST()
    {
        await signIn.SignOutAsync();
        return Results.Redirect("/");
    }
}
