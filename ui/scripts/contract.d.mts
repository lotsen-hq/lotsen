/** Returns the committed contract types and the types that the generator writes from the committed schema. */
export function readContract(): Promise<{ committed: string; generated: string }>;

/** Writes the contract types from the committed schema, and returns the path of the file it wrote. */
export function writeContract(): Promise<string>;
