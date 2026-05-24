namespace SampleApp.Pages.Blog.Api;

/// <summary>
/// Colocated API with a dynamic segment. File name <c>[id].cs</c> means the URL is
/// <c>/api/blog/{id}</c> — the parameter is bound from the route value.
/// </summary>
public class BlogPostById
{
    public object GET(string id) => new { id, fetchedAt = DateTime.UtcNow };
}
