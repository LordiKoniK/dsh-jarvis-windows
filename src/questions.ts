interface Dependencies {
  managed?: (session: string) => boolean;
}
interface Question {
  session?: string;
  run: () => Promise<void>;
  discard: () => void;
  cleanup: () => void;
}

/** One user question at a time, in arrival order.
 *
 *  DSH may hand several sessions' questions to the panel at once; letting them
 *  race would stack cards the user has to answer out of order, and a session
 *  released while its question waits must never be asked at all. This only
 *  serialises the questions — nothing here speaks. */
export class QuestionQueue {
  private readonly deps: Dependencies;
  private questions: Question[] = [];
  private asking = false;
  private disposed = false;

  constructor(deps: Dependencies = {}) {
    this.deps = deps;
  }

  ask<T>(fn: () => T | Promise<T>, options: { session?: string; signal?: AbortSignal } = {}): Promise<T | undefined> {
    if (this.disposed || options.signal?.aborted) return Promise.resolve(undefined);
    return new Promise<T | undefined>((resolve, reject) => {
      const question: Question = {
        session: options.session,
        run: async () => {
          try { resolve(await fn()); } catch (error) { reject(error); }
        },
        discard: () => resolve(undefined),
        cleanup: () => options.signal?.removeEventListener('abort', abort),
      };
      const abort = () => this.discardQueued(item => item === question);
      options.signal?.addEventListener('abort', abort, { once: true });
      this.questions.push(question);
      void this.drain();
    });
  }

  private async drain(): Promise<void> {
    if (this.asking) return;
    this.asking = true;
    try {
      while (!this.disposed && this.questions.length) {
        const question = this.questions.shift()!;
        question.cleanup();
        if (question.session !== undefined && this.deps.managed && !this.deps.managed(question.session)) {
          question.discard();
        } else {
          await question.run();
        }
      }
    } finally { this.asking = false; }
  }

  private discardQueued(predicate: (question: Question) => boolean): void {
    this.questions = this.questions.filter(question => {
      if (!predicate(question)) return true;
      question.cleanup();
      question.discard();
      return false;
    });
  }

  dropSession(session: string): void {
    this.discardQueued(question => question.session === session);
  }

  dispose(): void {
    this.disposed = true;
    this.discardQueued(() => true);
  }
}
