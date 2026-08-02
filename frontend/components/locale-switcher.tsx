"use client";

import { useLocale, useTranslations } from "next-intl";

import { Button } from "@/components/ui/button";
import { usePathname, useRouter } from "@/i18n/navigation";
import { routing } from "@/i18n/routing";

/**
 * Switches locale while staying on the current route — `usePathname` returns the path *without*
 * the locale prefix, and the locale-aware router puts the new one back on.
 */
export function LocaleSwitcher() {
  const t = useTranslations("LocaleSwitcher");
  const activeLocale = useLocale();
  const pathname = usePathname();
  const router = useRouter();

  return (
    <nav aria-label={t("label")} className="flex items-center gap-1">
      {routing.locales.map((locale) => {
        const isActive = locale === activeLocale;

        return (
          <Button
            key={locale}
            size="sm"
            variant={isActive ? "secondary" : "ghost"}
            aria-current={isActive ? "true" : undefined}
            onClick={() => router.replace(pathname, { locale })}
          >
            {t("locale", { locale })}
          </Button>
        );
      })}
    </nav>
  );
}
