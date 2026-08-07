"use client";

import { Vocabulary } from "@/components/back-office/vocabulary-browser";
import {
  brandsKey,
  categoriesKey,
  fetchBrands,
  fetchCategories,
  fetchTaxClasses,
  taxClassesKey,
} from "@/lib/api/products";
import { useAuth } from "@/components/auth-provider";

/**
 * The three classification vocabularies, stacked (`PRD-01`).
 *
 * Each loads independently rather than behind one combined query. A tenant with brands but no tax
 * classes should see the brands — and if one call fails, the other two are still usable, which a
 * single `Promise.all` would take away for no gain.
 */
export function ClassificationBrowser() {
  const { user } = useAuth();
  const subject = user?.profile.sub ?? "";

  return (
    <div className="flex flex-col gap-8">
      {/* Categories first: they are the one with structure, and the one a tenant thinks about
          before deciding which brands sit where. */}
      <Vocabulary
        kind="categories"
        queryKey={categoriesKey(subject)}
        fetcher={fetchCategories}
        hierarchical
      />
      <Vocabulary kind="brands" queryKey={brandsKey(subject)} fetcher={fetchBrands} />
      <Vocabulary kind="tax-classes" queryKey={taxClassesKey(subject)} fetcher={fetchTaxClasses} />
    </div>
  );
}
