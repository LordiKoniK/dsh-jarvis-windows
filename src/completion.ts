import { finalReply, judgePrompt, parseRelay, parseVerdict, planTurnEnd, relayPrompt } from './judge.ts';
import type { TurnEndAction, Verdict } from './judge.ts';
import { oneShot } from './llm.ts';
import { FAILED_KINDS } from './live-state.ts';
import type { Narration } from './narration.ts';
import type { TaskLedger } from './tasks.ts';

export interface JudgeConfig {
  provider: string;
  model: string;
  judgeEnabled: boolean;
  judgeProvider: string;
  judgeModel: string;
  judgeTimeoutMs: number;
  maxContinueRounds: number;
}

interface Dependencies {
  ledger: TaskLedger;
  managed: (session: string) => boolean;
  /** Read on every decision so a switch applies to the next turn end. */
  narration?: (session: string) => Narration;
  config: () => JudgeConfig;
  llm: () => unknown;
  messages: (session: string) => unknown;
  title: (session: string) => string | Promise<string>;
  say: (text: string) => Promise<unknown>;
  notify: (text: string) => void;
  onError: (error: unknown) => void;
}

/** Async completion work is tied to both a task id and a per-session turn.
 * The event listener never waits for a model or for the user's answer. */
export class CompletionJudge {
  private readonly deps: Dependencies;
  private readonly versions = new Map<string, number>();
  private readonly jobs = new Map<string, AbortController>();
  private readonly questions = new Map<string, { task: string; version: number }>();
  private disposed = false;

  constructor(deps: Dependencies) { this.deps = deps; }

  turnStarted(session: string): void { this.invalidate(session); }

  invalidate(session: string): void {
    this.versions.set(session, (this.versions.get(session) ?? 0) + 1);
    this.jobs.get(session)?.abort();
    this.jobs.delete(session);
    this.questions.delete(session);
    const task = this.deps.ledger.current(session);
    if (task?.status === 'judging') this.deps.ledger.setStatus(task.id, 'open');
  }

  async turnEnded(session: string, reasonKind?: string): Promise<void> {
    const { ledger } = this.deps;
    if (this.disposed || !this.deps.managed(session)) return;
    const task = ledger.current(session);
    const config = this.deps.config();
    const max = Number.isFinite(config.maxContinueRounds) ? Math.max(0, Math.floor(config.maxContinueRounds)) : 2;
    const narration = () => this.deps.narration?.(session);
    const action = planTurnEnd({ managed: true, task, reasonKind, max, judgeEnabled: config.judgeEnabled,
      narration: narration() });
    if (action === 'ignore' || !task) return;
    const settleSilent = (next: TurnEndAction): boolean => {
      if (next !== 'drop' && next !== 'done-silent' && next !== 'open-silent') return false;
      this.invalidate(session);
      ledger.setStatus(task.id, next === 'drop' ? 'dropped' : next === 'done-silent' ? 'done' : 'open');
      return true;
    };
    if (settleSilent(action)) return;
    this.jobs.get(session)?.abort();
    const job = new AbortController();
    this.jobs.set(session, job);
    const version = this.versions.get(session) ?? 0;
    const active = (status: string): boolean => {
      const current = ledger.current(session);
      return !this.disposed && !job.signal.aborted && this.deps.managed(session)
        && this.jobs.get(session) === job && (this.versions.get(session) ?? 0) === version
        && current?.id === task.id && current.status === status;
    };
    ledger.setStatus(task.id, action === 'fail' ? 'open' : 'judging');
    try {
      let title = session;
      try { title = (await this.deps.title(session)).trim() || session; } catch { /* A title is optional. */ }
      // Settings can change while title/model services are pending. Replan using
      // the same policy before any speech or continuation side effect.
      if (settleSilent(planTurnEnd({ managed: this.deps.managed(session), task: ledger.current(session),
        reasonKind, max, judgeEnabled: this.deps.config().judgeEnabled, narration: narration(),
        stale: !active(action === 'fail' ? 'open' : 'judging') }))) return;
      if (action === 'fail') {
        if (active('open')) await this.deps.say(`${title} failed: ${reasonKind === 'interrupted' ? 'it was interrupted' : FAILED_KINDS[reasonKind!]}`);
        return;
      }
      if (!active('judging')) return;
      let reply = '';
      try { reply = finalReply(this.deps.messages(session)); } catch { /* Missing session messages fall back to unclear. */ }
      let verdict: Verdict = { verdict: 'unclear', summary: 'it is done' };
      if (config.judgeEnabled && reply) {
        const prompt = judgePrompt(task, reply);
        const raw = await oneShot(this.deps.llm(), {
          ...prompt, provider: config.judgeProvider.trim() || config.provider,
          model: config.judgeModel.trim() || config.model, maxTokens: 300,
          timeoutMs: config.judgeTimeoutMs, purpose: 'jarvis-judge', signal: job.signal,
        });
        verdict = parseVerdict(raw) ?? verdict;
      } else if (reply && narration() === 'relay') {
        const prompt = relayPrompt(reply);
        const raw = await oneShot(this.deps.llm(), {
          ...prompt, provider: config.judgeProvider.trim() || config.provider,
          model: config.judgeModel.trim() || config.model, maxTokens: 200,
          timeoutMs: config.judgeTimeoutMs, purpose: 'jarvis-relay', signal: job.signal,
        });
        const line = parseRelay(raw);
        if (line) verdict = { verdict: 'satisfied', summary: line };
      }
      const result = planTurnEnd({ managed: this.deps.managed(session), task: ledger.current(session),
        reasonKind, verdict, max, judgeEnabled: this.deps.config().judgeEnabled, narration: narration(),
        stale: !active('judging') });
      if (result === 'ignore' || settleSilent(result)) return;
      const recorded = { ...verdict, at: Date.now() };
      if (result === 'continue') {
        ledger.setStatus(task.id, 'unsatisfied', recorded);
        this.questions.set(session, { task: task.id, version });
        try {
          this.deps.notify([
            `[Session ${title} (id=${session}) finished this turn, judgement unsatisfied]`,
            `Original request: ${task.request}`,
            `Missing: ${verdict.missing}`,
            `Ask the user with ask_user whether to let it continue (options: Continue / No thanks), passing session=${JSON.stringify(session)} and task=${JSON.stringify(task.id)}.`,
            'If the user chooses to continue, send a concrete continuation instruction with inject_to_session; if they decline, finish. Do not continue without the user\'s consent.',
          ].join('\n'));
        } catch (error) {
          this.questions.delete(session);
          ledger.setStatus(task.id, 'open');
          this.deps.onError(error);
        }
      } else {
        // Settle before awaiting speech so its completion cannot overwrite newer work.
        ledger.setStatus(task.id, 'done', recorded);
        const text = result === 'satisfied' ? `${title}: ${verdict.summary}`
          : result === 'limit' ? `${title} isn't done yet: ${verdict.missing}; it has continued ${task.rounds} times, so it is over to you`
          : `${title} is done`;
        await this.deps.say(text);
      }
    } catch (error) {
      if (active('judging')) ledger.setStatus(task.id, 'open');
      this.deps.onError(error);
    } finally {
      if (this.jobs.get(session) === job) this.jobs.delete(session);
    }
  }

  async askContinuation(session: string, taskId: string, ask: () => Promise<string>): Promise<string> {
    let question = this.questions.get(session);
    const current = this.deps.ledger.current(session);
    // A persisted notice can be answered after reload, before any new activity in this session.
    if (!question && !this.versions.has(session) && current?.id === taskId && current.status === 'unsatisfied') {
      question = { task: taskId, version: 0 };
      this.questions.set(session, question);
    }
    const active = () => !this.disposed && this.deps.managed(session)
      && this.questions.get(session) === question && question?.task === taskId
      && question.version === (this.versions.get(session) ?? 0)
      && this.deps.ledger.current(session)?.id === taskId
      && this.deps.ledger.current(session)?.status === 'unsatisfied';
    if (!active()) throw new Error('The continuation task has changed; do not send the old continuation instruction');
    const answer = await ask();
    if (!active()) return 'The task has changed, so this answer is no longer valid; do not send the old continuation instruction';
    // "No thanks" is the decline label this build offers. A question persisted by
    // the earlier Chinese build still shows its old decline label on the pending
    // card, so the legacy spelling stays this build's only deliberate CJK literal.
    if (/^(?:no thanks|不用了)(?:[;；]|$)/i.test(answer.trim())) this.deps.ledger.setStatus(taskId, 'dropped');
    if (answer.trim()) this.questions.delete(session);
    return answer;
  }

  dispose(): void {
    this.disposed = true;
    for (const session of this.jobs.keys()) this.invalidate(session);
    this.questions.clear();
    this.versions.clear();
  }
}
