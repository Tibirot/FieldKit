import type { Metadata } from "next";
import { Geist } from "next/font/google";
import { NextIntlClientProvider } from "next-intl";
import { getTranslations, setRequestLocale } from "next-intl/server";

import { resolveLocale } from "@/i18n/locale";
import { routing } from "@/i18n/routing";
import { cn } from "@/lib/utils";
import "../globals.css";

const geist = Geist({ subsets: ["latin"], variable: "--font-sans" });

type LocaleLayoutProps = Readonly<{
  children: React.ReactNode;
  params: Promise<{ locale: string }>;
}>;

/** Pre-render every locale at build time instead of on first request. */
export function generateStaticParams() {
  return routing.locales.map((locale) => ({ locale }));
}

export async function generateMetadata({
  params,
}: Omit<LocaleLayoutProps, "children">): Promise<Metadata> {
  const locale = resolveLocale((await params).locale);
  const t = await getTranslations({ locale, namespace: "App" });

  return { title: t("name"), description: t("description") };
}

export default async function LocaleLayout({ children, params }: LocaleLayoutProps) {
  const locale = resolveLocale((await params).locale);

  // Opts this subtree into static rendering (see `generateStaticParams`).
  setRequestLocale(locale);

  return (
    <html lang={locale} className={cn("font-sans", geist.variable)}>
      <body>
        <NextIntlClientProvider>{children}</NextIntlClientProvider>
      </body>
    </html>
  );
}
