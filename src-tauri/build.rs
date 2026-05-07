use std::fs;
use std::path::Path;

fn main() {
    // The .NET sidecar dlopens libporta_pty.dylib via @loader_path (the
    // sidecar's own directory). Tauri's plugin-shell copies the sidecar
    // executable from `src-tauri/binaries/fingertrap-sidecar-<triple>` to
    // `target/<profile>/fingertrap-sidecar` at build time, but knows
    // nothing about our companion native library. Copy it alongside the
    // sidecar so DYLD finds it. See ADR-0008.
    if let Err(err) = stage_pty_shim() {
        // Don't fail the build if the dylib isn't there yet; the .NET
        // sidecar needs to be published once before this works. The
        // resulting "no such file" runtime error is informative.
        println!("cargo:warning=stage_pty_shim skipped: {err}");
    }

    tauri_build::build()
}

fn stage_pty_shim() -> std::io::Result<()> {
    let manifest_dir =
        std::env::var("CARGO_MANIFEST_DIR").map_err(|e| std::io::Error::other(e.to_string()))?;
    let profile = std::env::var("PROFILE").map_err(|e| std::io::Error::other(e.to_string()))?;

    let candidates: &[&str] = if cfg!(target_os = "macos") {
        &["libporta_pty.dylib"]
    } else if cfg!(target_os = "linux") {
        &["libporta_pty.so"]
    } else {
        &[]
    };

    let src_dir = Path::new(&manifest_dir).join("binaries");
    let dst_dir = Path::new(&manifest_dir).join("target").join(&profile);

    for name in candidates {
        let src = src_dir.join(name);
        if !src.exists() {
            return Err(std::io::Error::new(
                std::io::ErrorKind::NotFound,
                format!("{} not found in {}", name, src_dir.display()),
            ));
        }
        fs::create_dir_all(&dst_dir)?;
        let dst = dst_dir.join(name);
        fs::copy(&src, &dst)?;
        println!("cargo:rerun-if-changed={}", src.display());
    }

    Ok(())
}
