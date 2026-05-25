import { items } from "../../../lib/items.js";

export const dynamic = "force-dynamic";

export async function GET() {
    return Response.json(items);
}
