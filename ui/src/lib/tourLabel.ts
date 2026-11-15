/** A label split into the part before its pull request number, such as `org/project/repo`, and the number, such as `#1234`. */
export interface SplitLabel {
   repository: string;
   number: string;
}

/** Splits a label that ends in a space, `#` and digits, as both platforms' labels do, or returns null for any other label, which the bar shows whole. */
export function splitLabel(label: string): SplitLabel | null {
   const match = /^(.*\S) (#\d+)$/.exec(label);
   return match === null ? null : { repository: match[1]!, number: match[2]! };
}
