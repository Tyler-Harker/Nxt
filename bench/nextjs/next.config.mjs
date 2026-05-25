/** @type {import('next').NextConfig} */
const nextConfig = {
    // Disable React strict double-render in dev (we only bench prod anyway, but keeps
    // any sanity-check runs honest).
    reactStrictMode: false,
};

export default nextConfig;
