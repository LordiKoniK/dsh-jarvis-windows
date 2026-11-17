import { homedir } from 'node:os';
import { basename, dirname, isAbsolute, parse, relative, resolve, sep } from 'node:path';

/** Two different axes meet in these classifiers and must not be conflated:
 * the host platform decides how a *filesystem* path is spelled, while the tool
 * name decides which *shell grammar* its `command` argument uses. The POSIX
 * assumptions that broke both modules came from mixing them up. */
export const WINDOWS = process.platform === 'win32';

/** Win32 accepts both separators inside a path, so a traversal check that only
 * splits on `/` misses `..\` entirely; POSIX must keep `\` literal because it is
 * a legal filename character there. */
export const SEPARATORS = WINDOWS ? /[\\/]/ : /\//;

// Win32 resolves these names to devices even with an extension or a trailing
// dot, so `NUL` and `nul.txt` both discard writes instead of creating a file.
const RESERVED_DEVICE = /^(?:CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[1-9]|LPT[1-9])(?:\..*)?$/i;

/** True when the path contains a `..` component, checked before any lexical
 * normalisation. `link/../x` is not the same file as `x` when `link` is a
 * symbolic link, so normalising first can approve a write to a path the panel
 * never displayed. */
export function hasTraversal(value: string): boolean {
  return value.split(SEPARATORS).includes('..');
}

/** Expand the shell's home shorthand. POSIX shells and PowerShell both expand a
 * leading `~/`; `~user` is not portable and PowerShell does not expand it, so
 * it stays rejected (undefined) rather than guessed. */
export function expandHome(value: string): string | undefined {
  if (value === '~') return homedir();
  if (value.startsWith('~/') || WINDOWS && value.startsWith('~\\')) return homedir() + value.slice(1);
  return value.startsWith('~') ? undefined : value;
}

/** Win32 silently rewrites several lexical forms, so the path compared against
 * the workspace can be a different file from the one that gets opened. Reject
 * the forms whose effective name cannot be established from the string:
 *  - `\\?\` and `\\.\` namespaces, which skip normal Win32 path handling;
 *  - alternate data streams, where `file.txt:stream` writes a hidden stream of
 *    an existing file rather than a new path;
 *  - trailing dots/spaces, which Win32 trims (`file.txt.` opens `file.txt`);
 *  - reserved DOS device names, which are never ordinary files;
 *  - characters Win32 forbids in names but the NT namespace accepts.
 * POSIX allows most of these characters in filenames, so this is Windows-only. */
export function hasWindowsPathHazard(value: string): boolean {
  if (!WINDOWS) return false;
  if (/^[\\/]{2}[?.][\\/]/.test(value)) return true;
  // `C:foo` is drive-relative: it means "relative to the current directory on
  // C:", which is not the workspace and is not visible in the string. Only
  // `C:\foo` and `C:/foo` name a fixed location.
  if (/^[A-Za-z]:(?![\\/])/.test(value)) return true;
  // Drop only the drive colon; any later colon is an alternate data stream.
  const body = value.replace(/^[A-Za-z]:/, '');
  return body.split(SEPARATORS).some(part => {
    if (part === '' || part === '.' || part === '..') return false;
    if (/[<>:"|?*\u0000-\u001f]/.test(part)) return true;
    if (/[. ]$/.test(part)) return true;
    return RESERVED_DEVICE.test(part);
  });
}

/** The root a Win32 absolute path must have: a drive (`C:\`) or a UNC share
 * (`\\server\share\`). `/x` and `\x` are absolute to node:path yet still depend
 * on the process' current drive, so their effective location is ambiguous. */
function conventionalWindowsRoot(root: string): boolean {
  return /^[A-Za-z]:[\\/]$/.test(root) || /^[\\/]{2}[^\\/]+[\\/][^\\/]+[\\/]$/.test(root);
}

/** Return the path root when the path is absolute and unambiguous on this
 * platform, otherwise undefined. Callers fail closed on undefined. */
export function conventionalRoot(path: string): string | undefined {
  if (!isAbsolute(path)) return undefined;
  const root = parse(path).root;
  if (!root) return undefined;
  return WINDOWS && !conventionalWindowsRoot(root) ? undefined : root;
}

/** Compare two already-canonical absolute paths the way the filesystem does:
 * Win32 is case-insensitive, POSIX is not. */
export function isSamePath(a: string, b: string): boolean {
  return WINDOWS ? a.toLowerCase() === b.toLowerCase() : a === b;
}

/** Compare path roots without touching the filesystem. A path on another drive
 * or another UNC share cannot be inside the workspace, and statting a remote
 * share just to prove that is slow and influenced by whoever controls the name. */
export function sameRoot(a: string, b: string): boolean {
  const rootA = conventionalRoot(a);
  const rootB = conventionalRoot(b);
  return rootA !== undefined && rootB !== undefined && isSamePath(rootA, rootB);
}

const WINDOWS_COMMAND_EXTENSION = /\.(?:exe|com|cmd|bat)$/i;

/** Normalise an executable token to the reviewed command name, or undefined when
 * the token is neither a bare name nor an absolute path inside the platform's
 * system directories. That absolute-path branch is what stops `/tmp/ls` or
 * `C:\Temp\ls.exe` from borrowing the allowlist tier of `ls`, and the Windows
 * directory list replaces the POSIX-only `/bin`, `/usr/bin` assumption. */
export function commandName(executable: string): string | undefined {
  if (!executable || executable.includes('\0')) return undefined;
  const bare = basename(executable);
  const name = WINDOWS ? bare.replace(WINDOWS_COMMAND_EXTENSION, '') : bare;
  if (!name) return undefined;
  if (executable === bare) return name;
  if (!isAbsolute(executable) || hasTraversal(executable) || hasWindowsPathHazard(executable)) return undefined;
  const dir = dirname(executable);
  if (WINDOWS) {
    const systemRoot = (process.env.SystemRoot || process.env.windir || 'C:\\Windows').replace(/[\\/]+$/, '').toLowerCase();
    const trusted = [
      `${systemRoot}\\system32`,
      `${systemRoot}\\syswow64`,
      `${systemRoot}\\system32\\windowspowershell\\v1.0`,
      `${systemRoot}\\windowspowershell\\v1.0`,
    ];
    return trusted.includes(resolve(dir).toLowerCase()) ? name : undefined;
  }
  return ['/bin', '/usr/bin'].includes(dir) ? name : undefined;
}

/** A relative path compared against a workspace must not sneak out through a
 * different drive or share: `relative()` then returns an absolute path, which is
 * treated as an escape. */
export function isInsideRelative(candidate: string, workspace: string): boolean {
  const rel = relative(workspace, candidate);
  return !isAbsolute(rel) && rel !== '..' && !rel.startsWith(`..${sep}`);
}
