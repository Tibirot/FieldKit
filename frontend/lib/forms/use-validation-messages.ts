"use client";

import { useTranslations } from "next-intl";
import { useMemo } from "react";

import type { ValidationMessages } from "@/lib/forms/custom-field-schema";

/**
 * The validation vocabulary, in the reader's language.
 *
 * A hook so the words come from the same catalogue as everything else on the screen — a form that
 * labels a field "Cod" and then refuses it in English is a worse experience than one that was never
 * translated at all.
 *
 * Memoised because schemas are rebuilt whenever this object changes, and a new object every render
 * would rebuild them on every keystroke.
 */
export function useValidationMessages(): ValidationMessages {
  const t = useTranslations("Validation");

  return useMemo(
    () => ({
      required: t("required"),
      tooLong: (max: number) => t("tooLong", { max }),
      atMost: (max: number) => t("atMost", { max }),
      atLeast: (min: number) => t("atLeast", { min }),
      mustBeNumber: t("mustBeNumber"),
      notAnOption: t("notAnOption"),
      mustBeDate: t("mustBeDate"),
    }),
    [t],
  );
}
