import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardDescription,
  CardFooter,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";

export default function Home() {
  return (
    <main className="grid min-h-dvh place-items-center bg-background p-6">
      <Card className="w-full max-w-lg text-center">
        <CardHeader className="space-y-2">
          <Badge
            variant="secondary"
            className="mx-auto font-mono text-[0.65rem] tracking-widest uppercase"
          >
            Sales Force Automation
          </Badge>
          <CardTitle className="text-4xl tracking-tight">FieldKit</CardTitle>
          <CardDescription className="text-base">
            The field app &amp; back office. The design system is live — shadcn/ui, Tailwind,
            and the FieldKit tokens (teal accent, light &amp; dark).
          </CardDescription>
        </CardHeader>
        <CardFooter className="justify-center gap-3">
          <Button>Get started</Button>
          <Button variant="outline">View the docs</Button>
        </CardFooter>
      </Card>
    </main>
  );
}
