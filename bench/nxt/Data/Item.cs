namespace Nxt.Bench.Data;

public record Item(int Id, string Name, decimal Price);

public class ItemStore
{
    public List<Item> All { get; } = Enumerable.Range(1, 100)
        .Select(i => new Item(i, $"Item {i}", Math.Round(i * 9.99m, 2)))
        .ToList();
}
