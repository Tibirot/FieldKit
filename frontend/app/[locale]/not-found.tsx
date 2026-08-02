import { useTranslations } from "next-intl";

import { Button } from "@/components/ui/button";
import { Card, CardDescription, CardFooter, CardHeader, CardTitle } from "@/components/ui/card";
import { Link } from "@/i18n/navigation";

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
          {/* Renders an <a>, so Base UI's native-button assumption has to be switched off. */}
          <Button nativeButton={false} render={<Link href="/" />}>
            {t("back")}
          </Button>
        </CardFooter>
      </Card>
    </main>
  );
}
