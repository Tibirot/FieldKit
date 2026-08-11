/**
 * Turns a label into a key an admin would have typed.
 *
 * The key is an identifier — it goes into JSON and into future index expressions — while the label is
 * prose, so left to themselves an admin types "Chiller count" into both and the server refuses the
 * second. Deriving it removes the mismatch at the source rather than explaining it afterwards.
 *
 * **Diacritics are folded rather than replaced with underscores.** This product ships in Romanian,
 * where "Suprafață de raft" is an ordinary label. Treating `ț` and `ă` as separators collapses them
 * with the space that follows and yields `suprafa_de_raft` — two letters gone and two words merged,
 * in a key that is immutable the moment it is saved. Decomposing first gives `suprafata_de_raft`,
 * which is what someone transliterating by hand would have written.
 *
 * It has its own module because it now has two callers with the same problem and the same rule: the
 * custom-field form (`CFG-01`) and the survey editor (`AUD-04`), whose question keys are what answers
 * are filed under. Two copies of this would be two answers to "what is the key for *Suprafață de
 * raft*", and both are immutable once saved.
 */
export function keyFromLabel(label: string): string {
  return label
    .normalize("NFD")
    .replace(/\p{Diacritic}/gu, "")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "_")
    // A key starts with a letter, so anything before the first one cannot be kept — "3G coverage"
    // becomes `g_coverage` rather than a key the server would refuse.
    .replace(/^[^a-z]+/, "")
    .replace(/_+$/, "")
    .slice(0, 60);
}
