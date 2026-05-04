import './styles.css';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import * as api from './api';

function randomSessionId(): string {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID();
  }
  return `s-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

function bytesToBase64(bytes: Uint8Array): string {
  const CHUNK = 0x8000;
  const parts: string[] = [];
  for (let i = 0; i < bytes.length; i += CHUNK) {
    parts.push(String.fromCharCode(...bytes.subarray(i, i + CHUNK)));
  }
  return btoa(parts.join(''));
}

function base64ToBytes(b64: string): Uint8Array {
  const binary = atob(b64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

async function main(): Promise<void> {
  const terminalEl = document.getElementById('terminal');
  if (!terminalEl) {
    throw new Error('expected #terminal in DOM');
  }

  const term = new Terminal({
    fontFamily: 'ui-monospace, SFMono-Regular, Menlo, monospace',
    fontSize: 13,
    theme: { background: '#000000' },
    cursorBlink: true,
    allowProposedApi: true,
  });
  const fit = new FitAddon();
  term.loadAddon(fit);
  term.open(terminalEl);
  fit.fit();

  await api.start();

  const sessionId = randomSessionId();
  const encoder = new TextEncoder();

  api.onPtyOutput((n) => {
    if (n.sessionId !== sessionId) return;
    term.write(base64ToBytes(n.dataBase64));
  });

  api.onPtyExit((n) => {
    if (n.sessionId !== sessionId) return;
    term.write(`\r\n\x1b[33m[process exited (${n.exitCode})]\x1b[0m\r\n`);
  });

  try {
    await api.ptySpawn({ sessionId, cols: term.cols, rows: term.rows });
  } catch (err) {
    term.write(`\r\n\x1b[31mfailed to spawn shell: ${(err as Error).message}\x1b[0m\r\n`);
    return;
  }

  term.onData((data) => {
    void api.ptyWrite({ sessionId, dataBase64: bytesToBase64(encoder.encode(data)) });
  });

  // The sidecar coalesces resize requests over 50 ms (ADR-0006); no UI debounce.
  term.onResize(({ cols, rows }) => {
    void api.ptyResize({ sessionId, cols, rows });
  });

  // xterm's onResize only fires from term.resize(...) — observe the container
  // and let FitAddon translate DOM size changes into terminal cell counts.
  const observer = new ResizeObserver(() => {
    fit.fit();
  });
  observer.observe(terminalEl);

  term.focus();
}

main().catch((err: unknown) => {
  console.error(err);
});
