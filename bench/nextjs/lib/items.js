// Matches bench/nxt/Data/Item.cs::ItemStore — same 100 items in the same shape.
export const items = Array.from({ length: 100 }, (_, i) => ({
    id: i + 1,
    name: `Item ${i + 1}`,
    price: ((i + 1) * 9.99).toFixed(2),
}));
