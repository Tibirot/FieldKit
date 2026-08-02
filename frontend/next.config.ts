import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Self-contained server output for containerised deploy (ADR-0004 / ADR-0011).
  output: "standalone",
  reactCompiler: true,
};

export default nextConfig;
