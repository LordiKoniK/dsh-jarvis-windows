/**
 * DSH wiring for the Jarvis commander and its independent worker sessions.
 *
 * apply() connects the task ledger, turn/end completion judge, output
 * coordinator, approval records, TTS and optional native-panel HTTP routes.
 * createAgent/resume setup scopes the commander persona and tools to Jarvis;
 * workers keep their own prompts and transcripts. Continuation waits for the
 * user's answer after turn/end, then Jarvis delivers a new UserMessage.
 *
 * Explicit memory tools and filtered temp capture use scoped stores; optional
 * DSH settings update runtime preferences, including opt-in low-risk question answering.
 * Pure logic lives in sibling modules so it can be tested without DSH.
 *
 * @module dsh-harness-jarvis
 */
import type { Context } from '@deepseek-ai/cordis';
import z from '@deepseek-ai/schemastery';
import { defineTool } from '@deepseek-ai/dsh-tools';
import { MessageId } from '@deepseek-ai/dsh-llm';
import type { UserMessage } from '@deepseek-ai/dsh-llm';
import type { PromptAssembly } from '@deepseek-ai/dsh-system-prompt';
import { homedir } from 'node:os';
import { AsyncLocalStorage } from 'node:async_hooks';
import { join, dirname } from 'node:path';
import { mkdirSync, readFileSync, writeFileSync, existsSync } from 'node:fs';
import { spawn } from 'node:child_process';
import type { ChildProcess } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import type { IncomingMessage, ServerResponse } from 'node:http';
import { createHash, randomBytes, randomInt, randomUUID } from 'node:crypto';
import { LiveState } from './live-state.ts';
import { deliver, visibleWorkers, workspaceName } from './sessions.ts';
import { ManagedSet } from './managed.ts';
import { effectiveNarration, isNarration, withNarrationNote, type Narration } from './narration.ts';
import { TaskLedger } from './tasks.ts';
import { approvalOperation, fingerprint, type ApprovalRecord } from './approvals.ts';
import { MemoryStore } from './memory/store.ts';
import { MemorySection } from './memory/section.ts';
import { mergeSettings, SETTINGS_LIMITS } from './settings.ts';
import { AskInterceptor } from './ask-intercept.ts';
import { captureTemp, type CaptureSession } from './memory/capture.ts';
import { remember, recall, type RememberArgs, type RecallArgs } from './memory/tools.ts';
import { writeApproval } from './approval-store.ts';
import { ApprovalPresets, validRuleKey, type ApprovalRuleKey } from './approval-presets.ts';
import { canPresetApproval, type PresetOperation } from './approval-risk.ts';
import { classify, type ApprovalOperation } from './approval-tier.ts';
import { AutoApproval, type AutoApprovalResult } from './auto-approval.ts';
import { AutoApprovalState } from './auto-approval-state.ts';
import { readApprovalHistory } from './approval-history.ts';
import { CompletionJudge } from './completion.ts';
import { OutputCoordinator } from './output.ts';
import { claimsTurnEnd } from './turn-end.ts';
import { sessionRow, type SessionRow } from './session-state.ts';
import { needsRename } from './title.ts';
import { PanelSupervisor } from './panel-supervisor.ts';
import { cleanupSpeechCache, RotatingLog } from './maintenance-files.ts';
import { routedInput, toConversation } from './conversation.ts';

/** Package root (lib/ → parent). Resolves bundled scripts/synth-edge.mjs. */
const here = dirname(fileURLToPath(import.meta.url));
const pkgRoot = join(here, '..');

/** The host half is platform-neutral except for three seams — the panel binary,
 *  the speech player and raising the DSH window. macOS keeps its original paths. */
const onWindows = process.platform === 'win32';

/** Python 3.13 from the per-user install — the interpreter this deployment has
 *  kokoro-onnx, onnxruntime and numpy installed in. Overridable via `pythonPath`. */
const defaultPythonPath = onWindows
  ? join(homedir(), 'AppData', 'Local', 'Programs', 'Python', 'Python313', 'python.exe')
  : 'python3';

/** Kokoro model directory: the DSH data directory, so it is neither inside this
 *  package nor tied to one user's home layout. Overridable via `kokoroModelDir`. */
const defaultKokoroModelDir = join(homedir(), '.dsh', 'models', 'kokoro');

/** Captured host webServer origin once webServer injects fire — reused to call
 *  voice-mini's same-host /voice-mini/test endpoint for chime+TTS greetings. */
let webOrigin: string | null = null;

/** The Jarvis agent handle (from create/resume). Stored so /jarvis/input
 *  can inject user messages into the agent (user → jarvis → response). */
let jarvisHandle: { agent: any } | null = null;

/** Creation surface of the `agents` service: `create`/`resume` are traced
 *  through the SERVICE's own context, so the agent's session stays attached to
 *  the SessionStore for the whole app lifetime instead of dying with the
 *  plugin context that asked for it. */
interface AgentsService {
  get(id: string): any;
  create(options: Record<string, unknown>): Promise<{ agent: any }>;
  resume(options: Record<string, unknown>): Promise<{ agent: any }>;
}

/** The persisted-session existence probe. A session whose file exists must be
 *  resumed; `agents.create` refuses an id the SessionStore still holds. */
interface SessionStoreService {
  get(id: string): unknown;
}

/** One Jarvis agent resolved for this apply — live-and-reused, resumed, or freshly created. */
interface SettledJarvis {
  handle: { agent: any };
  /** True when persisted history was behind the session, which is the greeting path. */
  existed: boolean;
}

/** Set from the panel's voice button; suppresses greetings until unmuted. */
let voiceMuted = false;
let playingChild: ChildProcess | null = null;

/** Session titles for /jarvis/state, refreshed at most every 10s. */
const titleCache: { at: number; map: Record<string, string> } = { at: 0, map: {} };

/** Stops the built-in speech child (macOS afplay / Windows PowerShell player).
 *  voice-mini playback is out of our reach. */
function stopSpeech(): void {
  // Windows has no SIGTERM to deliver — there kill() terminates the child
  // outright, which ends its audio with it.
  if (playingChild && !playingChild.killed) { try { playingChild.kill('SIGTERM'); } catch {} }
  playingChild = null;
}

/** Worker session ids from the agents registry, excluding Jarvis itself (the
 *  registry keys it by its DSH UUID, so also match the live handle's id). */
function listWorkerIds(ctx: Context, entry: JarvisConfig): string[] {
  const agents = (ctx as any).get?.('agents');
  let ids: string[] = [];
  if (agents) {
    const pick = (a: any) => typeof a === 'string' ? a : (a?.id ?? a?.sessionId ?? '');
    if (typeof agents.keys === 'function') ids = [...agents.keys()];
    else if (typeof agents.list === 'function') ids = agents.list().map(pick);
    else if (typeof agents[Symbol.iterator] === 'function') ids = [...agents].map(pick);
  }
  let archived: unknown[] = [];
  try { archived = [...((ctx as any).get?.('workspaceRegistry')?.archivedSessionIds ?? [])]; } catch { /* registry not started yet */ }
  return visibleWorkers(ids, [entry.jarvisSessionId, String(jarvisHandle?.agent?.id ?? ''), ...archived]);
}

/** Folder of a live session's cwd, so two sessions titled alike can be told apart. */
function sessionWorkspace(ctx: Context, id: string): string | undefined {
  try { return workspaceName((ctx as any).get?.('sessions')?.get?.(id)?.header?.cwd); } catch { return undefined; }
}

/** Batch-reads titles via sessionQuery.readTitleSnapshots.
 *  Actual shape: [{ sessionId, status, value: { session, title: { title } } }] */
async function readTitleMap(ctx: Context, ids: string[]): Promise<{ map: Record<string, string>; diag: string }> {
  const map: Record<string, string> = {};
  try {
    const sessionQuery = (ctx as any).get?.('sessionQuery');
    if (!sessionQuery?.readTitleSnapshots || ids.length === 0) {
      return { map, diag: 'sq=' + (sessionQuery ? 'found' : 'missing') + ' rts=' + (sessionQuery?.readTitleSnapshots ? 'fn' : 'no') + ' ids=' + ids.length };
    }
    const snaps = await sessionQuery.readTitleSnapshots(ids);
    if (Array.isArray(snaps)) {
      snaps.forEach((s: any, i: number) => {
        const sid = String(s?.sessionId ?? s?.id ?? ids[i] ?? '');
        const t = String(s?.value?.title?.title ?? s?.value?.title ?? s?.title?.title ?? s?.title ?? '');
        if (sid && t) map[sid] = t;
      });
      return { map, diag: 'array[' + snaps.length + '] titles=' + Object.values(map).join(' | ').slice(0, 200) };
    }
    if (snaps && typeof snaps === 'object') {
      for (const [k, v] of Object.entries(snaps as any)) {
        map[String(k)] = String((v as any)?.value?.title?.title ?? (v as any)?.title?.title ?? (v as any)?.title ?? '');
      }
      return { map, diag: 'object[' + Object.keys(snaps).length + ']' };
    }
    return { map, diag: '(empty)' };
  } catch (e) {
    return { map, diag: 'err: ' + (e instanceof Error ? e.message : String(e)) };
  }
}

/** /jarvis/input wraps session-targeted text in this instruction; history shows the original. */
export const name = 'dsh-harness-jarvis';

/** Hard-required services. llm / systemPrompt / webServer / settings deferred. */
export const inject = ['tools', 'userQuestions', 'jobs'] as const;

export const Config = z.object({
  locale: z.union(['zh', 'en']).default('zh'),
  audioDir: z.string().default('~/.dsh/jarvis'),
  managedFile: z.string().default('~/.dsh/jarvis/managed.json'),
  tasksFile: z.string().default('~/.dsh/jarvis/tasks.json'),
  memoryRoot: z.string().default('~/.dsh/jarvis'),
  approvalsDir: z.string().default('~/.dsh/jarvis/memory/approvals'),
  lockFile: z.string().default('~/.dsh/jarvis/lock.json'),
  runtimeFile: z.string().default('~/.dsh/jarvis/runtime.json'),
  titlePrefix: z.string().default('Jarvis-'),
  // Empty means "unset": resolveTtsBackend() then picks the platform default
  // (kokoro on Windows, edge elsewhere). A schema default of 'edge' filled the
  // field in on every platform, which made that platform fallback dead code and
  // silently pointed Windows at the cloud backend. An explicit 'edge' or
  // 'kokoro' still wins, so existing profiles keep their chosen backend.
  ttsBackend: z.union(['', 'edge', 'kokoro']).default(''),
  edgeVoice: z.string().default('zh-CN-YunjianNeural'),
  kokoroVoice: z.string().default('af_heart'),
  pythonPath: z.string().default(defaultPythonPath),
  kokoroModelDir: z.string().default(defaultKokoroModelDir),
  // fp32, never the int8 export: int8 measured a real-time factor of ~1.15 on
  // this machine — slower than playback — against ~0.32 for fp32.
  kokoroModelFile: z.string().default('kokoro-v1.0.onnx'),
  kokoroVoicesFile: z.string().default('voices-v1.0.bin'),
  // Deliberately not every core. Uncapped inference drove the package to ~89 C
  // and into thermal throttling; 4 threads still runs 2.5x faster than playback.
  kokoroThreads: z.number().default(4),
  archiveIdleDays: z.number().default(7),
  judgeEnabled: z.boolean().default(true),
  askInterception: z.boolean().default(false),
  autoApprove: z.union(['off', 'safe', 'safe+grey']).default('off'),
  managedNarration: z.union(['self', 'relay']).default('self'),
  judgeProvider: z.string().default(''),
  judgeModel: z.string().default(''),
  judgeTimeoutMs: z.number().default(20_000),
  maxContinueRounds: z.number().default(2),
  // Jarvis agent creation (§9.1). Empty means "auto": the first available
  // provider from the llm service and that provider's first model, resolved
  // when the agent is created — never a hardcoded vendor default.
  provider: z.string().default(''),
  model: z.string().default(''),
  // Deliberately empty by default: DSH resolves a reasoning effort of its own
  // when none is passed, and an explicit default here would silently change the
  // agent for every existing user. Empty means "pass nothing at all".
  reasoningEffort: z.string().default(''),
  jarvisSessionId: z.string().default('jarvis'),
  jarvisCwd: z.string().default('~/jarvis'),
  // Restart welcome greetings (random pick). The built-in English list
  // [Welcome back!, Welcome!] is always included; entries here are ADDED.
  greetings: z.array(z.string()).default([]),
});

/** The settings page intentionally omits composition-owned paths and identity. */
export const SettingsSchema = z.object({
  provider: Config.dict!.provider.description('Model provider for Jarvis. Leave empty to pick the first available provider automatically. Takes effect after a DSH restart.'),
  model: Config.dict!.model.description('Model for Jarvis. Leave empty to pick that provider\'s first model automatically. Takes effect after a DSH restart.'),
  reasoningEffort: Config.dict!.reasoningEffort.description('Reasoning effort for Jarvis (for example low / medium / high, depending on the provider). Leave empty to let DSH decide. Takes effect after a DSH restart.'),
  edgeVoice: Config.dict!.edgeVoice.description('Built-in speech voice (used when voice-mini is unavailable). Takes effect on the next announcement.'),
  greetings: Config.dict!.greetings.description('Extra welcome messages, picked at random for the restart greeting.'),
  askInterception: Config.dict!.askInterception.description('Let Jarvis answer low-risk multiple-choice questions in managed sessions for you. Off by default; anything uncertain still comes to you.'),
  autoApprove: Config.dict!.autoApprove.description('Automatic approval: off disables it, safe covers only the read-only allowlist, safe+grey adds the grey-area model judgement plus medium-risk presets and history. High risk always comes to you. Takes effect on the next request.'),
  managedNarration: Config.dict!.managedNarration.description('Default announcement style for managed sessions: self has the session report with its own speak tool (saves Jarvis tokens), relay has Jarvis summarise and retell it in its own words. Switchable per session in the panel. Takes effect at the end of the next turn.'),
  judgeEnabled: Config.dict!.judgeEnabled.description('Enable task-completion judging. Takes effect on the next judgement.'),
  judgeProvider: Config.dict!.judgeProvider.description('Model provider used for judging. Leave empty to use the current Jarvis provider.'),
  judgeModel: Config.dict!.judgeModel.description('Model used for judging. Leave empty to use the current Jarvis model.'),
  judgeTimeoutMs: Config.dict!.judgeTimeoutMs.min(SETTINGS_LIMITS.minTimeout).max(SETTINGS_LIMITS.maxTimeout).step(1)
    .description('Judging timeout in milliseconds (1000–120000). Takes effect on the next judgement.'),
  maxContinueRounds: Config.dict!.maxContinueRounds.min(0).max(SETTINGS_LIMITS.maxRounds).step(1)
    .description('Maximum number of continuation rounds (0–10); 0 never asks about continuing.'),
}).description('Jarvis');

interface JarvisConfig {
  locale: string;
  audioDir: string;
  managedFile: string;
  tasksFile: string;
  memoryRoot: string;
  approvalsDir: string;
  lockFile: string;
  runtimeFile: string;
  titlePrefix: string;
  ttsBackend: string;
  edgeVoice: string;
  kokoroVoice: string;
  pythonPath: string;
  kokoroModelDir: string;
  kokoroModelFile: string;
  kokoroVoicesFile: string;
  kokoroThreads: number;
  archiveIdleDays: number;
  judgeEnabled: boolean;
  askInterception: boolean;
  autoApprove: 'off' | 'safe' | 'safe+grey';
  managedNarration: Narration;
  judgeProvider: string;
  judgeModel: string;
  judgeTimeoutMs: number;
  maxContinueRounds: number;
  provider: string;
  model: string;
  reasoningEffort: string;
  jarvisSessionId: string;
  jarvisCwd: string;
  greetings: string[];
}

function resolveDir(p: string): string {
  return p.startsWith('~') ? join(homedir(), p.slice(1)) : p;
}

/** File-based debug logging (bypasses ctx.logger which is invisible from CLI). */
const debugLogs = new WeakMap<JarvisConfig, RotatingLog>();
function debug(entry: JarvisConfig, msg: string): void {
  try {
    let log = debugLogs.get(entry);
    if (!log) {
      log = new RotatingLog(join(resolveDir(entry.audioDir), 'debug.log'));
      debugLogs.set(entry, log);
    }
    log.write(`${new Date().toISOString()} ${msg}\n`);
  } catch { /* silent */ }
}

/** Agent options for createAgent/resume. reasoningEffort is omitted entirely
 *  when unset, so DSH resolves its own default exactly as it did before this
 *  key existed; passing an empty string would be a different request. */
function agentOptions(entry: JarvisConfig): { provider: string; model: string; reasoningEffort?: string } {
  const effort = entry.reasoningEffort.trim();
  return { provider: entry.provider, model: entry.model, ...(effort ? { reasoningEffort: effort } : {}) };
}

const COMMANDER_PERSONA = [
  '## You are Jarvis',
  'You are the user\'s butler and commander. You look after several sessions (workers): the user selects one in the panel and types to you, and you improve the wording before sending it to that session with inject_to_session.',
  '- You may only send content to sessions in the managed set. When the user says "hand session X over to you" or "you look after it", first find its id with list_managed, then use manage_session; when they say "you can stop looking after it", use release_session.',
  '- You do not pile worker content into your own memory; workers stay clean. You only handle routing, spoken announcements, inject decisions and continuation judgement.',
  '- You cannot inject_to_session to yourself.',
  '- A managed session has two announcement styles: self has the session report with its own speak tool (the system appends the report request automatically when you forward, so do not repeat it), and relay has you retell the result once it is done. When the user says "let it report for itself" or "you retell it", switch with set_session_narration.',
  '- Use say_to_user to speak to the user; use ask_user when the user has to make the call, then continue once you have the answer.',
  '- When you receive a notice beginning [Session … judgement unsatisfied], do what the notice asks: ask with ask_user and pass the session and task it carries, and do not decide about continuing yourself. Only send a concrete continuation instruction with inject_to_session if the user chooses to continue; finish if they decline. If the answer is no longer valid, do not send the old continuation instruction again.',
  '- For approvals you only relay the user\'s decision; never approve on your own initiative.',
  '- When the user wants to see or revoke approval presets, use list_approval_rules / remove_approval_rule; pass fingerprint, tool and workspace back unchanged when removing. A preset can only be created when the user clicks always allow on an approval card; whether approvals run automatically is decided by the user\'s settings, and you must not widen the authorisation yourself.',
  "- When the user says 'remember …', use remember; use recall first when you need to recall a past agreement or decision.",
  '- One or two sentences; never read out code, paths or markdown.',
].join('\n');

/** What Jarvis's tools need from the running plugin. */
interface JarvisDeps {
  managed: ManagedSet;
  ledger: TaskLedger;
  memory: MemoryStore;
  presets: ApprovalPresets;
  removeApprovalRule: (key: ApprovalRuleKey) => Promise<boolean>;
  sessionRows: () => Promise<SessionRow[]>;
  setManaged: (id: string, on: boolean) => Promise<'ok' | 'not-found'>;
  narration: (id: string) => Narration;
  setNarration: (id: string, narration: Narration | undefined) => 'ok' | 'not-managed';
  say: (text: string) => Promise<SpeakResult>;
  ask: (question: string, choices: string[], exec?: { agent?: unknown; signal?: AbortSignal },
    continuation?: { session: string; task: string }) => Promise<string>;
}

type SpeakResult = 'voice-mini' | 'built-in' | 'muted' | 'failed';

/** What the rest of the plugin needs from a speech backend, and nothing more:
 *  synthesize a file, then play it. */
interface SpeechEngine {
  synthesize(text: string, outFile: string): Promise<{ path: string; ms: number }>;
  play(file: string): Promise<void>;
}

/** Built-in TTS. Both backends synthesize in a child process and are judged by
 *  the output FILE, so a broken engine degrades to `failed` instead of taking
 *  the host down with it, then play through the platform player (afplay on
 *  macOS, WPF's MediaPlayer on Windows). No LLM. */
function makeBuiltInTTS(entry: JarvisConfig): SpeechEngine {
  return resolveTtsBackend(entry) === 'kokoro' ? makeKokoroTTS(entry) : makeEdgeTTS(entry);
}

/** Which backend actually speaks. An unset `ttsBackend` (the schema default is
 *  now the empty string) selects the platform backend: Windows takes kokoro
 *  because the edge-tts cloud path is verified broken there (the WebSocket
 *  upgrade to speech.platform.bing.com is refused with 403, so no file is ever
 *  produced) and local synthesis also removes the ISP dependency; macOS keeps
 *  edge — nothing there is broken. An explicit value always wins, so `edge`
 *  remains available on Windows. */
function resolveTtsBackend(entry: JarvisConfig): string {
  const configured = entry.ttsBackend.trim();
  if (configured === 'kokoro' || configured === 'edge') return configured;
  return onWindows ? 'kokoro' : 'edge';
}

/** Start a speech child. Keeping this the single spawn site is what makes the
 *  ELECTRON_RUN_AS_NODE contract impossible to forget. */
function spawnSynth(cmd: string, args: string[]): ChildProcess {
  // ELECTRON_RUN_AS_NODE: plain Node, not Electron. edge-tts is 100% reliable in
  // plain Node but flakes ~50% under Electron's network stack, and a Node synth
  // script handed to the Electron binary would otherwise open a second window.
  const env = { ...process.env, ELECTRON_RUN_AS_NODE: '1' };
  return spawn(cmd, args, { stdio: ['ignore', 'pipe', 'pipe'], env });
}

/** Spawn a synth child and report its exit code + stderr tail. The caller
 *  decides success from the output file, never from this alone. */
function runChild(cmd: string, args: string[]): Promise<{ code: number; stderr: string }> {
  return new Promise((res, rej) => {
    const p = spawnSynth(cmd, args);
    let stderr = '';
    p.stderr?.on('data', (d: Buffer) => { stderr += d.toString(); });
    p.on('error', rej);
    p.on('close', (code) => res({ code: code ?? -1, stderr }));
  });
}

/** The one playback implementation for synthesized files. macOS hands the file
 *  to `afplay`; Windows has no afplay, so PowerShell plays it through WPF's
 *  MediaPlayer (PresentationCore ships with .NET Framework — nothing to
 *  install). MediaPlayer decodes WAV exactly as it decodes MP3, so both
 *  backends share this. Either child *is* the playback, so playingChild still
 *  means "something is playing" and stopSpeech() can end it by killing the child. */
function spawnSpeech(file: string): ChildProcess {
  if (!onWindows) return spawn('afplay', [file], { stdio: 'ignore' });
  // MediaPlayer decodes asynchronously without a dispatcher pump, so the script
  // polls Position until the duration it reported has played. The two deadlines
  // keep an undecodable file from pinning a live child (and playingChild)
  // forever. The path sits in a single-quoted PowerShell string, so `'` doubles.
  const script = [
    "$ErrorActionPreference='Stop'",
    'Add-Type -AssemblyName PresentationCore',
    '$p=New-Object System.Windows.Media.MediaPlayer',
    `$p.Open([uri]'${file.replace(/'/g, "''")}')`,
    '$p.Play()',
    '$give=(Get-Date).AddSeconds(15)',
    'while(-not $p.NaturalDuration.HasTimeSpan -and (Get-Date) -lt $give){Start-Sleep -Milliseconds 50}',
    'if(-not $p.NaturalDuration.HasTimeSpan){exit 1}',
    '$end=(Get-Date).Add($p.NaturalDuration.TimeSpan).AddSeconds(5)',
    'while($p.Position -lt $p.NaturalDuration.TimeSpan -and (Get-Date) -lt $end){Start-Sleep -Milliseconds 50}',
    '$p.Stop();$p.Close()',
  ].join(';');
  // windowsHide: a console child of the GUI host would otherwise flash a window.
  return spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script],
    { stdio: 'ignore', windowsHide: true });
}

/** Playback shared by both backends: the child's lifetime IS the utterance. */
function playSpeech(file: string): Promise<void> {
  return new Promise((res) => {
    const p = spawnSpeech(file);
    playingChild = p;
    const done = () => { if (playingChild === p) playingChild = null; res(); };
    p.on('close', done);
    p.on('error', done);
  });
}

/** edge-tts (free Microsoft neural voices). Kept as the `edge` option and as
 *  the macOS default; on Windows the WebSocket upgrade is refused with 403. */
function makeEdgeTTS(entry: JarvisConfig): SpeechEngine {
  const synthScript = join(pkgRoot, 'scripts', 'synth-edge.mjs');
  return {
    async synthesize(text: string, outFile: string): Promise<{ path: string; ms: number }> {
      const t0 = Date.now();
      mkdirSync(dirname(outFile), { recursive: true });
      const { code, stderr } = await runChild(process.execPath, [synthScript, text, outFile, entry.edgeVoice, '+0%']);
      if (code !== 0 || !existsSync(outFile)) {
        throw new Error('edge-tts synth failed (child exit=' + code + (stderr ? ', stderr=' + stderr.slice(0, 200) : '') + ')');
      }
      return { path: outFile, ms: Date.now() - t0 };
    },
    play: playSpeech,
  };
}

/** Local Kokoro on ONNX Runtime's CPU provider, driven by our own
 *  scripts/kokoro_worker.py through scripts/synth-kokoro.mjs. Nothing leaves the
 *  machine, so a cloud outage cannot silence Jarvis. */
function makeKokoroTTS(entry: JarvisConfig): SpeechEngine {
  const synthScript = join(pkgRoot, 'scripts', 'synth-kokoro.mjs');
  const workerScript = join(pkgRoot, 'scripts', 'kokoro_worker.py');
  return {
    async synthesize(text: string, outFile: string): Promise<{ path: string; ms: number }> {
      const t0 = Date.now();
      mkdirSync(dirname(outFile), { recursive: true });
      const { code, stderr } = await runChild(process.execPath, [
        synthScript,
        '--text', text,
        '--out', outFile,
        '--voice', entry.kokoroVoice,
        '--threads', String(entry.kokoroThreads),
        '--model-dir', resolveDir(entry.kokoroModelDir),
        '--model-file', entry.kokoroModelFile,
        '--voices-file', entry.kokoroVoicesFile,
        '--python', entry.pythonPath,
        '--worker', workerScript,
      ]);
      if (code !== 0 || !existsSync(outFile)) {
        throw new Error('kokoro synth failed (child exit=' + code + (stderr ? ', stderr=' + stderr.slice(0, 200) : '') + ')');
      }
      return { path: outFile, ms: Date.now() - t0 };
    },
    play: playSpeech,
  };
}

// ── HTTP helpers (copied from voice-mini pet.ts pattern) ─────────────
function sendJson(res: ServerResponse, status: number, body: unknown): void {
  const json = JSON.stringify(body);
  res.writeHead(status, { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(json) });
  res.end(json);
}

/** Read the full POST body as a string (for JSON parsing). */
function readBody(req: IncomingMessage): Promise<string> {
  return new Promise((resolve) => {
    let data = '';
    req.on('data', (chunk: Buffer) => { data += chunk.toString(); });
    req.on('end', () => resolve(data));
    req.on('error', () => resolve(''));
  });
}
function isLoopbackReq(req: IncomingMessage): boolean {
  const ip = req.socket?.remoteAddress ?? '';
  return ip === '127.0.0.1' || ip === '::1' || ip === '::ffff:127.0.0.1';
}
function isAuthorized(req: IncomingMessage, token: string): boolean {
  if (req.headers.authorization === `Bearer ${token}`) return true;
  try { return new URL(req.url ?? '/', 'http://127.0.0.1').searchParams.get('token') === token; } catch { return false; }
}
function loadOrCreateToken(entry: JarvisConfig): string {
  try {
    const f = resolveDir(entry.runtimeFile);
    const parsed = JSON.parse(readFileSync(f, 'utf8')) as { token?: string };
    if (typeof parsed.token === 'string' && parsed.token.length >= 16) return parsed.token;
  } catch {}
  return randomBytes(24).toString('hex');
}
function writeRuntimeFile(entry: JarvisConfig, data: Record<string, unknown>): void {
  try {
    const f = resolveDir(entry.runtimeFile);
    mkdirSync(dirname(f), { recursive: true, mode: 0o700 });
    writeFileSync(f, JSON.stringify(data, null, 2) + '\n', { encoding: 'utf8', mode: 0o600 });
  } catch {}
}

/** Spawn the native panel (macos/jarvis-panel, or windows/dist/jarvis-panel.exe on
 *  Windows) as a NON-DETACHED child of the host — i.e. the orb's lifecycle is
 *  tied to DSH Desktop ("with DSH as a dependency"): DSH up → orb up (host spawns it on
 *  webServer ready); DSH quits → orb dies with it; DSH restarts → host
 *  re-spawns. NON-detached is intentional: on macOS a detached child loses
 *  DSH's GUI/WindowServer session and the orb didn't render after restart; as a
 *  regular child it inherits the session and renders reliably (same as a manual
 *  `./jarvis-panel` launch). Windows keeps the same non-detached contract, where
 *  the disposer's kill is what ends the orb. stdio ignored + unref'd so the host
 *  never blocks on it. The panel self-guards duplicates via pidfile. */
interface PanelProcess { start(): void; dispose(): void; }
let panelOwner: PanelProcess | null = null;

/** The subset of DSH's webServer service this plugin uses. */
interface JarvisWebServer {
  host?: string;
  port?: number;
  register(route: {
    kind: 'prefix';
    path: string;
    handler: (req: IncomingMessage, res: ServerResponse) => void | Promise<void>;
  }): () => void;
}

/** Process-wide slot holding the live /jarvis route registration.
 *
 *  A plugin reload re-imports this module, so module-level state cannot be how
 *  the next instance finds what the previous one registered — and DSH's
 *  webServer treats a second registration of the same path as an ERROR rather
 *  than a replacement, keeping the FIRST handler. That combination is what let
 *  an initial load keep answering /jarvis/* from a closure whose Session had
 *  already been detached, while every later apply looked like it had not been
 *  loaded at all. A `Symbol.for` slot lives on the process, not the module. */
const ROUTE_SLOT = Symbol.for('dsh-harness-jarvis:/jarvis-route');

/** The slot table, typed once. */
function routeSlot(): Record<symbol, unknown> {
  return globalThis as unknown as Record<symbol, unknown>;
}

/** Release a route a previous module instance left registered. */
function releasePreviousRoute(entry: JarvisConfig): void {
  const slot = routeSlot();
  const previous = slot[ROUTE_SLOT];
  if (typeof previous !== 'function') return;
  try {
    (previous as () => void)();
    debug(entry, 'released the previous /jarvis route registration');
  } catch (error) {
    debug(entry, 'releasing the previous /jarvis route failed: ' + (error instanceof Error ? error.message : String(error)));
  }
  delete slot[ROUTE_SLOT];
}

/** Claim the /jarvis prefix, reclaiming a registration whose owner is gone.
 *
 *  DSH models a route as an exclusive seat: `register` throws on a duplicate
 *  (kind, path) and otherwise keeps the incumbent. An entry left by an apply
 *  from before this fix — or by a reload that died mid-teardown — has no live
 *  owner and can never serve a correct request again, so the seat is reclaimed.
 *  The route table is private, so a renamed or missing field simply rethrows. */
function registerJarvisRoute(
  webServer: JarvisWebServer,
  entry: JarvisConfig,
  handler: (req: IncomingMessage, res: ServerResponse) => void | Promise<void>,
): () => void {
  const claim = (): (() => void) => webServer.register({ kind: 'prefix', path: '/jarvis', handler });
  let dispose: () => void;
  try {
    dispose = claim();
  } catch (error) {
    const table = (webServer as { prefixes?: Map<string, unknown> }).prefixes;
    if (!(table instanceof Map) || !table.delete('/jarvis')) throw error;
    debug(entry, 'reclaimed a stale /jarvis route: ' + (error instanceof Error ? error.message : String(error)));
    dispose = claim();
  }
  routeSlot()[ROUTE_SLOT] = dispose;
  return dispose;
}

/** The panel build for this platform: a bare Swift executable on macOS, the
 *  panel agent's windows/dist/jarvis-panel.exe on Windows. */
function panelBinary(root: string): string {
  return onWindows ? join(root, 'windows', 'dist', 'jarvis-panel.exe') : join(root, 'macos', 'jarvis-panel');
}

/** Each apply owns its process and timer. Replacing an owner permanently
 *  disposes it before SIGTERM, so late events cannot affect the new owner. */
function createPanelProcess(entry: JarvisConfig, say: JarvisDeps['say']): PanelProcess {
  const supervisor = new PanelSupervisor();
  let panelChild: ChildProcess | null = null;
  let restartTimer: ReturnType<typeof setTimeout> | null = null;
  let disposed = false;
  let started = false;

  const onExit = (code: number | null, signal: NodeJS.Signals | null): void => {
    const decision = supervisor.onExit(code, signal);
    if (decision.action === 'stay-down') {
      debug(entry, 'panel stopped: ' + decision.reason);
      if (decision.reason === 'crash-limit') {
        void say('The panel kept crashing, so automatic restarts have stopped').catch(e => {
          debug(entry, 'panel crash notification failed: ' + String(e));
        });
      }
      return;
    }
    debug(entry, 'panel exited (code=' + code + ', signal=' + signal + '), restarting in ' + decision.delayMs + 'ms');
    restartTimer = setTimeout(() => {
      restartTimer = null;
      if (!disposed) spawnPanel();
    }, decision.delayMs);
    restartTimer.unref();
  };

  const spawnPanel = (): void => {
    if (disposed) return;
    const bin = panelBinary(pkgRoot);
    if (!existsSync(bin)) { debug(entry, 'panel binary not found (' + bin + ') — skipping spawn (build ' + (onWindows ? 'windows/' : 'macos/') + ' first)'); return; }
    try {
      // windowsHide only hides a console window; a GUI panel still shows itself.
      const child = spawn(bin, [], onWindows ? { stdio: 'ignore', windowsHide: true } : { stdio: 'ignore' });
      panelChild = child;
      child.on('spawn', () => {
        if (!disposed && panelChild === child) supervisor.markStarted();
      });
      const finish = (code: number | null, signal: NodeJS.Signals | null): void => {
        if (disposed || panelChild !== child) return;
        panelChild = null;
        onExit(code, signal);
      };
      child.on('exit', finish);
      child.on('error', error => {
        if (disposed || panelChild !== child) return;
        debug(entry, 'panel spawn failed: ' + error.message);
        finish(-1, null);
      });
      child.unref();
      debug(entry, 'spawned panel (pid=' + child.pid + ', non-detached, bin=' + bin + ')');
    } catch (e) {
      debug(entry, 'panel spawn failed: ' + (e instanceof Error ? e.message : String(e)));
      onExit(-1, null);
    }
  };

  return {
    start() {
      if (disposed || started) return;
      started = true;
      spawnPanel();
    },
    dispose() {
      if (disposed) return;
      disposed = true;
      supervisor.markDisposed();
      if (restartTimer) { clearTimeout(restartTimer); restartTimer = null; }
      const child = panelChild;
      panelChild = null;
      if (child && !child.killed) {
        try { child.kill('SIGTERM'); debug(entry, 'killed panel (pid=' + child.pid + ')'); } catch {}
      }
    },
  };
}

/** Raise the running DSH Desktop window on Windows, where neither `open` nor an
 *  app bundle exists. DSH's Electron processes all share one exe path, so prefer
 *  our own pid (the host may be the windowed main process) and otherwise take
 *  whichever process on that path owns a main window. Best-effort and never
 *  awaited — /open stays fire-and-forget. Focus-stealing rules make a bare
 *  SetForegroundWindow unreliable, so the script joins the foreground thread's
 *  input queue for that one call. */
function raiseDshWindow(entry: JarvisConfig): void {
  // One-line C#: -Command receives the script as a single argument, and a
  // here-string would depend on newline quoting surviving that trip.
  const types = 'using System;using System.Runtime.InteropServices;public class JarvisWin{'
    + '[DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);'
    + '[DllImport("user32.dll")]public static extern bool ShowWindowAsync(IntPtr h,int c);'
    + '[DllImport("user32.dll")]public static extern bool IsIconic(IntPtr h);'
    + '[DllImport("user32.dll")]public static extern IntPtr GetForegroundWindow();'
    + '[DllImport("user32.dll")]public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);'
    + '[DllImport("user32.dll")]public static extern bool AttachThreadInput(uint a,uint b,bool f);'
    + '[DllImport("kernel32.dll")]public static extern uint GetCurrentThreadId();}';
  const script = [
    `$exe='${process.execPath.replace(/'/g, "''")}'`,
    `Add-Type -TypeDefinition '${types}'`,
    `$p=Get-Process -Id ${String(process.pid)} -ErrorAction SilentlyContinue`,
    // A windowless Electron child cannot be raised; the browser process with the
    // same exe path is where the window lives.
    'if(-not $p -or $p.MainWindowHandle -eq 0){$p=Get-Process -ErrorAction SilentlyContinue|Where-Object{try{$_.Path -eq $exe}catch{$false}}|Where-Object{$_.MainWindowHandle -ne 0}|Select-Object -First 1}',
    'if(-not $p -or $p.MainWindowHandle -eq 0){exit 1}',
    '$h=$p.MainWindowHandle',
    // Restore first: SetForegroundWindow leaves a minimized window minimized.
    'if([JarvisWin]::IsIconic($h)){[void][JarvisWin]::ShowWindowAsync($h,9)}',
    '$fgPid=0',
    '$fg=[JarvisWin]::GetWindowThreadProcessId([JarvisWin]::GetForegroundWindow(),[ref]$fgPid)',
    '$mine=[JarvisWin]::GetCurrentThreadId()',
    'if($fg -ne 0 -and $fg -ne $mine){[void][JarvisWin]::AttachThreadInput($mine,$fg,$true)}',
    '[void][JarvisWin]::SetForegroundWindow($h)',
    'if($fg -ne 0 -and $fg -ne $mine){[void][JarvisWin]::AttachThreadInput($mine,$fg,$false)}',
  ].join(';');
  try {
    const child = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script],
      { stdio: 'ignore', windowsHide: true });
    child.on('error', error => debug(entry, 'raise DSH window failed: ' + error.message));
    child.unref();
    debug(entry, 'raising DSH window (pid=' + child.pid + ')');
  } catch (e) {
    debug(entry, 'raise DSH window failed: ' + (e instanceof Error ? e.message : String(e)));
  }
}

export function apply(ctx: Context, rawConfig: unknown): (() => void) | void {
  const entry = { ...Config(rawConfig ?? {}), ...(rawConfig as object) } as JarvisConfig;
  Object.assign(entry, mergeSettings(Config({}) as JarvisConfig, entry));
  debug(entry, 'apply() started');
  const pruneAudio = setTimeout(() => cleanupSpeechCache(resolveDir(entry.audioDir)), 30_000);
  pruneAudio.unref();
  const managed = new ManagedSet(resolveDir(entry.managedFile),
    (e) => debug(entry, 'managed.json write failed: ' + (e instanceof Error ? e.message : String(e))));
  const ledger = new TaskLedger(resolveDir(entry.tasksFile),
    (e) => debug(entry, 'tasks.json write failed: ' + (e instanceof Error ? e.message : String(e))));
  const pruneTasks = setInterval(() => ledger.prune(Date.now()), 60_000);
  pruneTasks.unref();
  const memory = new MemoryStore({
    rootDir: resolveDir(entry.memoryRoot), lockFile: resolveDir(entry.lockFile),
    approvalsDir: resolveDir(entry.approvalsDir),
    onError: e => debug(entry, 'memory lock diagnostic failed: ' + (e instanceof Error ? e.message : String(e))),
  });
  const live = new LiveState(entry.jarvisSessionId);
  const presets = new ApprovalPresets(memory);
  const autoApprovals = new AutoApprovalState();
  const removeApprovalRule = async (key: ApprovalRuleKey): Promise<boolean> => {
    const removed = await presets.remove(key);
    autoApprovals.revoke(key); live.touch();
    return removed;
  };
  const autoApproval = new AutoApproval({
    config: () => entry, classify, llm: () => llm,
    preset: operation => presets.matches({ ...operation, workspace: operation.cwd }),
    history: operation => readApprovalHistory(memory, operation),
  });

  /** Every visible worker (not Jarvis, not archived), flagged by whether it is handed to Jarvis. */
  const sessionRows = async (): Promise<SessionRow[]> => {
    const ids = listWorkerIds(ctx, entry);
    if (Date.now() - titleCache.at > 10_000 || ids.some((id) => !(id in titleCache.map))) {
      titleCache.map = (await readTitleMap(ctx, ids)).map;
      titleCache.at = Date.now();
    }
    return ids.map((id) => {
      const workspace = sessionWorkspace(ctx, id);
      return sessionRow({
        id, title: titleCache.map[id] || '', status: live.status(id, agentRunning(ctx, id)), unread: live.isUnread(id),
        managed: managed.has(id),
        ...(managed.has(id) ? { narration: narrationOf(id) } : {}),
        ...(workspace ? { workspace } : {}),
      }, ledger.peekCurrent(id));
    });
  };

  const narrationOf = (id: string): Narration => effectiveNarration(entry.managedNarration, managed.narration(id));
  const setNarration = (id: string, narration: Narration | undefined): 'ok' | 'not-managed' => {
    if (!managed.has(id)) return 'not-managed';
    if (managed.setNarration(id, narration)) live.touch();
    return 'ok';
  };

  const setManaged = async (id: string, on: boolean): Promise<'ok' | 'not-found'> => {
    if (on && !listWorkerIds(ctx, entry).includes(id)) return 'not-found';
    if (!on) { ledger.dropSession(id); completion.invalidate(id); output.dropSession(id); }
    if (on ? managed.add(id) : managed.remove(id)) live.touch();
    return 'ok';
  };

  const output = new OutputCoordinator({ say: text => deps.say(text), managed: id => managed.has(id) });
  // CompletionJudge's text-only callback keeps its originating session across model awaits.
  const completionContext = new AsyncLocalStorage<{ session: string; title: string; reason?: string }>();
  const deps: JarvisDeps = {
    managed,
    ledger,
    memory,
    presets,
    removeApprovalRule,
    sessionRows,
    setManaged,
    narration: narrationOf,
    setNarration,
    say: (text) => speakAsJarvis(builtInTts, entry, text, live),
    ask: async (question, choices, exec, continuation) => {
      const ask = async (): Promise<string> => {
        const uq = (ctx as any).get?.('userQuestions') as { ask?: (r: unknown) => Promise<any> } | undefined;
        if (!uq?.ask) throw new Error('ask_user: userQuestions service unavailable');
        const agent = exec?.agent ?? jarvisHandle?.agent;
        const answer = await uq.ask({
          questions: [{
            id: 'q1', question, header: 'Jarvis',
            ...(choices.length > 0 ? { options: choices.map((label) => ({ label })) } : {}),
          }],
          ...(agent ? { agent } : {}),
          ...(exec?.signal ? { signal: exec.signal } : {}),
        });
        const first = answer?.answers?.[0];
        const picked = [...(first?.selected ?? []), ...(first?.custom ? [first.custom] : [])];
        return picked.join('; ');
      };
      return await output.ask(() => continuation
        ? completion.askContinuation(continuation.session, continuation.task, ask) : ask(),
      { session: continuation?.session, signal: exec?.signal }) ?? '';
    },
  };

  // ── provide 'jarvis' service so voice-mini (and other plugins) auto-detect
  //    us. voice-mini does ctx.inject(['jarvis']) → flips jarvisLinked (shows
  //    the Jarvis panel section). Contract: who we are + our session id
  //    (voice-mini singles out Jarvis's session for narration), and speech():
  //    voice-mini reports every line it plays so the orb talks in step with it.
  //    claimsTurnEnd() identifies worker completions narrated by Jarvis instead.
  ctx.provide('jarvis' as any, {
    sessionId: entry.jarvisSessionId,
    cwd: resolveDir(entry.jarvisCwd),
    speech: (signal: unknown) => live.speechSignal(signal),
    claimsTurnEnd: (id: string) => claimsTurnEnd({
      managed: managed.has(id),
      task: managed.has(id) ? ledger.current(id) : undefined,
      judgeEnabled: entry.judgeEnabled,
      narration: narrationOf(id),
    }),
  });
  debug(entry, 'provided "jarvis" service (sessionId=' + entry.jarvisSessionId + ')');
  ctx.logger?.warn?.('dsh-harness-jarvis: provided "jarvis" service');
  let llm: unknown;
  const completion = new CompletionJudge({
    ledger,
    managed: id => managed.has(id),
    narration: narrationOf,
    config: () => entry,
    llm: () => llm,
    messages: id => (ctx as any).get?.('agents')?.get?.(id)?.session?.deriveMessages?.() ?? [],
    title: async id => {
      const title = (await sessionRows()).find(row => row.id === id)?.title.trim() || id;
      const context = completionContext.getStore();
      if (context) context.title = title;
      return title;
    },
    say: text => {
      const context = completionContext.getStore();
      return output.announce(context?.session ?? '', text, {
        title: context?.title,
        // The continuation-limit warning must never become a successful completion summary.
        immediate: !context || context.reason !== 'completed' || text.startsWith(`${context.title} isn't done yet: `),
      });
    },
    notify: text => {
      const agent = jarvisHandle?.agent ?? jarvisHandle;
      const notice: UserMessage = {
        id: MessageId(`jarvis-judge-${Date.now().toString(36)}-${randomBytes(4).toString('hex')}`),
        role: 'user', content: [{ type: 'text', text }],
        source: { kind: 'user' },
      };
      if (!deliver(agent, notice)) throw new Error('completion: Jarvis accepts no messages');
    },
    onError: error => debug(entry, 'completion failed: ' + (error instanceof Error ? error.message : String(error))),
  });

  const panel = createPanelProcess(entry, text => deps.say(text));
  panelOwner?.dispose();
  panelOwner = panel;

  // ── deferred services ─────────────────────────────────────────────────
  ctx.inject(['llm' as any], (llmCtx: Context) => {
    debug(entry, 'llm inject fired');
    llm = (llmCtx as any).get('llm');
    ctx.logger?.warn?.('dsh-harness-jarvis: llm attached');
  });
  // Filled once webServer injects; the disposer below releases it.
  let disposeRoute: (() => void) | null = null;
  ctx.inject(['webServer' as any], (serverCtx: Context) => {
    const webServer = (serverCtx as any).get('webServer') as JarvisWebServer | undefined;
    if (!webServer) { debug(entry, 'webServer not found'); return; }
    const token = loadOrCreateToken(entry);
    const origin = `http://${webServer.host ?? '127.0.0.1'}:${String(webServer.port ?? '')}`;
    webOrigin = origin;
    const writeRt = (): void => {
      let header: { name: string; value: string } | undefined;
      try { header = (ctx as any).get?.('desktopBrowserAccess')?.rendererHeader; } catch {}
      writeRuntimeFile(entry, { origin, token, pid: process.pid, writtenAt: Date.now(), ...(header ? { rendererHeader: header } : {}) });
      debug(entry, 'runtime.json: ' + origin + ' token=' + token.slice(0, 8) + '…' + (header ? ' +renderer' : ' no-renderer'));
    };
    writeRt();
    ctx.inject(['desktopBrowserAccess' as any], () => writeRt());
    panel.start();
    // Registration is a duplicate-path ERROR in DSH, not a replacement, and the
    // handler closes over this apply's token, live state and jarvisHandle. A
    // registration left behind therefore both blocks this apply and, while it
    // still serves, answers /jarvis/* from a torn-down closure — the exact
    // failure that made every reload look like it had not re-imported the
    // module. Release the previous owner first, then claim the path.
    releasePreviousRoute(entry);
    disposeRoute = registerJarvisRoute(webServer, entry, async (req: IncomingMessage, res: ServerResponse) => {
      if (!isLoopbackReq(req) || !isAuthorized(req, token)) { sendJson(res, 403, { error: 'forbidden' }); return; }
      const url = (req.url ?? '').replace(/^\/jarvis/, '').split('?')[0] ?? '/';
      try {
          if (url === '/state' && req.method === 'GET') {
            const narration = await voiceMiniQueue(webOrigin);
            const speech = live.speech();
            const speaking = speech !== null;
            const sessions = await sessionRows();
            const handed = sessions.filter((s) => s.managed);
            const pending = live.pending();
            sendJson(res, 200, {
              agentId: entry.jarvisSessionId,
              managed: managed.list(),
              speaking,
              activity: live.activity(speaking, agentRunning(ctx, entry.jarvisSessionId)),
              error: live.error(),
              voice: {
                speaking,
                ...(speech ?? {}),
                muted: voiceMuted, paused: narration.paused, queued: narration.queued,
              },
              counts: {
                running: handed.filter((s) => s.status === 'running').length,
                pending: pending.length,
                unread: handed.filter((s) => s.unread).length,
                failed: handed.filter((s) => s.status === 'failed').length,
              },
              sessions,
              pending,
              autoApprovals: autoApprovals.list(),
            });
            return;
          }
          if (url === '/wait' && req.method === 'GET') {
            const params = new URL(req.url ?? '', 'http://x').searchParams;
            const since = Number(params.get('since'));
            const timeout = Number(params.get('timeout') ?? '1000');
            const version = await live.waitForChange(
              Number.isFinite(since) ? since : -1,
              Number.isFinite(timeout) ? timeout : 1000,
            );
            sendJson(res, 200, { version });
            return;
          }
          if (url === '/pending/answer' && req.method === 'POST') {
            const body = JSON.parse((await readBody(req)) || '{}');
            const result = live.answer(body);
            debug(entry, '/jarvis/pending/answer: ' + String(body.id) + ' → ' + result);
            if (result === 'ok') { res.statusCode = 204; res.end(); return; }
            sendJson(res, result === 'invalid' ? 400 : 404, { error: result });
            return;
          }
          if (url === '/approval-rules' && req.method === 'GET') {
            sendJson(res, 200, { rules: await presets.list() });
            return;
          }
          if (url === '/approval-rules/remove' && req.method === 'POST') {
            const body = JSON.parse((await readBody(req)) || '{}');
            if (!validRuleKey(body)) { sendJson(res, 400, { error: 'invalid rule key' }); return; }
            if (!await removeApprovalRule(body)) { sendJson(res, 404, { error: 'rule not found' }); return; }
            res.statusCode = 204; res.end(); return;
          }
          if (url === '/voice' && req.method === 'POST') {
            const body = JSON.parse((await readBody(req)) || '{}');
            const action = String(body.action);
            if (action === 'mute') { voiceMuted = true; stopSpeech(); }
            else if (action === 'unmute') voiceMuted = false;
            else if (action in VOICE_MINI_CONTROL) {
              if (action !== 'resume') stopSpeech();
              const ok = await voiceMiniControl(webOrigin, action);
              debug(entry, '/jarvis/voice: ' + action + ' → voice-mini ' + (ok ? 'ok' : 'failed'));
            } else { sendJson(res, 400, { error: 'unknown action' }); return; }
            debug(entry, '/jarvis/voice: ' + action);
            res.statusCode = 204; res.end(); return;
          }
          if (url === '/read' && req.method === 'POST') {
            const body = JSON.parse((await readBody(req)) || '{}');
            live.markRead(typeof body.session === 'string' && body.session ? body.session : undefined);
            res.statusCode = 204; res.end(); return;
          }
          if (url === '/open' && req.method === 'POST') {
            if (onWindows) {
              // Windows has no app bundle to hand to `open`; activate the DSH window itself.
              raiseDshWindow(entry);
            } else {
              // The host runs inside DSH, so its own .app bundle is the one to raise (covers Beta builds).
              const app = /^(.*?\.app)\//.exec(process.execPath)?.[1];
              const args = app ? [app] : ['-b', 'ai.deepseek.dsh.desktop'];
              try { spawn('open', args, { stdio: 'ignore' }).on('error', () => {}); } catch {}
            }
            res.statusCode = 204; res.end(); return;
          }
          if (url === '/providers' && req.method === 'GET') {
            const a = (llm as any)?.adapters; sendJson(res, 200, { providers: a instanceof Map ? [...a.keys()] : [] }); return;
          }
          if (url === '/models' && req.method === 'GET') {
            const q = new URL(req.url ?? '/', 'http://127.0.0.1').searchParams;
            let p = q.get('provider') ?? '';
            if (!p) { const a = (llm as any)?.adapters; if (a instanceof Map && a.size > 0) p = [...a.keys()][0] ?? ''; }
            if (!p || !(llm as any)?.listModels) { sendJson(res, 200, { provider: null, models: [] }); return; }
            const ms = await (llm as any).listModels(p);
            sendJson(res, 200, { provider: p, models: ms.map((m: any) => ({ id: m.id, name: m.name ?? m.id })) }); return;
          }
          if (url === '/sessions' && req.method === 'GET') { sendJson(res, 200, { managed: managed.list() }); return; }
          if (url === '/managed' && req.method === 'POST') {
            const body = JSON.parse((await readBody(req)) || '{}');
            const session = typeof body.session === 'string' ? body.session.trim() : '';
            if (!session || typeof body.managed !== 'boolean') { sendJson(res, 400, { error: 'need {session, managed}' }); return; }
            if (await setManaged(session, body.managed) === 'not-found') { sendJson(res, 404, { error: 'no such session' }); return; }
            sendJson(res, 200, { managed: managed.list() });
            return;
          }
          if (url === '/narration' && req.method === 'POST') {
            const body = JSON.parse((await readBody(req)) || '{}');
            const session = typeof body.session === 'string' ? body.session.trim() : '';
            if (!session || !(body.narration === null || isNarration(body.narration))) {
              sendJson(res, 400, { error: 'need {session, narration: self|relay|null}' }); return;
            }
            if (setNarration(session, body.narration ?? undefined) === 'not-managed') { sendJson(res, 404, { error: 'session not managed' }); return; }
            sendJson(res, 200, { session, narration: narrationOf(session) });
            return;
          }
          if (url === '/agents' && req.method === 'GET') {
            // List all available agents/sessions (so the user/Jarvis knows which
            // sessions can be targeted by inject_to_session).
            try {
              const agents = (ctx as any).get?.('agents');
              const ids = listWorkerIds(ctx, entry);
              const { map: titleMap, diag: snapsDiag } = await readTitleMap(ctx, ids);
              if (snapsDiag.startsWith('err')) debug(entry, '/agents: ' + snapsDiag);
              const result = ids.map((id: string) => {
                try {
                  const a = agents?.get?.(id);
                  const agent = a?.agent ?? a;
                  const title = titleMap[id] || '';
                  return { id, title: title || id.slice(-8), hasInject: typeof agent?.inject === 'function', hasFollowup: typeof agent?.followup === 'function' };
                } catch { return { id, title: titleMap[id] || id.slice(-8), hasInject: false, hasFollowup: false }; }
              });
              sendJson(res, 200, { agents: result, count: result.length, snapsDiag });
            } catch (e) { sendJson(res, 500, { error: e instanceof Error ? e.message : String(e) }); }
            return;
          }
          if (url === '/messages' && req.method === 'GET') {
            // Jarvis's own conversation by default; `?session=<id>` reads a worker's,
            // so switching the send target also switches what the panel shows.
            try {
              const wanted = new URL(req.url ?? '', 'http://x').searchParams.get('session') ?? '';
              if (wanted && !listWorkerIds(ctx, entry).includes(wanted)) { sendJson(res, 404, { error: 'no such session' }); return; }
              const s = wanted
                ? (ctx as any).get?.('agents')?.get?.(wanted)?.session
                : jarvisHandle?.agent?.session;
              if (!s) { sendJson(res, 200, { messages: [], error: 'no session' }); return; }
              const conv = toConversation(readSessionMessages(s, entry));
              sendJson(res, 200, { messages: conv, count: conv.length });
            } catch (e) { sendJson(res, 500, { error: e instanceof Error ? e.message : String(e) }); }
            return;
          }
          if (url === '/debug' && req.method === 'GET') {
            const h = jarvisHandle;
            const a = h?.agent;
            const s = a?.session;
            const tryLen = (fn: any) => { try { return (fn?.() ?? []).length } catch { return 'err' } };
            const tryLast = (fn: any) => { try { const m = fn?.() ?? []; const last = m.length > 0 ? m[m.length - 1] : null; return last ? (last.role + ': ' + (last.content?.map((b: any) => b.type === 'text' ? b.text : '').join('').slice(0, 80)) ) : '(empty)'; } catch { return 'err' } };
            // sessionQuery diagnostic: what does readTitleSnapshots return?
            let titleDiag = '(not called)';
            try {
              const sq = (ctx as any).get?.('sessionQuery');
              if (sq?.readTitleSnapshots) {
                const agentsSvc = (ctx as any).get?.('agents');
                let rawIds: any[] = [];
                if (typeof agentsSvc?.keys === 'function') rawIds = [...agentsSvc.keys()];
                else if (agentsSvc && typeof agentsSvc[Symbol.iterator] === 'function') rawIds = [...agentsSvc];
                titleDiag = 'rawIds=' + rawIds.length + ' sample=' + JSON.stringify(rawIds.slice(0, 3))?.slice(0, 200) + ' | ';
                const ids = rawIds.map((x: any) => String(typeof x === 'string' ? x : (x?.id ?? x?.sessionId ?? x))).filter((x: string) => x !== entry.jarvisSessionId);
                if (ids.length > 0) {
                  const snaps = await sq.readTitleSnapshots(ids.slice(0, 3));
                  titleDiag += JSON.stringify(snaps)?.slice(0, 400) ?? '(null)';
                } else { titleDiag += 'no ids after filter'; }
              } else { titleDiag = 'sessionQuery/readTitleSnapshots unavailable; proto keys=' + (sq ? Object.getOwnPropertyNames(Object.getPrototypeOf(sq)).join(',') : '(no sq)'); }
            } catch (e) { titleDiag = 'err: ' + (e instanceof Error ? e.message : String(e)); }
            sendJson(res, 200, {
              handleKeys: h ? Object.keys(h).join(',') : '(null)',
              agentKeys: a ? Object.keys(a).slice(0, 25).join(',') : '(null)',
              agentId: a?.id ?? '(none)',
              sessionNull: !s,
              sessionKeys: s ? Object.keys(s).slice(0, 20).join(',') : '(null)',
              sessionSeq: s?.seq ?? '(none)',
              msgsLen: tryLen(s?.deriveMessages),
              msgsLast: tryLast(s?.deriveMessages),
              eventsLen: tryLen(s?.ownEvents),
              hasFollowup: typeof a?.followup === 'function',
              titleDiag,
            });
            return;
          }
          if (url === '/config' && req.method === 'GET') { sendJson(res, 200, { provider: entry.provider, model: entry.model, edgeVoice: entry.edgeVoice }); return; }
          if (url === '/input' && req.method === 'POST') {
            // Inject the user's text into the Jarvis agent → triggers a turn.
            // If a target session is provided, format the message so Jarvis knows
            // to inject into that session (the user picks the session in the panel
            // picker — no need to type UUIDs).
            const body = JSON.parse((await readBody(req)) || '{}');
            const text = typeof body.text === 'string' ? body.text.trim() : '';
            const session = typeof body.session === 'string' ? body.session.trim() : '';
            if (!text) { sendJson(res, 400, { error: 'empty text' }); return; }
            if (!jarvisHandle) { sendJson(res, 503, { error: 'jarvis agent not ready' }); return; }
            const jarvisText = session
              ? routedInput(session, text)
              : text;
            const msg: UserMessage = {
              id: MessageId(`jarvis-input-${Date.now().toString(36)}`),
              role: 'user',
              content: [{ type: 'text', text: jarvisText }],
              source: { kind: 'user' },
            };
            const agent = jarvisHandle?.agent ?? jarvisHandle;
            live.userSent();
            try {
              if (typeof agent?.followup === 'function') {
                agent.followup(msg);
                if (session) { completion.invalidate(session); ledger.expect(session, body.text); }
                debug(entry, '/jarvis/input: followup() — "' + text.slice(0, 60) + '"' + (session ? ' → ' + session : ''));
                sendJson(res, 200, { ok: true });
              } else if (typeof agent?.inject === 'function') {
                agent.inject(msg);
                if (session) { completion.invalidate(session); ledger.expect(session, body.text); }
                debug(entry, '/jarvis/input: inject() — "' + text.slice(0, 60) + '"');
                sendJson(res, 200, { ok: true });
              } else {
                sendJson(res, 500, { error: 'no followup/inject on jarvis handle' });
              }
            } catch (e) {
              // Without this per-branch catch the rejection would leave the
              // response head-only; the panel reads that as a bare status code
              // with no body, which is undebuggable from the outside.
              const reason = e instanceof Error ? e.message : String(e);
              debug(entry, '/jarvis/input: delivery failed — ' + reason);
              sendJson(res, 500, { error: reason });
            }
            return;
          }
          sendJson(res, 404, { error: 'not found', url });
        } catch (e) { sendJson(res, 500, { error: e instanceof Error ? e.message : String(e) }); }
      });
    debug(entry, 'webServer routes mounted at /jarvis');
  });
  const settingsBase = mergeSettings(entry, {});
  let settingsSource: () => unknown = () => settingsBase;
  let settingsDisposed = false;
  let agentModelSelected = false;
  const refreshSettings = () => {
    if (settingsDisposed) return;
    try {
      const next = mergeSettings(settingsBase, settingsSource());
      // A live agent and its judge fallback keep the model selected at creation.
      if (agentModelSelected) { next.provider = entry.provider; next.model = entry.model; }
      Object.assign(entry, next);
    } catch (error) { debug(entry, 'settings read failed: ' + String(error)); }
  };
  ctx.inject(['settings' as any], (settingsCtx: Context) => {
    if (settingsDisposed) return;
    try {
      const settings = (settingsCtx as any).get?.('settings');
      if (typeof settings?.installSection !== 'function') return;
      // installSection owns scope.watch, persistence and fallback when the service detaches.
      settings.installSection(ctx, 'jarvis', SettingsSchema, settingsBase, {
        setSource: (source: () => unknown) => { if (!settingsDisposed) settingsSource = source; },
        onChange: refreshSettings,
      });
    } catch (error) {
      settingsSource = () => settingsBase;
      refreshSettings();
      debug(entry, 'settings install failed: ' + String(error));
    }
  });
  let titleService: any;
  let jarvisReady = false;
  let titleAttempted = false;
  const setJarvisTitle = () => {
    if (!jarvisReady || !titleService || titleAttempted) return;
    titleAttempted = true;
    void trySetTitle(ctx, titleService, entry, 'Jarvis');
  };
  ctx.inject(['sessionTitle' as any], (tsCtx: Context) => {
    titleService = (tsCtx as any).get('sessionTitle');
    setJarvisTitle();
  });

  // ── Hook 2: create Jarvis agent with setup callback ──────────────────
  // The plugin creates the agent itself (not relying on cordis.yml config
  // or GUI naming). The setup callback registers persona + tools (hook 2)
  // through agentCtx before the agent is published.
  //
  // Creation goes through the `agents` registry, NOT through
  // agentLoop.createAgent(ctx, …): since DSH 0.2.x every agent is owned by the
  // context whose effect mints it (agent-loop `initializeAgent`), and
  // service-owned creation is traced through the agents service's own context.
  // Passing this loader entry's context made the session die with the plugin —
  // the next apply() then held a Session that had been detached from its
  // SessionStore, and every append failed with
  // `session "jarvis" cannot append outside a SessionStore`.
  ctx.inject(['agents' as any], (agentsCtx: Context) => {
    debug(entry, 'agents inject callback FIRED');
    const agents = (agentsCtx as any).get('agents') as AgentsService | undefined;
    // Resolve the auto model: empty provider → first llm adapter, empty model
    // → that provider's first entry. The result is written back into entry so
    // the judge fallback, /config route, and settings pinning all see the
    // concrete values the live agent actually uses. The llm service is read
    // from this context or the earlier llm inject, whichever is available.
    try {
      const svc = ((agentsCtx as any).get('llm') ?? llm) as any;
      if ((!entry.provider.trim() || !entry.model.trim()) && svc) {
        const adapters = svc.adapters;
        const keys = adapters instanceof Map ? [...adapters.keys()].map(String) : [];
        if (!entry.provider.trim() && keys.length > 0) entry.provider = keys[0];
        if (entry.provider.trim() && !entry.model.trim() && typeof svc.listModels === 'function') {
          const models = svc.listModels(entry.provider) ?? [];
          const first = models?.[0];
          entry.model = String(typeof first === 'string' ? first : (first?.id ?? ''));
        }
        debug(entry, 'agent model auto-resolved: provider=' + entry.provider + ' model=' + entry.model);
      }
    } catch (e) { debug(entry, 'agent model auto-resolve failed: ' + (e instanceof Error ? e.message : String(e))); }
    if (!agents || typeof agents.create !== 'function') {
      debug(entry, 'agents service NOT found (get returned undefined)');
      ctx.logger?.warn?.('dsh-harness-jarvis: agents service not found');
      return;
    }
    agentModelSelected = true;
    // Setup recomposes the Jarvis world on every mount. The Agent parameter is
    // the 0.2.x second argument; decoration never needs it, but taking only the
    // first would silently mis-bind if a future release reorders them.
    const setup = (agentCtx: Context, agent: unknown): void => {
      debug(entry, 'setup callback FIRED — decorating Jarvis agent');
      void agent;
      decorateJarvisAgent(agentCtx, entry, deps);
    };
    debug(entry, 'agents service found, resolving Jarvis agent');
    void settleJarvisAgent(agents, agentsCtx, entry, setup).then((settled) => {
      jarvisHandle = settled.handle;
      jarvisReady = true;
      setJarvisTitle();
      debug(entry, 'Jarvis agent ready (' + (settled.existed ? 'resumed' : 'created') + ')');
      ctx.logger?.warn?.('dsh-harness-jarvis: Jarvis agent ready (' + (settled.existed ? 'resumed' : 'created') + ')');
      // First install: the session did not exist, so one LLM turn brings it to
      // life. Restart: existing history, so only the spoken greeting.
      if (settled.existed) void speakGreeting(builtInTts, entry, live);
      else kickstartJarvis(settled.handle, entry);
    }).catch((err: unknown) => {
      debug(entry, 'Jarvis agent setup failed: ' + (err instanceof Error ? err.message : String(err)));
    });
  });

  // ── Hook 1: global listeners (worker events, filtered to managed) ────
  ctx.on('session/event' as any, (session: { id?: string } | undefined, event: { type?: string; data?: any }) => {
    const sid = typeof session?.id === 'string' ? session.id : undefined;
    if (sid && (sid === entry.jarvisSessionId || managed.has(sid))) {
      void captureTemp(memory, session as CaptureSession, event, error =>
        debug(entry, 'temp capture failed: ' + (error instanceof Error ? error.message : String(error))));
    }
    if (sid) {
      if (event?.type === 'turn/start') live.turnStarted(sid);
      else if (event?.type === 'turn/end') live.turnEnded(sid, event.data?.reason);
      else if (event?.type === 'user/message' && event.data?.source === 'user') live.markRead(sid);
    }
    if (!sid || !managed.has(sid)) return;
    switch (event?.type) {
      case 'turn/start':
        completion.turnStarted(sid);
        break;
      case 'turn/end':
        void completionContext.run({ session: sid, title: sid, reason: event.data?.reason?.kind },
          () => completion.turnEnded(sid, event.data?.reason?.kind)).catch(error =>
          debug(entry, 'turn/end judge failed: ' + (error instanceof Error ? error.message : String(error))));
        break;
      case 'approval/asked':
        // Approval relay is handled by approval/request below.
        break;
      case 'ask_user_question':
        // Question relay is handled by user-questions/request below; auto-answering is backlog J7.
        break;
    }
  });

  // ── approvals / questions relayed to the panel (§11) ─────────────────
  // Prepended so the panel races the DSH window; whichever answers first wins.
  ctx.on('approval/request' as any, (req: any, next: () => Promise<any>) => {
    let snapshot: Omit<ApprovalRecord, 'ts' | 'decision'> | undefined;
    let metadataError: unknown;
    let command: string | undefined;
    let presetOperation: PresetOperation | undefined;
    let operation: ApprovalOperation | undefined;
    let taskContext = '';
    let userMessageSnapshot = '';
    let taskId: string | undefined;
    try {
      // Freeze request metadata before DSH resumes and starts mutating the session.
      const sid = String(req.agent.id);
      const messages: any[] = req.agent?.session?.deriveMessages?.() ?? [];
      const rawOperation = approvalOperation(messages, req.callId);
      command = rawOperation.command;
      const lastUser = [...messages].reverse().find(message => message?.role === 'user');
      userMessageSnapshot = JSON.stringify(lastUser ?? null);
      const task = ledger.peekCurrent(sid);
      taskId = task?.id;
      const context = task?.request ?? (lastUser?.content ?? [])
        .filter((block: any) => block?.type === 'text' && typeof block.text === 'string')
        .map((block: any) => block.text).join('\n');
      const cwd = (ctx as any).get?.('sessions')?.get?.(sid)?.header?.cwd;
      taskContext = context;
      operation = { tool: req.toolName, command, args: rawOperation.args, cwd: typeof cwd === 'string' ? cwd : '' };
      presetOperation = { ...operation, workspace: operation.cwd };
      snapshot = {
        fingerprint: fingerprint(req.toolName, command),
        session: { id: sid, cwd: typeof cwd === 'string' ? cwd : '', managed: managed.has(sid) },
        operation: { tool: req.toolName, ...rawOperation },
        context: Array.from(context).slice(0, 300).join(''),
        tier: { value: '', notes: 'Phase1: untiered' },
      };
    } catch (error) { metadataError = error; }
    const current = (): boolean => {
      if (settingsDisposed || req.signal?.aborted || !operation || String(req.agent.id) !== snapshot?.session.id) return false;
      const messages: any[] = req.agent?.session?.deriveMessages?.() ?? [];
      const latest = approvalOperation(messages, req.callId);
      const latestTask = ledger.peekCurrent(String(req.agent.id));
      const lastUser = [...messages].reverse().find(message => message?.role === 'user');
      const latestContext = latestTask?.request ?? (lastUser?.content ?? [])
        .filter((block: any) => block?.type === 'text' && typeof block.text === 'string')
        .map((block: any) => block.text).join('\n');
      const cwd = (ctx as any).get?.('sessions')?.get?.(String(req.agent.id))?.header?.cwd;
      return req.toolName === operation.tool && latest.command === operation.command && latest.args === operation.args &&
        cwd === operation.cwd && latestTask?.id === taskId && latestContext === taskContext &&
        JSON.stringify(lastUser ?? null) === userMessageSnapshot;
    };
    const relay = (note = '') => live.holdApproval(req, next, command?.slice(0, 300), {
      canAlwaysAllow: () => !!presetOperation && !!operation && classify(operation) !== 'high' && canPresetApproval(presetOperation),
      onAlways: () => {
        const operation = presetOperation;
        if (!operation) return;
        // Return the current approval before acquiring any storage locks or doing I/O.
        setImmediate(() => {
          void presets.add(operation).catch(error => {
            try { debug(entry, 'approval preset failed: ' + (error instanceof Error ? error.message : String(error))); } catch {}
          });
        });
      },
    }, note);
    const mode = entry.autoApprove;
    const decision = snapshot && operation ? autoApproval.decide({ operation, context: taskContext, signal: req.signal, current })
      : Promise.resolve<AutoApprovalResult>({allow:false,tier:'high',reason:''});
    const settled = mode === 'off'
      ? relay().then(outcome => ({ result: {allow:false,tier:'grey',reason:''} as AutoApprovalResult, automatic:false, outcome }))
      : decision.then(async result => {
      let valid = false;
      try { valid = result.allow && entry.autoApprove === mode && current() && classify(operation!) === result.tier; } catch {}
      return { result, automatic: valid, outcome: valid ? 'allowed-once' : await relay(result.reason) };
    });
    return settled.then(({ outcome, result, automatic }) => {
      if (outcome !== 'allowed-once' && outcome !== 'rejected') return outcome;
      const ts = new Date().toISOString();
      // A microtask would run before the DSH waterfall returns to its caller.
      setImmediate(() => {
        const onError = (error: unknown) => debug(entry, 'approval record failed: ' +
          (error instanceof Error ? error.message : String(error)));
        try {
          if (!snapshot) { onError(metadataError); return; }
          void writeApproval(memory, {
            ...snapshot, ts, ...(mode === 'off' ? {} : {tier:{value:result.tier,notes:automatic?'Auto-approved':'Sent to the user for approval'}}),
            decision: { allow: outcome === 'allowed-once', source: automatic ? 'jarvis' : 'user', reason: automatic ? result.reason : '' },
          }, onError);
          if (automatic && result.tier !== 'high') {
            const title = titleCache.map[snapshot.session.id] || snapshot.session.id;
            autoApprovals.add({ session:snapshot.session.id,title,tool:snapshot.operation.tool,command:snapshot.operation.command,
              tier:result.tier,...(result.rule?{rule:result.rule}:{}) });
            live.touch();
            if (result.tier === 'grey' && !settingsDisposed) {
              void deps.say(`Approved for ${title}: ${Array.from(snapshot.operation.command).slice(0,80).join('')}`).catch(onError);
            }
          }
        } catch (error) { try { onError(error); } catch {} }
      });
      return outcome;
    });
  }, { prepend: true } as any);
  const askInterceptor = new AskInterceptor({
    config: () => entry,
    managed: id => id !== entry.jarvisSessionId && managed.has(id),
    task: id => ledger.peekCurrent(id),
    llm: () => llm,
    recall: (session, query) => recall(memory, { session, query, limit: 5 }),
    accepted: answer => {
      // Return to DSH before any reporting or disk work; neither can reopen the question.
      setImmediate(() => {
        if (settingsDisposed) return;
        const onError = (error: unknown) => debug(entry, 'auto answer report failed: ' + String(error));
        void memory.appendTemp(answer.session, {
          at: Date.now(), type: 'question/auto-answer', questionId: answer.question.id,
          question: Array.from(answer.question.question).slice(0, 500).join(''),
          choice: Array.from(answer.choice).slice(0, 500).join(''), confidence: answer.confidence,
          ...(answer.task ? { taskId: answer.task.id } : {}),
        }).catch(onError);
        void (async () => {
          const title = (await sessionRows()).find(row => row.id === answer.session)?.title || answer.session;
          await deps.say(`${title} asked "${answer.question.question}", so I chose ${answer.choice} for you`);
        })().catch(onError);
      });
    },
  });
  ctx.on('user-questions/request' as any, (req: any, next: () => Promise<any>) =>
    askInterceptor.answer(req, () => settingsDisposed || req.signal?.aborted ? next() : live.holdAsk(req, next)),
    { prepend: true } as any);

  // ── built-in TTS + voice:tts detect (§5) ─────────────────────────────
  const builtInTts = makeBuiltInTTS(entry);
  ctx.inject(['voice:tts' as any], () => {
    ctx.logger?.warn?.('dsh-harness-jarvis: external voice:tts detected');
  });

  void llm; void builtInTts;

  // Cordis convention: apply() returns a disposer that runs when this plugin
  // context is torn down (DSH quits / plugin unloads) — kill the panel
  // so the orb's lifecycle stays tied to DSH ("with DSH as a dependency").
  return () => {
    settingsDisposed = true;
    clearInterval(pruneTasks);
    clearTimeout(pruneAudio);
    completion.dispose();
    askInterceptor.dispose();
    autoApproval.dispose();
    output.dispose();
    // Unregister this apply's route so the next reload can claim the path, and
    // clear the process slot only while it still names this registration — a
    // later apply may already own it.
    if (disposeRoute) {
      const slot = routeSlot();
      if (slot[ROUTE_SLOT] === disposeRoute) delete slot[ROUTE_SLOT];
      disposeRoute();
      disposeRoute = null;
    }
    panel.dispose();
    if (panelOwner === panel) panelOwner = null;
  };
}

/** Built-in restart welcome greetings. This deployment ships English wording
 *  for every locale; a user who wants another language adds entries through the
 *  `greetings` setting, which is appended to this list. */
function builtinGreetings(_locale: string): string[] {
  return ['Welcome back!', 'Welcome!'];
}

/** Pick a random restart welcome greeting from (built-ins ∪ user-configured
 *  greetings). Restart path only — no LLM. */
function pickGreeting(entry: JarvisConfig): string {
  const list = [...builtinGreetings(entry.locale), ...entry.greetings];
  // crypto.randomInt (OS CSPRNG) instead of Math.random — the host pins V8's
  // --random_seed so Math.random() is deterministic per-process (always the
  // same first call), which made every restart pick the same greeting.
  const fallback = builtinGreetings(entry.locale)[0]!;
  if (list.length <= 1) return fallback;
  return list[randomInt(list.length)] ?? fallback;
}

/**
 * First-install kickstart: the jarvis session did NOT exist, so we make the
 * first LLM call to bring the session to life (createAgent created the object;
 * the turn persists/finalises it). The LLM turn's reply is spoken by voice-mini
 * (when linked + narrating jarvis turns). NO fixed welcome greeting here — the
 * welcome greeting is for the restart path (session already exists).
 * (§: session absent → a model call makes the session get created) */
function kickstartJarvis(handle: any, entry: JarvisConfig): void {
  try {
    // This deployment ships English wording for every locale.
    const init = 'Hello';
    const msg: UserMessage = {
      id: MessageId(`jarvis-init-${Date.now().toString(36)}`),
      role: 'user',
      content: [{ type: 'text', text: init }],
      source: { kind: 'user' },
    };
    const agent = handle?.agent ?? handle;
    if (typeof agent?.followup === 'function') {
      agent.followup(msg);
      debug(entry, 'kickstart: followup() (first-install LLM turn, create session) — "' + init + '"');
    } else if (typeof agent?.inject === 'function') {
      agent.inject(msg);
      debug(entry, 'kickstart: inject() (first-install, may not trigger turn) — "' + init + '"');
    } else {
      debug(entry, 'kickstart: no followup/inject on handle — session created but no init turn');
    }
  } catch (e) {
    debug(entry, 'kickstart failed: ' + (e instanceof Error ? e.message : String(e)));
  }
}

/**
 * Restart welcome greeting (session already existed → NO LLM). Picks a random
 * greeting from the list and speaks it via voice-mini /test (chime+TTS, the
 * "full stack": jarvis+voice-mini); falls back to built-in edge-tts if voice-mini is
 * absent. NO text written into the session (§: session exists → greeting, random pick, no LLM).
 */
async function speakGreeting(
  tts: { synthesize: (text: string, outFile: string) => Promise<unknown>; play: (file: string) => unknown },
  entry: JarvisConfig,
  live: LiveState,
): Promise<void> {
  const text = pickGreeting(entry);
  debug(entry, 'speakGreeting: picked "' + text + '" → ' + await speakAsJarvis(tts, entry, text, live));
}

/** Jarvis's own voice (greetings, say_to_user): voice-mini /test with the
 *  Jarvis chime when present, otherwise the built-in backend (cached per text). */
async function speakAsJarvis(
  tts: { synthesize: (text: string, outFile: string) => Promise<unknown>; play: (file: string) => unknown },
  entry: JarvisConfig,
  text: string,
  live: LiveState,
): Promise<SpeakResult> {
  if (voiceMuted) return 'muted';
  if (webOrigin && await speakViaVoiceMini(webOrigin, text, entry)) return 'voice-mini';
  try {
    // The voice and the backend both belong in the cache key: the same words in
    // another voice — or from a backend that writes another format — are
    // different audio, and reusing the wrong file would be audible.
    const backend = resolveTtsBackend(entry);
    const key = backend === 'kokoro' ? entry.kokoroVoice : entry.edgeVoice;
    const extension = backend === 'kokoro' ? 'wav' : 'mp3';
    const hash = createHash('sha1').update(backend + '\0' + key + '\0' + text).digest('hex').slice(0, 16);
    const outFile = join(resolveDir(entry.audioDir), `say-${hash}.${extension}`);
    if (!existsSync(outFile)) await tts.synthesize(text, outFile);
    const id = `built-in-${randomUUID()}`;
    live.speechSignal({ phase: 'start', id, source: 'jarvis', text });
    // Playback remains asynchronous; always retire this exact utterance on failure or completion.
    void Promise.resolve().then(() => tts.play(outFile))
      .catch((error) => debug(entry, 'built-in playback failed: ' + String(error)))
      .finally(() => live.speechSignal({ phase: 'end', id, source: 'jarvis' }));
    return 'built-in';
  } catch (e) {
    debug(entry, 'speakAsJarvis built-in failed: ' + (e instanceof Error ? e.message : String(e)));
    return 'failed';
  }
}

/** Speak via voice-mini: (1) fast GET /voice-mini/state to confirm voice-mini
 *  is loaded on the same-host webServer; (2) if present, fire-and-forget POST
 *  /voice-mini/test {text, jarvis:true} — we DON'T await voice-mini's synth+play
 *  (it returns 200 only after playback ~6s, and hangs on a busy voice-mini,
 *  which used to time out → double-sound with the built-in fallback). voice-mini
 *  keeps processing after we move on, so it plays regardless. Returns true iff
 *  voice-mini is present (caller then skips built-in). /state is loopback-open
 *  in voice-mini; we also pass its bearer + rendererHeader from its runtime file. */
async function speakViaVoiceMini(origin: string, text: string, entry: JarvisConfig): Promise<boolean> {
  const vm = readVoiceMiniRuntime();
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (vm?.token) headers['Authorization'] = `Bearer ${vm.token}`;
  if (vm?.rendererHeader && typeof vm.rendererHeader.name === 'string') {
    headers[vm.rendererHeader.name] = String(vm.rendererHeader.value);
  }
  // 1) presence check (fast, ~ms): GET /state
  try {
    const sc = new AbortController();
    const st = setTimeout(() => sc.abort(), 2500);
    const sr = await fetch(`${origin}/voice-mini/state`, { headers, signal: sc.signal });
    clearTimeout(st);
    if (!sr.ok) { debug(entry, 'speakViaVoiceMini: /state HTTP ' + sr.status + ' → voice-mini absent, built-in fallback'); return false; }
  } catch (e) {
    debug(entry, 'speakViaVoiceMini: /state failed (' + (e instanceof Error ? e.message : String(e)) + ') → absent, built-in fallback');
    return false;
  }
  // 2) present → fire-and-forget POST /test (don't await synth+chime+play)
  fetch(`${origin}/voice-mini/test`, {
    method: 'POST', headers, body: JSON.stringify({ text, jarvis: true }),
  }).catch(() => { /* swallow: presence already confirmed */ });
  debug(entry, 'speakViaVoiceMini: /test sent fire-and-forget — "' + text + '" (voice-mini owns synth+chime+play)');
  return true;
}

/** Best-effort read of voice-mini's runtime file (~/.dsh/voice-mini/runtime.json)
 *  for its bearer token + rendererHeader. Returns null if absent/unreadable
 *  (voice-mini not installed or not yet started). */
function readVoiceMiniRuntime(): { token?: string; rendererHeader?: { name: string; value: string } } | null {
  try {
    const f = join(homedir(), '.dsh', 'voice-mini', 'runtime.json');
    if (!existsSync(f)) return null;
    return JSON.parse(readFileSync(f, 'utf8'));
  } catch { return null; }
}

/** Who is speaking comes from voice-mini's jarvis.speech() signals; its queue
 *  (paused / waiting lines) is read here. Cached briefly: /jarvis/state is
 *  polled every second and this is a same-host round trip. */
type VoiceQueue = { paused: boolean; queued: number };
const SILENT: VoiceQueue = { paused: false, queued: 0 };
const voiceMiniCache = { at: 0, value: SILENT };
async function voiceMiniQueue(origin: string | null): Promise<VoiceQueue> {
  if (!origin || Date.now() - voiceMiniCache.at < 400) return voiceMiniCache.value;
  voiceMiniCache.at = Date.now();
  const vm = readVoiceMiniRuntime();
  if (!vm?.token) { voiceMiniCache.value = SILENT; return SILENT; }
  try {
    const r = await fetch(origin + '/voice-mini/pet/state', {
      headers: voiceMiniHeaders(vm),
      signal: AbortSignal.timeout(300),
    });
    const body = r.ok ? await r.json() as { paused?: unknown; queued?: unknown } : {};
    voiceMiniCache.value = {
      paused: body.paused === true,
      queued: typeof body.queued === 'number' ? body.queued : 0,
    };
  } catch { /* keep the last value; voice-mini may be busy or absent */ }
  return voiceMiniCache.value;
}

/** The DSH webServer requires the renderer header on every route, besides voice-mini's bearer. */
function voiceMiniHeaders(vm: ReturnType<typeof readVoiceMiniRuntime>): Record<string, string> {
  const headers: Record<string, string> = {};
  if (vm?.token) headers['Authorization'] = `Bearer ${vm.token}`;
  if (vm?.rendererHeader && typeof vm.rendererHeader.name === 'string') {
    headers[vm.rendererHeader.name] = String(vm.rendererHeader.value);
  }
  return headers;
}

/** Playback control is voice-mini's; Jarvis only forwards the signal. */
const VOICE_MINI_CONTROL: Record<string, string> = {
  pause: '/voice-mini/pause',
  resume: '/voice-mini/resume',
  skip: '/voice-mini/skip',
  clear: '/voice-mini/queue/clear',
};
async function voiceMiniControl(origin: string | null, action: string): Promise<boolean> {
  const path = VOICE_MINI_CONTROL[action];
  if (!origin || !path) return false;
  const headers = { 'Content-Type': 'application/json', ...voiceMiniHeaders(readVoiceMiniRuntime()) };
  voiceMiniCache.at = 0;
  try {
    const r = await fetch(origin + path, { method: 'POST', headers, body: '{}', signal: AbortSignal.timeout(1500) });
    return r.ok;
  } catch { return false; }
}

/** A live session's messages; falls back to deriving them event by event. */
function readSessionMessages(s: any, entry: JarvisConfig): unknown[] {
  try { return s.deriveMessages() ?? []; } catch (e1) {
    debug(entry, '/messages: deriveMessages err: ' + (e1 instanceof Error ? e1.message : String(e1)));
    try {
      const events = (s.snapshotEvents?.() ?? s.ownEvents?.() ?? []) as any[];
      return events.map((ev: any) => { try { return s.deriveEventMessage?.(ev); } catch { return null; } }).filter(Boolean);
    } catch (e2) {
      debug(entry, '/messages: snapshot err: ' + (e2 instanceof Error ? e2.message : String(e2)));
      return [];
    }
  }
}

/** `running` per the agents service, or undefined when it can't tell. */
function agentRunning(ctx: Context, id: string): boolean | undefined {
  try {
    const a = (ctx as any).get?.('agents')?.get?.(id);
    const status = a?.status ?? a?.agent?.status;
    return typeof status === 'string' ? status === 'running' : undefined;
  } catch { return undefined; }
}

/** Rename pins the title, so each successful startup only needs one attempt. */
async function trySetTitle(ctx: Context, titleService: any, entry: JarvisConfig, title: string): Promise<void> {
  try {
    const session = (ctx as any).get('sessions')?.get(entry.jarvisSessionId);
    if (session && needsRename(titleService.get(session), title)) await titleService.rename(session, title);
  } catch (e) {
    debug(entry, 'trySetTitle failed: ' + (e instanceof Error ? e.message : String(e)));
  }
}

/** Resolve the Jarvis agent for one apply, reusing what is already live.
 *
 *  A live agent must be REUSED rather than replaced: agents created through the
 *  `agents` registry outlive this plugin context, so after a plugin reload the
 *  same session is still registered and attached. Creating or resuming over it
 *  would collide, and dropping the old handle would leave its session detached
 *  from the SessionStore — the state in which every append fails. A session
 *  that is stored but not live is resumed; anything else is created fresh. */
async function settleJarvisAgent(
  agents: AgentsService,
  agentsCtx: Context,
  entry: JarvisConfig,
  setup: (agentCtx: Context, agent: unknown) => void,
): Promise<SettledJarvis> {
  const live = agents.get(entry.jarvisSessionId);
  if (live) {
    debug(entry, 'Jarvis agent already live — reusing it');
    return { handle: { agent: live }, existed: true };
  }
  const sessions = (agentsCtx as any).get?.('sessions') as SessionStoreService | undefined;
  const stored = (() => { try { return sessions?.get?.(entry.jarvisSessionId) !== undefined; } catch { return false; } })();
  if (stored) return { handle: await resumeJarvis(agents, entry, setup), existed: true };
  try {
    return {
      handle: await agents.create({
        sessionId: entry.jarvisSessionId,
        // agentPreset is the session's creation record; the composition itself is
        // mounted by whichever preset row carries the id.
        meta: { cwd: resolveDir(entry.jarvisCwd), agentPreset: 'jarvis' },
        agentOptions: agentOptions(entry),
        setup,
      }),
      existed: false,
    };
  } catch (error) {
    // A failed creation may still have left a stored Session (setup events are
    // persisted before publication), and its identity is then unavailable for a
    // new creation. Only a stored-history collision earns the resume retry;
    // every other failure is reported as itself.
    if (!/already exists|already attached/.test(error instanceof Error ? error.message : String(error))) throw error;
    debug(entry, 'create collided with a stored session — resuming instead');
    return { handle: await resumeJarvis(agents, entry, setup), existed: true };
  }
}

/** Resume one stored Jarvis session through the agents registry. */
function resumeJarvis(
  agents: AgentsService,
  entry: JarvisConfig,
  setup: (agentCtx: Context, agent: unknown) => void,
): Promise<{ agent: any }> {
  if (typeof agents.resume !== 'function') throw new Error('agents service offers no resume');
  return agents.resume({ resumeSessionId: entry.jarvisSessionId, agentOptions: agentOptions(entry), setup });
}

// ── Hook 2: decorate Jarvis agent (via agentCtx from setup callback) ───
function decorateJarvisAgent(agentCtx: Context, entry: JarvisConfig, deps: JarvisDeps): void {
  debug(entry, 'decorateJarvisAgent started');
  // Use agentCtx.inject (not ctx.inject) so section registers on AGENT scope
  agentCtx.inject(['systemPrompt' as any], () => {
    debug(entry, 'systemPrompt inject fired (agentCtx)');
    const sp = (agentCtx as any).get?.('systemPrompt') as
      | { section?: (s: { name: string; order: number; text: () => string }) => void }
      | undefined;
    debug(entry, 'sp=' + (sp ? 'found' : 'UNDEFINED') + ' section=' + (sp?.section ? 'found' : 'UNDEFINED'));
    sp?.section?.({
      name: 'jarvis:persona',
      order: 50,
      text: () => COMMANDER_PERSONA,
    });
    const memorySection = new MemorySection(deps.memory, {
      onError: error => debug(entry, 'memory section failed: ' + (error instanceof Error ? error.message : String(error))),
    });
    sp?.section?.({ name: 'jarvis:memory', order: 60, text: () => memorySection.text() });
    // DSH evaluates text synchronously before this awaited, agent-scoped assembly waterfall.
    agentCtx.on('system-prompt/assemble' as any, async (assembly: PromptAssembly, _context: unknown, next: () => Promise<PromptAssembly>) => {
      const section = assembly.sections.find(section => section.name === 'jarvis:memory');
      if (section) {
        section.text = await memorySection.refresh();
        if (section.text.includes('{{')) {
          // Variable values are not interpolated again: preserve literal templates in remembered keys.
          assembly.variables.jarvis_memory_literal = section.text;
          section.text = '{{jarvis_memory_literal}}';
        }
      }
      return next();
    });
    debug(entry, 'persona and memory sections registered OK');
  });

  registerJarvisTools(agentCtx, entry, deps);
}

const textOutput = {
  schema: { type: 'object', additionalProperties: false, properties: { text: { type: 'string', required: true } } },
  render: (_args: unknown, value: unknown) => [{ type: 'text', text: String((value as { text?: unknown })?.text ?? '') }],
};

/** Human-readable session line for the model: title, workspace, status, id. */
function describeSession(s: SessionRow): string {
  const name = s.title || '(untitled)';
  const narration = s.narration ? ` · announce=${s.narration === 'self' ? 'self-report' : 'Jarvis relays'}` : '';
  return `- ${name}${s.workspace ? ` [${s.workspace}]` : ''} · ${s.status}${narration} · id=${s.id}`;
}

function registerJarvisTools(agentCtx: Context, entry: JarvisConfig, deps: JarvisDeps): void {
  const tools = (agentCtx as any).tools as { register: (t: unknown) => () => void } | undefined;
  debug(entry, 'tools=' + (tools ? 'found' : 'UNDEFINED'));
  if (!tools) { debug(entry, 'tools NOT found — cannot register'); return; }
  const selfId = entry.jarvisSessionId;
  const text = (t: string) => ({ text: t });

  tools.register(defineTool({
    name: 'list_approval_rules',
    description: 'List the unexpired workspace presets the user created on approval cards. Returns fingerprint, tool and workspace for exact removal; a preset is only used when the user has enabled the matching auto-approval setting.',
    parameters: {}, output: textOutput, isConcurrencySafe: () => true,
    async execute() { return text(JSON.stringify(await deps.presets.list(), null, 2)); },
  } as never));
  tools.register(defineTool({
    name: 'remove_approval_rule',
    description: 'Remove one approval preset when the user asks to revoke it: pass fingerprint, tool and workspace back exactly as listed.',
    parameters: { fingerprint: { type: 'string' as const, required: true },
      tool: { type: 'string' as const, required: true }, workspace: { type: 'string' as const, required: true } },
    output: textOutput, isConcurrencySafe: () => true,
    async execute(args: ApprovalRuleKey) { return text(await deps.removeApprovalRule(args) ? 'Approval preset removed' : 'Approval preset not found'); },
  } as never));

  tools.register(defineTool({
    name: 'remember',
    description: 'Save a long-term memory when the user explicitly asks you to remember something. The first sentence of content is the key point and the rest is supporting detail; leave session empty to store a general memory, otherwise pass the session id. expiresDays is optional; once it expires the memory is no longer recalled.',
    parameters: {
      content: { type: 'string' as const, required: true },
      tag: { type: 'string' as const }, session: { type: 'string' as const },
      expiresDays: { type: 'number' as const },
    },
    output: textOutput,
    isConcurrencySafe: () => true,
    async execute(args: RememberArgs) { return text(await remember(deps.memory, args)); },
  } as never));

  tools.register(defineTool({
    name: 'recall',
    description: 'Recall past agreements or decisions: searches general long-term memory and that of a named session. A query mentioning approval, approved, rejected or denied also searches approval records. Returns only the key point, source and date; limit defaults to 5 and is capped at 10.',
    parameters: {
      query: { type: 'string' as const, required: true }, session: { type: 'string' as const },
      limit: { type: 'number' as const },
    },
    output: textOutput,
    isConcurrencySafe: () => true,
    async execute(args: RecallArgs) { return text(await recall(deps.memory, args)); },
  } as never));

  tools.register(defineTool({
    name: 'say_to_user',
    description: 'Speak one line to the user in Jarvis\'s own voice (with a chime). One or two sentences; never read out code, paths or Markdown.',
    parameters: { text: { type: 'string' as const, required: true } },
    output: textOutput,
    isConcurrencySafe: () => true,
    async execute(args: { text: string }) {
      const line = String(args.text ?? '').trim();
      if (!line) throw new Error('say_to_user: text must not be empty');
      const how = await deps.say(line);
      return text(how === 'muted' ? 'The user is muted, so nothing was played' : how === 'failed' ? 'Playback failed' : 'Played');
    },
  } as never));

  tools.register(defineTool({
    name: 'ask_user',
    description: 'Ask the user a question and wait for the answer (it pops up in both the panel and the DSH window). Pass choices when there are fixed options. A continuation notice must also pass the session and task it carries, so that declining closes the right task.',
    parameters: {
      question: { type: 'string' as const, required: true },
      choices: { type: 'array' as const, items: { type: 'string' as const } },
      session: { type: 'string' as const },
      task: { type: 'string' as const },
    },
    output: textOutput,
    isConcurrencySafe: () => false,
    async execute(args: { question: string; choices?: unknown; session?: string; task?: string }, exec: { agent?: unknown; signal?: AbortSignal } | undefined) {
      const question = String(args.question ?? '').trim();
      if (!question) throw new Error('ask_user: question must not be empty');
      const choices = Array.isArray(args.choices)
        ? args.choices.map((c) => String(c).trim()).filter((c) => c.length > 0) : [];
      if ((args.session !== undefined || args.task !== undefined) && (!args.session?.trim() || !args.task?.trim())) {
        throw new Error('ask_user: continuation needs both session and task');
      }
      const answer = args.session && args.task
        ? await deps.ask(question, ['Continue', 'No thanks'], exec, { session: args.session, task: args.task })
        : await deps.ask(question, choices, exec);
      return text(answer ? `User answered: ${answer}` : 'The user gave no answer');
    },
  } as never));

  tools.register(defineTool({
    name: 'inject_to_session',
    description: 'Send content to a managed session; it starts working on it immediately. The target cannot be yourself and must be in the managed set.',
    parameters: { session: { type: 'string' as const, required: true }, message: { type: 'string' as const, required: true } },
    output: textOutput,
    isConcurrencySafe: () => true,
    async execute(args: { session: string; message: string }) {
      if (!args.session.trim() || !args.message.trim()) throw new Error('inject_to_session: session/message must not be empty');
      if (args.session === selfId) throw new Error('inject_to_session: cannot target self');
      if (!deps.managed.has(args.session)) {
        throw new Error('inject_to_session: that session is not in the managed set — have the user hand it to you first (manage_session)');
      }
      const target = (agentCtx as any).get?.('agents')?.get?.(args.session);
      if (!target) throw new Error('inject_to_session: target not found');
      const note: UserMessage = {
        id: MessageId(`jarvis-${Date.now().toString(36)}`),
        role: 'user',
        content: [{ type: 'text', text: withNarrationNote(args.message, deps.narration(args.session)) }],
        source: { kind: 'user' },
      };
      const how = deliver(target, note);
      if (!how) throw new Error('inject_to_session: target accepts no messages');
      deps.ledger.open(args.session, args.message);
      return text(how === 'steer' ? 'Injected into the turn already in progress' : 'Sent; the other session has started working');
    },
  } as never));

  tools.register(defineTool({
    name: 'list_managed',
    description: 'List the sessions in the managed set plus the other sessions you could be given (title, workspace, status, id).',
    parameters: {},
    output: textOutput,
    isConcurrencySafe: () => true,
    async execute() {
      const rows = await deps.sessionRows();
      const mine = rows.filter((r) => r.managed);
      const others = rows.filter((r) => !r.managed);
      return text([
        `Managed (${mine.length}):`, ...(mine.length ? mine.map(describeSession) : ['(none)']),
        `Other sessions you could be given (${others.length}):`, ...(others.length ? others.map(describeSession) : ['(none)']),
      ].join('\n'));
    },
  } as never));

  tools.register(defineTool({
    name: 'manage_session',
    description: 'Add a session to the managed set (only when the user explicitly hands it to you). Use the id from list_managed for session.',
    parameters: { session: { type: 'string' as const, required: true } },
    output: textOutput,
    isConcurrencySafe: () => true,
    async execute(args: { session: string }) {
      if (args.session === selfId) throw new Error('manage_session: cannot manage self');
      if (await deps.setManaged(args.session, true) === 'not-found') throw new Error('manage_session: no such session');
      return text('Now managed');
    },
  } as never));

  tools.register(defineTool({
    name: 'set_session_narration',
    description: 'Switch how a managed session reports its results: self has the session report with speak itself (saves tokens), relay has you summarise and retell it, and default restores the default from the user\'s settings.',
    parameters: {
      session: { type: 'string' as const, required: true },
      narration: { type: 'string' as const, required: true },
    },
    output: textOutput,
    isConcurrencySafe: () => true,
    async execute(args: { session: string; narration: string }) {
      const narration = args.narration === 'default' ? undefined : args.narration;
      if (narration !== undefined && !isNarration(narration)) throw new Error('set_session_narration: narration must be self / relay / default');
      if (deps.setNarration(args.session, narration) === 'not-managed') throw new Error('set_session_narration: that session is not in the managed set');
      return text(deps.narration(args.session) === 'self' ? 'The session now reports for itself' : 'You now retell its results');
    },
  } as never));

  tools.register(defineTool({
    name: 'release_session',
    description: 'Remove a session from the managed set.',
    parameters: { session: { type: 'string' as const, required: true } },
    output: textOutput,
    isConcurrencySafe: () => true,
    async execute(args: { session: string }) {
      await deps.setManaged(args.session, false);
      return text('No longer managed');
    },
  } as never));
}

export default { name, inject, Config, apply };
