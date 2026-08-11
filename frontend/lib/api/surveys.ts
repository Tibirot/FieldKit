import { apiDelete, apiGet, apiSend } from "@/lib/api/client";

/**
 * What kind of answer a survey question takes (`AUD-04`).
 *
 * The server's own closed set, as a union rather than a `string`: the editor renders one control per
 * type and stores options for two of them, so "which kinds exist" is a question the compiler should
 * answer rather than one a typo could extend.
 */
export type SurveyQuestionType =
  | "Text"
  | "Number"
  | "Boolean"
  | "SingleChoice"
  | "MultiChoice"
  | "Photo";

/** In the order the type picker offers them — the spec's own (Configuration §3). */
export const SURVEY_QUESTION_TYPES: readonly SurveyQuestionType[] = [
  "Text",
  "Number",
  "Boolean",
  "SingleChoice",
  "MultiChoice",
  "Photo",
];

/**
 * Whether a type offers a list to pick from.
 *
 * Mirrors `SurveyQuestionTypes.IsChoice` for the reason that helper exists at all: every place that
 * asks has to agree, and a caller comparing against `SingleChoice` alone is how a multi-choice
 * question ends up saved with its options thrown away.
 */
export function isChoice(type: SurveyQuestionType): boolean {
  return type === "SingleChoice" || type === "MultiChoice";
}

/** One question, as stored. `order` is contiguous from 1 and assigned by the server. */
export type SurveyQuestion = {
  order: number;
  key: string;
  text: string;
  type: SurveyQuestionType;
  mandatory: boolean;
  options: string[];
};

export type SurveyForm = { id: string; name: string; questions: SurveyQuestion[] };

/** One question as the editor submits it. No order — position in the list is the order. */
export type SurveyQuestionWrite = {
  key: string;
  text: string;
  type: SurveyQuestionType;
  mandatory: boolean;
  options?: string[] | null;
};

/** The column widths and the sanity bound, so the controls refuse what the server would. */
export const SURVEY_NAME_LIMIT = 120;
export const QUESTION_TEXT_LIMIT = 300;
export const MAXIMUM_QUESTIONS = 50;

const SURVEYS = "/api/config/surveys";

export function surveysKey(subject: string): readonly unknown[] {
  return ["surveys", subject];
}

export function fetchSurveys(accessToken: string, signal?: AbortSignal): Promise<SurveyForm[]> {
  return apiGet<SurveyForm[]>(SURVEYS, accessToken, signal);
}

export function createSurvey(
  accessToken: string,
  name: string,
  questions: SurveyQuestionWrite[],
): Promise<SurveyForm> {
  return apiSend<SurveyForm>("POST", SURVEYS, accessToken, { name, questions });
}

/**
 * Replaces a form's name and **all** of its questions.
 *
 * There is no per-question endpoint, and that is the API's decision rather than an omission: order is
 * a property of the list, so a patch would have to describe moves as well as edits. The editor
 * therefore sends the whole form every time, which is why it holds a full draft rather than a diff.
 */
export function setSurvey(
  accessToken: string,
  formId: string,
  name: string,
  questions: SurveyQuestionWrite[],
): Promise<SurveyForm> {
  return apiSend<SurveyForm>("PUT", `${SURVEYS}/${formId}`, accessToken, { name, questions });
}

/**
 * Stops a form being asked. It is **not** a redaction, and it is not a loss either.
 *
 * Two things this deliberately does not do, for the same reason: Configuration owns neither of them
 * (ADR-0005). It cannot refuse the delete because an audit points at the form — that would mean
 * reading Audit's schema — and it cannot remove the answers already given, which stay in Audit's
 * rows and stay **readable**, because each one carries its question's text as it was asked.
 *
 * That last part is the difference from deleting a custom field, where the values are undescribed
 * from that moment and vanish the next time their row is saved. Here nothing is lost; the form
 * simply stops being handed out.
 */
export function deleteSurvey(accessToken: string, formId: string): Promise<void> {
  return apiDelete(`${SURVEYS}/${formId}`, accessToken);
}

/**
 * The keys that appear more than once.
 *
 * <b>Worth catching here rather than leaving to the server</b>, unlike most of this form's rules. The
 * editor *derives* keys from question text, so two questions worded alike — "Notes" on the chiller
 * and "Notes" on the gondola — silently derive the same key. The server refuses that with
 * `config.survey.duplicateKey`, which names neither question; an admin holding twelve of them cannot
 * act on it.
 *
 * A `Set` rather than indexes, because both questions are equally the problem: whichever one is
 * renamed fixes it, and marking only the second would suggest the first is the right one.
 */
export function duplicateKeys(questions: readonly { key: string }[]): Set<string> {
  const seen = new Set<string>();
  const twice = new Set<string>();

  for (const question of questions) {
    const key = question.key.trim();

    // An empty key is its own problem — one per question, and reported as "missing" rather than as
    // a collision with every other blank row.
    if (key === "") continue;

    if (seen.has(key)) twice.add(key);

    seen.add(key);
  }

  return twice;
}
