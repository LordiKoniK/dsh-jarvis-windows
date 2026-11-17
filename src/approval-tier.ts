import { lstatSync, readlinkSync } from 'node:fs';
import { dirname, isAbsolute, join, resolve, sep } from 'node:path';
import { SEPARATORS, WINDOWS, commandName, conventionalRoot, expandHome, hasTraversal,
  hasWindowsPathHazard, isInsideRelative, isSamePath, sameRoot } from './approval-path.ts';

export type ApprovalTier = 'safe' | 'grey' | 'medium' | 'high';
export interface ApprovalOperation {
  tool: string;
  command: string;
  path?: string;
  /** Trusted workspace boundary, not a cwd supplied by the operation being approved. */
  cwd: string;
  /** Complete original tool arguments, before any UI preview truncation. */
  args?: string;
}

// These tables are exported so settings can later present the actual built-in rules.
// Extending a safe rule requires reviewing every supported option's side effects.
export const SAFE_TOOLS = Object.freeze(['read', 'read_file', 'glob', 'grep', 'search', 'list', 'list_directory', 'ls', 'file_search', 'web_search']);
export const WRITE_TOOLS = Object.freeze(['write', 'write_file', 'edit', 'edit_file', 'multiedit']);
/** Shell tools whose `command` argument is a POSIX shell command line. */
export const SHELL_TOOLS = Object.freeze(['bash', 'shell', 'exec', 'exec_command', 'run_command']);
/** PowerShell shell tools. DSH's `pwsh` runs `pwsh -NoLogo -NoProfile
 * -NonInteractive -Command` in the session workspace, so relative paths in the
 * command resolve there and the command text can be inspected. MCP tools such
 * as `mcp__windowsmcp__PowerShell` are deliberately absent: they run with their
 * own working directory, so a redirect validated against the session workspace
 * would describe a different file. */
export const POWERSHELL_TOOLS = Object.freeze(['pwsh', 'powershell']);
/** Shells whose quoting/expansion rules are not modelled by this tokenizer.
 * cmd.exe expands `%VAR%` and consumes `^` outside any quoting it understands,
 * so the displayed command text does not determine the real argv. */
export const OPAQUE_SHELL_TOOLS = Object.freeze(['cmd', 'cmd.exe']);
/** The POSIX command allowlist. It is only consulted for POSIX shell tools: the
 * option tables below encode POSIX semantics, and PowerShell parameter binding
 * and aliases are different enough that borrowing them would be a guess. */
export const SAFE_COMMANDS = Object.freeze(['ls', 'cat', 'head', 'tail', 'wc', 'rg', 'grep', 'find', 'pwd', 'echo', 'which']);
export const HIGH_COMMANDS = Object.freeze(['sudo', 'doas', 'dd', 'launchctl', 'eval', 'exec', 'command', 'env', 'xargs',
  'sh', 'bash', 'zsh', 'dash', 'fish', 'ksh', 'source', '.', 'node', 'nodejs', 'python', 'python3', 'ruby', 'perl',
  'php', 'lua', 'osascript', 'awk', 'gawk', 'sed', 'make', 'npx', 'bun', 'deno', 'ssh', 'su', 'nohup', 'timeout', 'busybox', 'sudoedit', 'tclsh', 'wish', 'swift', 'java', 'js', 'jsc', 'sqlite3', 'mysql', 'psql', 'time', 'nice', 'ionice', 'parallel', 'watch']);
/** Windows programs that hand control to another interpreter or change system
 * state. Applied on Windows for every dialect: `cmd`, `pwsh` and `wsl` are
 * shell escapes no matter which shell hosted them. */
export const WINDOWS_HIGH_COMMANDS = Object.freeze(['pwsh', 'powershell', 'powershell_ise', 'cmd', 'wsl', 'wslconfig',
  'mshta', 'rundll32', 'regsvr32', 'cscript', 'wscript', 'msiexec', 'schtasks', 'bcdedit', 'takeown', 'icacls',
  'cacls', 'reg', 'regedit', 'netsh', 'net', 'wmic', 'certutil', 'bitsadmin', 'forfiles', 'attrib', 'manage-bde',
  'vssadmin', 'diskpart', 'format', 'fsutil', 'powercfg', 'shutdown', 'runas', 'mstsc', 'psexec']);
/** PowerShell cmdlets (and the default aliases they ship with) that execute code
 * or mutate state. They sit outside the read-only Get-/Test-/Select- families.
 * The PowerShell dialect has no `safe` tier, so this list can only raise a
 * command from grey to high, never the other way. */
export const POWERSHELL_HIGH_COMMANDS = Object.freeze(['iex', 'invoke-expression', 'icm', 'invoke-command', 'iwr',
  'invoke-webrequest', 'irm', 'invoke-restmethod', 'start-process', 'saps', 'start-job', 'start-threadjob', 'add-type',
  'set-executionpolicy', 'install-module', 'install-script', 'import-module', 'register-scheduledtask',
  'unregister-scheduledtask', 'new-service', 'set-service', 'stop-service', 'restart-service', 'new-item',
  'remove-item', 'clear-content', 'set-content', 'add-content', 'out-file', 'copy-item', 'move-item', 'rename-item',
  'set-item', 'set-itemproperty', 'remove-itemproperty', 'new-itemproperty', 'set-location', 'push-location',
  'pop-location', 'get-credential', 'convertto-securestring', 'unprotect-cmsmessage', 'remove-module', 'save-module',
  'update-help',
  // Default aliases of the mutating cmdlets above; -NoProfile does not remove the
  // aliases that ship with the engine.
  'rm', 'del', 'erase', 'rd', 'rmdir', 'ri', 'mv', 'move', 'mi', 'cp', 'copy', 'cpi', 'ni', 'mkdir', 'md',
  'sc', 'ac', 'clc', 'si', 'sp', 'spp', 'ren', 'rename', 'rni', 'start']);
export const PROTECTED_DIRECTORIES = Object.freeze(['.ssh', '.dsh']);
export const SAFE_OPTIONS: Readonly<Record<string, readonly string[]>> = Object.freeze({
  ls: Object.freeze(['-l', '-a', '-h', '-la', '-lah', '-al', '-R', '-1', '-d', '-t', '-r', '-S', '--all', '--long', '--human-readable']),
  cat: Object.freeze(['-n', '-b', '-s', '-v', '-E', '-T', '-A']),
  head: Object.freeze(['-n', '-c', '-q', '-v']), tail: Object.freeze(['-n', '-c', '-f', '-F', '-q', '-v']),
  wc: Object.freeze(['-l', '-w', '-c', '-m', '-L']),
  rg: Object.freeze(['-n', '-l', '-i', '-F', '-e', '--regexp', '--files', '--hidden', '--glob', '-g', '--type', '-t',
    '--count', '-c', '-A', '-B', '-C', '--context', '--max-count', '-m', '--no-heading', '--line-number', '--files-with-matches', '--fixed-strings']),
  grep: Object.freeze(['-n', '-l', '-i', '-r', '-R', '-F', '-E', '-c', '-e', '-A', '-B', '-C', '-m', '--color']),
  find: Object.freeze(['-name', '-iname', '-type', '-maxdepth', '-mindepth', '-print', '-print0', '-size', '-mtime', '-atime', '-ctime', '-path', '-ipath', '-empty']),
  pwd: Object.freeze(['-L', '-P']), echo: Object.freeze(['-n', '-e', '-E']), which: Object.freeze(['-a', '-s']),
});
export const GIT_READ_OPTIONS = Object.freeze(['--short', '--porcelain', '--stat', '--oneline', '--all', '--cached', '--staged',
  '--name-only', '--name-status', '--no-ext-diff', '--no-textconv', '--no-pager', '--patch', '-p', '--summary', '--numstat',
  '--check', '--raw', '--decorate', '--format', '--pretty', '-n', '--max-count', '--graph', '--color', '--no-color', '-U', '--unified',
  '--abbrev-commit', '--no-renames', '--exit-code', '--quiet', '--relative', '--date']);

function hasProtectedPart(path: string): boolean {
  return path.split(/[\\/]/).some(part => PROTECTED_DIRECTORIES.includes(part.toLowerCase()));
}

/** Walk components before normalization. Missing leaves are fine; dangling links,
 * link loops, and traversal components fail closed. Retain protection seen in
 * intermediate symlink targets, even when a later link resolves elsewhere. */
function canonical(path: string, depth = 0): { path: string; protected: boolean; exists: boolean } {
  if (path.includes('\0') || hasTraversal(path) || hasWindowsPathHazard(path) || depth > 40) throw Error('Ambiguous path');
  const root = conventionalRoot(path);
  if (!root) throw Error('Ambiguous path');
  let current = root;
  let protectedPath = hasProtectedPart(path);
  let exists = true;
  for (const part of path.slice(root.length).split(SEPARATORS).filter(part => part && part !== '.')) {
    current = join(current, part);
    if (!exists) continue;
    try {
      const stat = lstatSync(current);
      if (stat.isSymbolicLink()) {
        const target = windowsLinkTarget(readlinkSync(current));
        const resolved = canonical(isAbsolute(target) ? target : dirname(current) + sep + target, depth + 1);
        if (!resolved.exists) throw Error('Dangling symbolic link');
        protectedPath ||= resolved.protected;
        current = resolved.path;
      }
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
      exists = false;
    }
  }
  return { path: current, protected: protectedPath, exists };
}
/** Junction targets come back from Win32 in the extended-length namespace
 * (`\\?\C:\...`). Strip that wrapper â€” it is filesystem metadata, not attacker
 * text â€” but refuse a target that is left in a namespace or device we cannot
 * resolve. */
function windowsLinkTarget(target: string): string {
  if (!WINDOWS || !/^\\\\\?\\/.test(target)) return target;
  const rest = target.slice(4);
  const normal = /^UNC\\/i.test(rest) ? '\\\\' + rest.slice(4) : rest;
  if (!isAbsolute(normal) || hasWindowsPathHazard(normal)) throw Error('Opaque symbolic link target');
  return normal;
}
function writeInside(path: string, cwd: string): boolean {
  if (!path || path.includes('\0') || hasTraversal(path)) return false;
  const expanded = expandHome(path);
  if (expanded === undefined || hasWindowsPathHazard(expanded)) return false;
  try {
    const absolute = isAbsolute(expanded) ? expanded : resolve(cwd, expanded);
    // Prove a different drive/share is outside before stat() can reach a remote
    // share; the metadata walk itself is only safe for roots we share with cwd.
    if (!sameRoot(absolute, cwd)) return false;
    const candidate = canonical(absolute);
    const workspace = canonical(cwd);
    return !candidate.protected && !workspace.protected && isInsideRelative(candidate.path, workspace.path);
  } catch { return false; }
}

interface Token { value: string; redirect: boolean }
/** Shells whose command text means what it says. `opaque` shells (cmd.exe) are
 * rejected before parsing because their expansion rules are not modelled. */
type ShellDialect = 'posix' | 'powershell';
/** Punctuation that starts a construct this parser does not evaluate. Backslash
 * is an escape in POSIX shells but an ordinary path separator in PowerShell, so
 * the two dialects cannot share one character class. `%` and `@` are PowerShell
 * expansion/splatting syntax (and `%` is also cmd.exe expansion); POSIX keeps its
 * original class so the macOS rules do not change. */
const AMBIGUOUS_POSIX = /[;&|<$`\\(){}*?\[\]!#]/;
const AMBIGUOUS_POWERSHELL = /[;&|<$`(){}*?\[\]!#%@]/;

/** Intentionally a shell subset, never an evaluator. Quoted punctuation is data;
 * active expansions, escapes, pipelines, chains and subshells are opaque/high. */
function tokenize(command: string, dialect: ShellDialect): Token[] | undefined {
  const tokens: Token[] = [];
  const ambiguous = dialect === 'posix' ? AMBIGUOUS_POSIX : AMBIGUOUS_POWERSHELL;
  let value = '', started = false, quote = '';
  const flush = () => { if (started) tokens.push({ value, redirect: false }); value = ''; started = false; };
  for (let i = 0; i < command.length; i++) {
    const char = command[i];
    if (/[\0\r\n]/.test(char)) return;
    if (quote) {
      if (char === quote) {
        // PowerShell doubles the quote to escape it inside a string; joining the
        // two halves would silently change a path, so refuse instead.
        if (dialect === 'powershell' && command[i + 1] === quote) return;
        quote = ''; continue;
      }
      if (quote === '"' && (/[$`]/.test(char) || dialect === 'posix' && char === '\\')) return;
      value += char; continue;
    }
    if (char === "'" || char === '"') { quote = char; started = true; continue; }
    if (/\s/.test(char)) { flush(); continue; }
    if (char === '>') {
      flush(); const append = command[i + 1] === '>'; if (append) i++;
      tokens.push({ value: append ? '>>' : '>', redirect: true }); continue;
    }
    if (ambiguous.test(char)) return;
    started = true; value += char;
  }
  if (quote) return;
  flush(); return tokens;
}
function optionsAllowed(words: string[], options: readonly string[]): boolean {
  let positional = false;
  for (const word of words) {
    if (word === '--') { positional = true; continue; }
    if (!positional && word.startsWith('-') && !options.includes(word.split('=')[0])) return false;
  }
  return true;
}
function dangerousFlag(words: string[], pattern: RegExp): boolean { return words.some(word => pattern.test(word)); }
function opaqueCwd(words: string[]): boolean {
  return dangerousFlag(words, /^(?:--(?:cwd|dir|directory|prefix|work-tree|git-dir|config-env)|-C)(?:=|$)/) ||
    words.some(word => /^-C.+/.test(word));
}
/** PowerShell cmdlets are PascalCase; the verb is the part before the dash. All
 * listed verbs mutate state or execute code, so matching them can only raise a
 * tier. Get-/Test-/Select-/Measure-/Compare-/Format-/ConvertTo- stay grey. */
const POWERSHELL_MUTATING_VERB = /^(?:Add|Clear|Close|Copy|Disable|Dismount|Edit|Enable|Export|Grant|Import|Install|Invoke|Lock|Mount|Move|New|Optimize|Protect|Publish|Register|Remove|Rename|Repair|Reset|Restart|Restore|Resume|Revoke|Save|Set|Start|Stop|Suspend|Uninstall|Unlock|Unpublish|Unregister|Update)-/i;

function shellTier(command: string, cwd: string, dialect: ShellDialect): ApprovalTier {
  if (/\bDROP\s+(?:TABLE|DATABASE)\b/i.test(command)) return 'high';
  const tokens = tokenize(command, dialect);
  if (!tokens?.length || tokens[0].redirect) return 'high';
  const redirects: string[] = [], words: string[] = [];
  for (let i = 0; i < tokens.length; i++) {
    if (tokens[i].redirect) {
      const target = tokens[++i];
      if (!target || target.redirect || !writeInside(target.value, cwd)) return 'high';
      redirects.push(target.value);
    } else words.push(tokens[i].value);
  }
  const executable = words.shift()!;
  if (!executable || executable.includes('=') || executable.startsWith('~')) return 'high';
  // Resolving the executable through the platform's trusted directories is what
  // keeps a PATH-planted or workspace-planted `ls` from borrowing `ls`'s tier.
  const name = commandName(executable);
  if (!name) return 'high';
  // POSIX command names are case-sensitive; Win32 and PowerShell resolve them
  // case-insensitively, so only those lookups fold case.
  const lookup = WINDOWS || dialect === 'powershell' ? name.toLowerCase() : name;
  if (HIGH_COMMANDS.includes(lookup) || /^mkfs(?:\.|$)/.test(lookup) || /^(?:python|perl|ruby|php)\d/.test(lookup)) return 'high';
  // Windows shells/state-changing programs are dangerous for any dialect that can
  // run them, including a PowerShell tool hosted on a non-Windows OS.
  if ((WINDOWS || dialect === 'powershell') && WINDOWS_HIGH_COMMANDS.includes(lookup)) return 'high';
  if (dialect === 'powershell' && (POWERSHELL_HIGH_COMMANDS.includes(lookup) || POWERSHELL_MUTATING_VERB.test(lookup))) return 'high';
  if (opaqueCwd(words) || words.some(word => /^~[^/\\]/.test(word))) return 'high';
  let tier: ApprovalTier;
  if (dialect === 'powershell') {
    // No auto-approve tier for PowerShell. The command and option tables below
    // encode POSIX shell semantics; PowerShell aliases, parameter binding and
    // redirection differ, so a POSIX-derived `safe` verdict would be a guess.
    // Everything that is not high stays grey and goes to model review or a human.
    tier = 'grey';
  } else if (SAFE_COMMANDS.includes(lookup)) {
    // echo does not execute its literal arguments; other read commands use a
    // narrow option allowlist to exclude preprocessors/output/config switches.
    tier = lookup === 'find' && words.includes('--') ? 'high' : lookup === 'echo' || optionsAllowed(words, SAFE_OPTIONS[lookup]) ? 'safe' : 'high';
  } else if (lookup === 'git') {
    tier = gitTier(words);
  } else if (lookup === 'rm') {
    tier = dangerousFlag(words, /^(?:--rec|-[^-]*[rR])/) ? 'high' : pathsTier(words, cwd);
  } else if (lookup === 'chmod' || lookup === 'chown') {
    tier = dangerousFlag(words, /^(?:--rec|-[^-]*[rR])/) ? 'high' : pathsTier(words.slice(1), cwd);
  } else if (lookup === 'kill') {
    tier = words.includes('1') && dangerousFlag(words, /^(?:-9|-KILL|-SIGKILL|KILL|SIGKILL|9|--signal=(?:9|KILL|SIGKILL))$/i) ? 'high' : 'grey';
  } else if (['mv', 'cp', 'touch', 'mkdir'].includes(lookup)) {
    tier = pathsTier(words, cwd);
  } else if (['npm', 'pnpm', 'yarn', 'pip', 'pip3'].includes(lookup)) {
    tier = installTier(lookup, words, cwd);
  } else if (lookup === 'curl' || lookup === 'wget') {
    tier = networkTier(lookup, words, cwd);
  } else {
    // Unknown simple tools are the only shell forms eligible for model review.
    tier = 'grey';
  }
  // Redirects always mutate, and must not disguise an otherwise unknown program.
  return redirects.length && tier !== 'high' ? tier === 'safe' || tier === 'medium' ? 'medium' : 'high' : tier;
}
function gitTier(words: string[]): ApprovalTier {
  const [subcommand, ...args] = words;
  if (!subcommand || subcommand.startsWith('-')) return 'high';
  if (dangerousFlag(args, /^--(?:ext-diff|textconv|output|exec|upload-pack|receive-pack|config|work-tree|git-dir)(?:=|$)/)) return 'high';
  if (subcommand === 'reset' && args.some(arg => arg.startsWith('--hard'))) return 'high';
  if (subcommand === 'clean') return 'high';
  if (['status', 'diff', 'log', 'show'].includes(subcommand)) {
    return optionsAllowed(args, GIT_READ_OPTIONS) ? 'safe' : 'high';
  }
  if (subcommand === 'branch') {
    if (!args.length) return 'safe';
    if (args.every(arg => ['--show-current', '-a', '--all', '-r', '--remotes', '-v', '-vv', '--verbose'].includes(arg))) return 'safe';
    if (args[0] === '--list' || args[0] === '-l') return optionsAllowed(args.slice(1), ['-a', '--all', '-r', '--remotes']) ? 'safe' : 'high';
    return optionsAllowed(args, ['-d', '-D', '--delete', '-m', '-M', '--move', '-c', '-C', '--copy', '--force', '-f']) ? 'grey' : 'high';
  }
  if (subcommand === 'push') {
    if (dangerousFlag(args, /^(?:\+|--(?:for|mirror|delete)|-[^-]*f)/)) return 'high';
    if (!optionsAllowed(args, ['-u', '--set-upstream', '--dry-run', '--verbose', '-v', '--tags', '--all', '--atomic', '--porcelain'])) return 'high';
    if (args.some(arg => /^(?:ext::|[^\s]+::|file:|\/|\.\.?\/|~)/.test(arg))) return 'high';
    return 'medium';
  }
  if (subcommand === 'commit') return optionsAllowed(args, ['-m', '--message', '-a', '--all', '--amend', '--no-edit', '--allow-empty', '--dry-run', '-v', '--verbose']) ? 'medium' : 'high';
  return ['fetch', 'checkout', 'switch', 'restore', 'merge', 'rebase', 'tag', 'stash', 'remote'].includes(subcommand) &&
    args.every(arg => !arg.startsWith('-')) ? 'grey' : 'high';
}
function pathsTier(words: string[], cwd: string): ApprovalTier {
  const paths = words.filter(word => word !== '--');
  // Avoid option values that designate alternate targets or execute programs.
  if (paths.some(word => word.startsWith('-') && !['-f', '-i', '-n', '-v', '-p', '-r', '-R'].includes(word))) return 'high';
  const operands = paths.filter(word => !word.startsWith('-'));
  return operands.length && operands.every(path => writeInside(path, cwd)) ? 'medium' : 'high';
}
function installTier(name: string, words: string[], cwd: string): ApprovalTier {
  const [subcommand, ...args] = words;
  if (args.includes('--')) return 'high';
  // Project script execution is deliberately grey: the context/model decision
  // must justify it. Syntax/option/cwd hazards are still rejected before here.
  if (['npm', 'pnpm', 'yarn'].includes(name) && ['test', 'run', 'build', 'start', 'lint', 'check', 'dev'].includes(subcommand)) {
    return args.every(arg => /^[a-zA-Z0-9_:@./-]+$/.test(arg) && !arg.startsWith('-') && !HIGH_COMMANDS.includes(arg) && arg !== 'rm') ? 'grey' : 'high';
  }
  if (!['install', 'add', 'ci'].includes(subcommand)) return 'high';
  const options = ['--save-dev', '-D', '--save-exact', '-E', '--ignore-scripts', '--no-audit', '--no-fund', '--offline', '--silent', '--dry-run', '--frozen-lockfile'];
  if (name === 'pip' || name === 'pip3') {
    if (subcommand !== 'install') return 'high';
    let target = '';
    const remaining: string[] = [];
    for (let i = 0; i < args.length; i++) {
      if (args[i] === '--target' || args[i] === '-t') { target = args[++i] ?? ''; continue; }
      if (args[i].startsWith('--target=')) { target = args[i].slice(9); continue; }
      remaining.push(args[i]);
    }
    return target && writeInside(target, cwd) && optionsAllowed(remaining, ['--upgrade', '-U', '--no-deps', '--quiet', '-q']) ? 'medium' : 'high';
  }
  if (!optionsAllowed(args, options)) return 'high';
  if (args.some(arg => (arg.startsWith('/') || arg.startsWith('.') || arg.startsWith('~') || arg.startsWith('file:')) && !writeInside(arg.replace(/^file:/, ''), cwd))) return 'high';
  return 'medium';
}
function networkTier(name: string, words: string[], cwd: string): ApprovalTier {
  const options = name === 'curl' ? ['--fail', '--silent', '--show-error', '--location', '-f', '-s', '-S', '-L', '-I', '--head', '-X', '--request', '-H', '--header', '-d', '--data', '--data-raw', '--max-time'] : ['-q', '--quiet', '--timeout'];
  for (let i = 0; i < words.length; i++) {
    const arg = words[i];
    const output = name === 'curl' ? /^(?:-o|--output)(?:=|$)/ : /^(?:-O|--output-document)(?:=|$)/;
    if (output.test(arg)) {
      const path = arg.includes('=') ? arg.slice(arg.indexOf('=') + 1) : words[++i];
      if (!path || !writeInside(path, cwd)) return 'high';
    } else if (name === 'curl' && arg.startsWith('-o') && arg.length > 2 || name === 'wget' && arg.startsWith('-O') && arg.length > 2) {
      if (!writeInside(arg.slice(2), cwd)) return 'high';
    } else if (arg.startsWith('-') && !options.includes(arg.split('=')[0])) return 'high';
    // Network payloads can reference files, but do not write those files.
  }
  return 'medium';
}

/** Pure classification: no subprocesses, model calls, writes, or DSH imports.
 * Filesystem metadata is read synchronously only to validate the write boundary. */
export function classify(operation: ApprovalOperation): ApprovalTier {
  try {
    const { tool, command, cwd, args } = operation;
    if (typeof tool !== 'string' || !tool.trim() || typeof command !== 'string' || !command.trim() ||
      typeof cwd !== 'string' || !isAbsolute(cwd) || cwd.includes('\0')) return 'high';
    const workspace = canonical(cwd);
    if (!workspace.exists) return 'high';
    let data: Record<string, unknown> = {};
    if (args !== undefined) {
      if (typeof args !== 'string') return 'high';
      const parsed = JSON.parse(args);
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return 'high';
      data = parsed;
    }
    for (const key of ['cwd', 'workdir', 'working_directory', 'workspace']) {
      // A different working directory re-points every relative path in the
      // operation, so only an exact alias of the workspace is accepted. Win32
      // compares case-insensitively and ignores a trailing separator.
      if (data[key] !== undefined && (typeof data[key] !== 'string' || !isAbsolute(data[key]) ||
        !sameRoot(data[key], cwd) || !isSamePath(canonical(data[key]).path, workspace.path))) return 'high';
    }
    const name = tool.toLowerCase();
    for (const [tools, dialect] of [[SHELL_TOOLS, 'posix'], [POWERSHELL_TOOLS, 'powershell']] as const) {
      if (!tools.includes(name)) continue;
      for (const key of ['command', 'cmd', 'script']) if (data[key] !== undefined && data[key] !== command) return 'high';
      return shellTier(command, cwd, dialect);
    }
    // cmd.exe expands `%VAR%` and consumes `^` outside any quoting this module
    // models, so the displayed command text does not determine the real argv.
    if (OPAQUE_SHELL_TOOLS.includes(name)) return 'high';
    if (WRITE_TOOLS.includes(name)) {
      const acceptedFields = ['path', 'file_path', 'filePath', 'content', 'text', 'old_string', 'new_string', 'oldString', 'newString',
        'old_text', 'new_text', 'replace_all', 'replaceAll', 'create', 'append', 'encoding', 'cwd', 'workdir', 'working_directory', 'workspace'];
      if (Object.keys(data).some(key => !acceptedFields.includes(key))) return 'high';
      const paths = [operation.path, data.path, data.file_path, data.filePath].filter(path => path !== undefined);
      if (!paths.length) paths.push(command);
      return paths.every(path => typeof path === 'string' && writeInside(path, cwd)) ? 'medium' : 'high';
    }
    if (SAFE_TOOLS.includes(name)) return 'safe';
    return 'grey';
  } catch { return 'high'; }
}
