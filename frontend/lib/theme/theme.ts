/**
 * Which palette the app is wearing, and who decided (W12½ slice 7, `A7`).
 *
 * **The tokens were already there.** `globals.css` has carried a complete light and dark set since
 * W2, and has honoured a `.dark` / `.light` class on `<html>` as an override the whole time — with
 * nothing setting it, so the device preference decided and a person had no say. This is the thing
 * that sets it.
 *
 * **Light is the default, not the fallback.** An unset choice resolves to `light` rather than to the
 * device, which is what "light theme by default" means and is a real change: a laptop set to dark
 * now opens FieldKit in light until somebody says otherwise. `system` is offered as the third state
 * precisely so that saying otherwise can mean *go back to following my device* — dropping it would
 * make the app override a preference the operating system already carries, with no way back.
 *
 * **What it costs is a property that used to be free.** Resolving in CSS alone made a flash of the
 * wrong theme impossible on a cold offline start. A stored choice has to be applied by
 * {@link THEME_BOOTSTRAP} before first paint, and that script has to survive the service worker
 * serving this document from cache. See the note on the script itself.
 */

export const THEMES = ["light", "dark", "system"] as const;

export type Theme = (typeof THEMES)[number];

/** Namespaced, because this device's IndexedDB and its outbox share the origin. */
export const THEME_STORAGE_KEY = "fieldkit.theme";

export const DEFAULT_THEME: Theme = "light";

export function isTheme(value: unknown): value is Theme {
  return typeof value === "string" && (THEMES as readonly string[]).includes(value);
}

/**
 * Which class `<html>` should carry — `null` meaning **neither**, which is how `system` is spelled.
 *
 * `globals.css` reads an absent class as "obey `prefers-color-scheme`", so following the device is
 * the absence of an instruction rather than an instruction of its own. That is why this returns a
 * nullable rather than a third class name: there is no `.system` for it to return.
 */
export function classFor(theme: Theme): "light" | "dark" | null {
  return theme === "system" ? null : theme;
}

/** Puts `theme` on the document. The only writer, so the two class names live in one place. */
export function applyTheme(theme: Theme, root: HTMLElement): void {
  const wanted = classFor(theme);

  root.classList.remove("light", "dark");

  if (wanted) root.classList.add(wanted);
}

/**
 * What the device last chose, or the default.
 *
 * Storage throws rather than returning null in a few real cases — Safari's private mode, a locked
 *-down enterprise profile, an iframe with third-party storage blocked. A theme is not worth a blank
 * page, so an unreadable store reads as "no choice yet".
 */
export function storedTheme(): Theme {
  try {
    const stored = globalThis.localStorage?.getItem(THEME_STORAGE_KEY);

    return isTheme(stored) ? stored : DEFAULT_THEME;
  } catch {
    return DEFAULT_THEME;
  }
}

/** Remembers `theme` for next time. Silent if the store refuses, for the reason above. */
export function rememberTheme(theme: Theme): void {
  try {
    globalThis.localStorage?.setItem(THEME_STORAGE_KEY, theme);
  } catch {
    // A preference that cannot be saved is still a preference that applies to this session.
  }
}

/*
 * A store, so a control can read the theme with `useSyncExternalStore`.
 *
 * **Because the server and the browser cannot agree here, and that is not a bug.** The choice lives
 * in `localStorage`, which the server cannot see — so a control rendered on the server shows the
 * default and the same control in the browser shows `dark`, and React reports that as a hydration
 * mismatch. `useSyncExternalStore` is the one hook that models it honestly: a server snapshot, a
 * client snapshot, and a re-render between them rather than an error.
 *
 * The alternative — `useState` seeded from storage — is what this was first, and it hydrated with
 * `aria-checked` on the wrong option and a console full of mismatch. Suppressing that warning would
 * have hidden a real disagreement instead of describing one.
 */
const listeners = new Set<() => void>();

export function subscribeToTheme(listener: () => void): () => void {
  listeners.add(listener);

  return () => {
    listeners.delete(listener);
  };
}

/** What the server renders before it can know: the default, which is what an unset choice means. */
export function serverTheme(): Theme {
  return DEFAULT_THEME;
}

/** Chooses `theme`: applies it, remembers it, and tells every control that reads it. */
export function chooseTheme(theme: Theme, root: HTMLElement): void {
  applyTheme(theme, root);
  rememberTheme(theme);

  for (const listener of listeners) listener();
}

/**
 * The same decision, written again for a `<script>` that runs **before the first paint**.
 *
 * <b>Why it is duplicated at all.</b> Everything above is a module: it is fetched, parsed and run
 * after the document has painted, which is exactly one paint too late — the page would appear in
 * whatever the CSS resolved to and then change, and a colour scheme changing under the reader is
 * worse than the wrong one arriving. So this runs inline, in the `<head>`, before the body exists,
 * and cannot import anything.
 *
 * <b>Two things make the duplication safe.</b> It is one statement rather than a copy of the logic —
 * the class names and the storage key are interpolated from the constants above, so a rename cannot
 * leave this behind. And `theme.test.ts` runs this string against a real DOM for all three values
 * and compares the result with {@link applyTheme}, which is the same argument the pricing vectors
 * make: two implementations of one rule are safe only while something compares them.
 *
 * <b>It needs the CSP nonce.</b> `script-src` is `'self' 'nonce-…' 'strict-dynamic'`, so an inline
 * script without one is refused and the theme silently reverts to the device preference. The layout
 * reads the nonce off the `x-nonce` request header the proxy sets.
 */
export const THEME_BOOTSTRAP = `try{
var t=localStorage.getItem(${JSON.stringify(THEME_STORAGE_KEY)});
if(t!=="dark"&&t!=="system")t=${JSON.stringify(DEFAULT_THEME)};
if(t!=="system")document.documentElement.classList.add(t)
}catch(e){document.documentElement.classList.add(${JSON.stringify(DEFAULT_THEME)})}`
  .replace(/\n/g, "");
