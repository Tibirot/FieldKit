import { useTranslations } from "next-intl";

import { Card, CardDescription, CardFooter, CardHeader, CardTitle } from "@/components/ui/card";
import { LinkButton } from "@/components/ui/link-button";

export default function NotFound() {
  const t = useTranslations("NotFound");

  return (
    <main className="grid min-h-dvh place-items-center bg-background p-6">
      <Card className="w-full max-w-md text-center">
        <CardHeader className="space-y-2">
          <CardTitle className="text-2xl">{t("title")}</CardTitle>
          <CardDescription>{t("description")}</CardDescription>
        </CardHeader>
        <CardFooter className="justify-center">
          <LinkButton href="/">{t("back")}</LinkButton>
        </CardFooter>
      </Card>
    </main>
  );
}
