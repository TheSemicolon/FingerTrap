import './styles.css';
import { Terminal } from '@xterm/xterm';
import * as api from './api';

async function main(): Promise<void> {
  const terminalEl = document.getElementById('terminal');
  const pingButton = document.getElementById('ping-button');
  if (!terminalEl || !pingButton) {
    throw new Error('expected #terminal and #ping-button in DOM');
  }

  const term = new Terminal({
    rows: 18,
    cols: 100,
    fontFamily: 'ui-monospace, SFMono-Regular, Menlo, monospace',
    fontSize: 13,
    theme: { background: '#000000' },
  });
  term.open(terminalEl);
  term.writeln('FingerTrap M0 — click "Ping sidecar" to round-trip a JSON-RPC call.');
  term.writeln('');

  await api.start();

  pingButton.addEventListener('click', async () => {
    try {
      const reply = await api.ping('hello from m0');
      term.writeln(`\x1b[32m${reply}\x1b[0m`);
    } catch (err) {
      term.writeln(`\x1b[31merror: ${(err as Error).message}\x1b[0m`);
    }
  });
}

main().catch((err: unknown) => {
  console.error(err);
});
