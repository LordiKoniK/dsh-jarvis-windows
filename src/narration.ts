/**
 * How a managed session's results are announced.
 * - self: the session speaks for itself with voice-mini's `speak` tool; Jarvis
 *   spends no tokens on it and stays quiet at turn end.
 * - relay: Jarvis owns the turn end and retells the result in its own voice.
 */
export type Narration = 'self' | 'relay';

export const NARRATIONS: readonly Narration[] = ['self', 'relay'];

export function isNarration(value: unknown): value is Narration {
  return value === 'self' || value === 'relay';
}

/** A per-session override beats the global default; anything invalid falls back to it. */
export function effectiveNarration(fallback: Narration, override?: unknown): Narration {
  return isNarration(override) ? override : fallback;
}

/** Callers that predate narration modes: judging on meant Jarvis spoke, off meant it stayed quiet. */
export function legacyNarration(judgeEnabled: unknown): Narration {
  return judgeEnabled === true ? 'relay' : 'self';
}

export const SELF_NARRATION_NOTE = '(When you are done, if a speak tool is available, use it to report the result to the user in one or two spoken sentences. Do not read your written reply aloud.)';

/** What Jarvis sends a self-narrating session carries the speak request exactly once. */
export function withNarrationNote(message: string, narration: Narration): string {
  if (narration !== 'self' || message.includes(SELF_NARRATION_NOTE)) return message;
  return `${message.trimEnd()}\n\n${SELF_NARRATION_NOTE}`;
}
