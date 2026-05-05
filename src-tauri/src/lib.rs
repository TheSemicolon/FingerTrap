mod sidecar;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .manage(sidecar::SidecarState::default())
        .setup(|app| {
            sidecar::spawn(app)?;
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            sidecar::sidecar_write,
            sidecar::subscribe_sidecar_output,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
