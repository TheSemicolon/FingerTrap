use std::sync::Mutex;

use tauri::{ipc::Channel, Manager, State};
use tauri_plugin_shell::process::{CommandChild, CommandEvent};
use tauri_plugin_shell::ShellExt;

#[derive(Default)]
pub struct SidecarState {
    child: Mutex<Option<CommandChild>>,
    output_channel: Mutex<Option<Channel<Vec<u8>>>>,
}

pub fn spawn(app: &mut tauri::App) -> Result<(), Box<dyn std::error::Error>> {
    let app_handle = app.handle().clone();
    // set_raw_out: deliver stdout as raw bytes. The default reader splits on
    // \r or \n, which fragments LSP-style JSON-RPC framing
    // (`Content-Length: N\r\n\r\n{...}`) and pty/output payloads that
    // contain CR/LF inside the JSON body.
    let (mut rx, child) = app
        .shell()
        .sidecar("fingertrap-sidecar")?
        .set_raw_out(true)
        .spawn()?;

    let state: State<SidecarState> = app_handle.state();
    *state.child.lock().unwrap() = Some(child);

    tauri::async_runtime::spawn(async move {
        while let Some(event) = rx.recv().await {
            match event {
                CommandEvent::Stdout(bytes) => {
                    let state: State<SidecarState> = app_handle.state();
                    let guard = state.output_channel.lock().unwrap();
                    if let Some(channel) = guard.as_ref() {
                        let _ = channel.send(bytes);
                    }
                }
                CommandEvent::Stderr(bytes) => {
                    eprintln!("sidecar stderr: {}", String::from_utf8_lossy(&bytes));
                }
                CommandEvent::Terminated(payload) => {
                    eprintln!("sidecar terminated: {:?}", payload);
                    break;
                }
                CommandEvent::Error(message) => {
                    eprintln!("sidecar error: {message}");
                }
                _ => {}
            }
        }
    });

    Ok(())
}

#[tauri::command]
pub fn sidecar_write(state: State<'_, SidecarState>, payload: Vec<u8>) -> Result<(), String> {
    let mut guard = state.child.lock().unwrap();
    match guard.as_mut() {
        Some(child) => child.write(&payload).map_err(|e| e.to_string()),
        None => Err("sidecar is not running".into()),
    }
}

#[tauri::command]
pub fn subscribe_sidecar_output(
    state: State<'_, SidecarState>,
    channel: Channel<Vec<u8>>,
) -> Result<(), String> {
    *state.output_channel.lock().unwrap() = Some(channel);
    Ok(())
}
