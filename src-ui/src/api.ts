import {
  createMessageConnection,
  type MessageConnection,
  RequestType1,
} from 'vscode-jsonrpc/browser';
import { TauriMessageReader, TauriMessageWriter } from './transport';

let connection: MessageConnection | undefined;

export async function start(): Promise<void> {
  if (connection) return;
  const reader = new TauriMessageReader();
  const writer = new TauriMessageWriter();
  await reader.start();
  connection = createMessageConnection(reader, writer);
  connection.listen();
}

function require_(): MessageConnection {
  if (!connection) {
    throw new Error('api.start() must be awaited before invoking RPC methods');
  }
  return connection;
}

const PingMethod = new RequestType1<string, string, void>('ping');

export async function ping(message: string): Promise<string> {
  return require_().sendRequest(PingMethod, message);
}
