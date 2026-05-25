import { items } from "../../lib/items.js";

// Force per-request SSR (otherwise Next.js would static-render at build).
export const dynamic = "force-dynamic";

export default function Ssr() {
    return (
        <div>
            <h1>SSR page — {items.length} items</h1>
            <ul>
                {items.map(i => (
                    <li key={i.id}>#{i.id} — {i.name} — ${i.price}</li>
                ))}
            </ul>
        </div>
    );
}
