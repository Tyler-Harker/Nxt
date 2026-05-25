// No dynamic fetches → Next.js prerenders at build time.
export default function Static() {
    return (
        <div>
            <h1>Static page</h1>
            <p>Prerendered at build time, served from the static cache.</p>
            <p>Framework: Next.js</p>
        </div>
    );
}
