using Nxt;

namespace NxtApp.Api;

[ApiRoute("hello")]
public class HelloEndpoint
{
    public object GET() => new { message = "Hello from Nxt" };
}
