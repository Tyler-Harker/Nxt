using Nxt;

namespace SampleApp.Api;

/// <summary>API endpoint at <c>/api/hello</c>. Methods named after HTTP verbs are auto-mapped.</summary>
[ApiRoute("hello")]
public class HelloEndpoint(IGreeting greeting)
{
    public object GET() => new { message = greeting.Hello("api"), at = DateTime.UtcNow };

    public object POST(EchoRequest req) => new { youSaid = req.Text };

    public record EchoRequest(string Text);
}
