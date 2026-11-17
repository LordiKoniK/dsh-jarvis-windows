// Local Kokoro TTS child-process synth. Args (all named):
//
//   --text <text> --out <out.wav> [--voice af_heart] [--speed 1.0]
//   [--lang en-us] [--model-dir DIR] [--model-file kokoro-v1.0.onnx]
//   [--voices-file voices-v1.0.bin] [--threads 4] [--python python.exe]
//   [--worker kokoro_worker.py] [--ready-timeout 180] [--timeout 180]
//
// WHY a child process: the parent judges success by the output FILE, so
// synthesis must never be able to take the host down with it. The Kokoro model
// is ~325 MB of fp32 weights and ONNX Runtime is a native library; a crash or an
// OOM belongs in this process, not in DSH.
//
// WHY this speaks to the worker rather than importing a library: `kokoro_worker.py`
// owns the model, the thread cap and the WAV encoding. Loading the model twice
// (once here, once there) would double both the memory and the load latency.
//
// ONE WORKER PER UTTERANCE. The worker protocol is long-lived — the model loads
// once and the process then answers many requests — but the plugin's contract is
// "synthesize a file, then play it", so a fresh worker is started here, asked for
// exactly one utterance, and shut down. That keeps the parent's success test
// (child exit code + a file that exists) intact and means no Python is left
// holding 400+ MB of resident weights between utterances.
import { spawn } from 'node:child_process';
import { writeFileSync } from 'node:fs';

function parseArgs(argv) {
  const values = {};
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (!token.startsWith('--')) throw new Error(`unexpected argument: ${token}`);
    // --out is the one flag whose value may legitimately start with dashes.
    const eq = token.indexOf('=');
    if (eq > 0) values[token.slice(2, eq)] = token.slice(eq + 1);
    else { values[token.slice(2)] = argv[index + 1] ?? ''; index += 1; }
  }
  return values;
}

const args = parseArgs(process.argv.slice(2));
const text = args.text ?? '';
const outFile = args.out ?? '';
if (!text.trim() || !outFile) {
  console.error('usage: synth-kokoro.mjs --text <text> --out <out.wav> [--voice af_heart] [--model-dir DIR]');
  process.exit(2);
}

const python = args.python || 'python';
const worker = args.worker;
if (!worker) { console.error('synth-kokoro: --worker is required'); process.exit(2); }

const threads = String(args.threads ?? '4');
const readySeconds = Number(args['ready-timeout'] ?? 180);
const synthSeconds = Number(args.timeout ?? 180);

/** The worker reads all of these from the environment; defaults live in Python. */
const env = { ...process.env };
if (args['model-dir']) env.KOKORO_MODEL_DIR = args['model-dir'];
if (args['model-file']) env.KOKORO_MODEL_FILE = args['model-file'];
if (args['voices-file']) env.KOKORO_VOICES_FILE = args['voices-file'];
if (args.voice) env.KOKORO_VOICE = args.voice;
if (args.lang) env.KOKORO_LANG = args.lang;
if (args.speed) env.KOKORO_SPEED = String(args.speed);
// Capping the ONNX Runtime thread count is what keeps this machine out of
// thermal throttling: uncapped, fp32 inference pegs every core and the package
// reached ~89 C. Four threads measured a real-time factor of 0.391, still 2.5x
// faster than playback. An explicit 0 means "no cap" and is passed through.
env.KOKORO_THREADS = threads;

let child;
let stdoutBuffer = '';
let stderrTail = '';
let finished = false;

/** Leave nothing behind: a killed worker cannot hold the model in RAM. */
function shutdown() {
  if (!child || child.exitCode !== null) return;
  try { child.stdin.end(); } catch { /* already closed */ }
  try { child.kill(); } catch { /* already gone */ }
}

function fail(message) {
  if (finished) return;
  finished = true;
  clearTimeout(readyTimer);
  clearTimeout(synthTimer);
  shutdown();
  const detail = stderrTail.trim().split('\n').slice(-3).join(' | ');
  console.error('synth-kokoro failed:', message + (detail ? ' — ' + detail : ''));
  process.exit(1);
}

// Write BEFORE exiting: the parent only ever looks at the file.
function succeed(wav) {
  if (finished) return;
  finished = true;
  clearTimeout(readyTimer);
  clearTimeout(synthTimer);
  try { writeFileSync(outFile, wav); } catch (error) { fail('could not write ' + outFile + ': ' + error.message); return; }
  shutdown();
  process.exit(0);
}

try {
  child = spawn(python, [worker], {
    stdio: ['pipe', 'pipe', 'pipe'],
    // ELECTRON_RUN_AS_NODE is for the Node synth children; a Python interpreter
    // ignores it, and it is dropped here so nothing downstream inherits it.
    env: { ...env, ELECTRON_RUN_AS_NODE: undefined },
    windowsHide: true,
  });
} catch (error) {
  console.error('synth-kokoro failed: could not start', python + ':', error.message);
  process.exit(1);
}

const readyTimer = setTimeout(
  () => fail(`the speech worker did not load the model within ${readySeconds}s`), readySeconds * 1000);
let synthTimer = null;

child.stdout.setEncoding('utf8');
child.stdout.on('data', chunk => {
  stdoutBuffer += chunk;
  let index;
  while ((index = stdoutBuffer.indexOf('\n')) >= 0) {
    const line = stdoutBuffer.slice(0, index);
    stdoutBuffer = stdoutBuffer.slice(index + 1);
    handleLine(line);
  }
});

child.stderr.setEncoding('utf8');
child.stderr.on('data', chunk => {
  stderrTail = (stderrTail + chunk).slice(-2000);
});

child.on('error', error => fail('speech worker error: ' + error.message));
child.on('exit', code => fail(`the speech worker exited (code ${code}) before producing audio`));

function handleLine(line) {
  if (!line.trim()) return;
  let message;
  try { message = JSON.parse(line); } catch { return; }

  if (message.event === 'ready') {
    clearTimeout(readyTimer);
    synthTimer = setTimeout(() => fail(`the speech worker did not synthesize within ${synthSeconds}s`), synthSeconds * 1000);
    child.stdin.write(JSON.stringify({
      id: 1, text,
      ...(args.voice ? { voice: args.voice } : {}),
      ...(args.speed ? { speed: Number(args.speed) } : {}),
      ...(args.lang ? { lang: args.lang } : {}),
    }) + '\n');
    return;
  }
  if (message.event === 'fatal') { fail('speech worker: ' + message.error); return; }
  if (message.event) return;
  if (message.error) { fail('speech worker: ' + message.error); return; }
  if (!message.wav) { fail('speech worker returned no audio'); return; }
  succeed(Buffer.from(message.wav, 'base64'));
}

// A signal from the parent (or a Ctrl-C while testing) must not orphan Python.
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => { shutdown(); process.exit(1); });
