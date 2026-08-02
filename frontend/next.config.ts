import type { NextConfig } from "next";
import createNextIntlPlugin from "next-intl/plugin";

const nextConfig: NextConfig = {
  // Self-contained server output for containerised deploy (ADR-0004 / ADR-0011).
  output: "standalone",
  reactCompiler: true,
};

// Resolves `i18n/request.ts` and makes the message catalogs available to the App Router (ADR-0010).
const withNextIntl = createNextIntlPlugin();

export default withNextIntl(nextConfig);
