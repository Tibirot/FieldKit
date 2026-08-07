import { type VariantProps } from "class-variance-authority";
import type { ComponentProps } from "react";

import { buttonVariants } from "@/components/ui/button";
import { Link } from "@/i18n/navigation";
import { cn } from "@/lib/utils";

/**
 * A link that looks like a button.
 *
 * **Not `<Button render={<Link/>}>`.** Base UI's button applies `role="button"` unconditionally
 * whenever `nativeButton` is false — there is no prop that suppresses it — so that combination
 * produced an `<a href>` announcing itself as a button. Assistive technology then told the user
 * "this does something here" about a control that navigates, and the two are not interchangeable:
 * a link can be opened in a new tab, appears in a screen reader's list of links, and is activated
 * by Enter alone, while Space (which Base UI's handlers bound) does nothing on a real link.
 *
 * So this borrows only the styling. `buttonVariants` is the same `cva` the button uses, which is
 * what keeps the two visually identical; everything else is a plain anchor with a real `href`, and
 * the browser supplies the semantics.
 *
 * There is no `disabled`. A disabled link is not a thing the platform has — the honest expression
 * of "you may not go there" is not rendering the link, which is what every caller here already does
 * with its permission check.
 */
export function LinkButton({
  className,
  variant = "default",
  size = "default",
  ...props
}: ComponentProps<typeof Link> & VariantProps<typeof buttonVariants>) {
  return <Link className={cn(buttonVariants({ variant, size, className }))} {...props} />;
}
