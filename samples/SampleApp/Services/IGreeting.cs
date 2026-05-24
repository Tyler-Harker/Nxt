namespace SampleApp;

public interface IGreeting { string Hello(string name); }

public class Greeting : IGreeting
{
    public string Hello(string name) => $"Hello, {name}!";
}
