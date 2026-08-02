import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "FieldKit",
  description: "Sales Force Automation — field app & back office.",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
