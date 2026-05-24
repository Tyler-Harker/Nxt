namespace SampleApp.Pages.Blog.Api;

/// <summary>
/// Colocated API — no <c>[ApiRoute]</c> attribute needed. Living under
/// <c>Pages/Blog/api/</c> auto-maps this to <c>/api/blog/comments</c>.
/// </summary>
public class Comments(IGreeting greeting)
{
    public object GET() => new
    {
        endpoint = "/api/blog/comments",
        derivedFrom = "Pages/Blog/api/Comments.cs (RELOAD-V77)",
        greetingDi = greeting.Hello("blog reader"),
    };
}
