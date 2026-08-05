import { getTranslations, setRequestLocale } from "next-intl/server";

import { UserBrowser } from "@/components/back-office/user-browser";
import { resolveLocale } from "@/i18n/locale";

/**
 * Users and the roles they hold (`IAM-03`).
 *
 * **Roles and their permission toggles** (`IAM-04`) are the slice after this one — the same page,
 * below the user list. The **Device** column the wireframe draws is `IAM-07` and stays deferred:
 * IAM has no device concept yet, so a column here would be an empty promise.
 */
export default async function UsersPage({ params }: { params: Promise<{ locale: string }> }) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "Users" });

  return (
    <div className="flex flex-col gap-4">
      <header>
        <p className="font-mono text-[11.5px] text-muted-foreground">{t("crumb")}</p>
        <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
      </header>
      <UserBrowser />
    </div>
  );
}
