import './styles.css';
import { Terminal } from '@xterm/xterm';
import * as api from './api';

const RESIZE_DEBOUNCE_MS = 80;

function randomSessionId(): string {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID();
  }
  return `s-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

function bytesToBase64(bytes: Uint8Array): string {
  let binary = '';
  for (let i = 0; i < bytes.length; i++) {
    binary += String.fromCharCode(bytes[i]);
  }
  return btoa(binary);
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
  term.open(terminalEl);

  await api.start();

  const sessionId = randomSessionId();
  const encoder = new TextEncoder();

  const cols = term.cols;
  const rows = term.rows;

  api.onPtyOutput((n) => {
    if (n.sessionId !== sessionId) return;
    term.write(base64ToBytes(n.dataBase64));
  });

  api.onPtyExit((n) => {
    if (n.sessionId !== sessionId) return;
    term.write(`\r\n\x1b[33m[process exited (${n.exitCode})]\x1b[0m\r\n`);
  });

  try {
    await api.ptySpawn({ sessionId, cols, rows });
  } catch (err) {
    term.write(`\r\n\x1b[31mfailed to spawn shell: ${(err as Error).message}\x1b[0m\r\n`);
    return;
  }

  term.onData((data) => {
    void api.ptyWrite({ sessionId, dataBase64: bytesToBase64(encoder.encode(data)) });
  });

  let resizeTimer: number | undefined;
  term.onResize(({ cols: c, rows: r }) => {
    if (resizeTimer !== undefined) {
      window.clearTimeout(resizeTimer);
    }
    resizeTimer = window.setTimeout(() => {
      void api.ptyResize({ sessionId, cols: c, rows: r });
    }, RESIZE_DEBOUNCE_MS);
  });

  term.focus();
}

main().catch((err: unknown) => {
  console.error(err);
});
