use aes_gcm::{Aes256Gcm, KeyInit, Nonce, aead::Aead};
use anyhow::{Context, Result, bail};
use rand::RngCore;
use serde::{Deserialize, Serialize};
use std::io::Write;
use std::os::unix::fs::OpenOptionsExt;
use std::path::{Path, PathBuf};

#[derive(Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct Settings {
    pub interface_language: String,
    pub language: String,
    pub model: String,
    pub download_model: String,
    pub microphone: String,
    pub cpu_threads: usize,
    pub unload_minutes: usize,
    pub press_enter_after_paste: bool,
    pub save_history: bool,
    pub launch_at_login: bool,
    pub enable_shortcut: bool,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            interface_language: "es".into(),
            language: "auto".into(),
            model: "ggml-base-q5_1.bin".into(),
            download_model: "".into(),
            microphone: "".into(),
            cpu_threads: std::thread::available_parallelism()
                .map(|n| (n.get() / 2).clamp(1, 4))
                .unwrap_or(2),
            unload_minutes: 5,
            press_enter_after_paste: false,
            save_history: true,
            launch_at_login: false,
            enable_shortcut: false,
        }
    }
}
#[derive(Clone, Serialize, Deserialize)]
pub struct Entry {
    pub created: u64,
    pub text: String,
    pub model: String,
    pub engine: String,
    pub audio_seconds: f64,
    pub processing_seconds: f64,
}
pub fn now() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}
pub fn data_dir() -> PathBuf {
    gtk::glib::user_data_dir().join("senda")
}
pub fn settings_path() -> PathBuf {
    gtk::glib::user_config_dir().join("senda/settings.json")
}
pub fn load_settings() -> Result<Settings> {
    let path = settings_path();
    if !path.exists() {
        return Ok(Settings::default());
    }
    Ok(serde_json::from_slice(&std::fs::read(path)?)
        .context("Invalid settings; the original file was preserved")?)
}
pub fn atomic_write(path: &Path, bytes: &[u8]) -> Result<()> {
    std::fs::create_dir_all(path.parent().context("Missing data directory")?)?;
    let temporary = path.with_extension("tmp");
    let mut file = std::fs::OpenOptions::new()
        .write(true)
        .create(true)
        .truncate(true)
        .mode(0o600)
        .open(&temporary)?;
    file.write_all(bytes)?;
    file.sync_all()?;
    drop(file);
    std::fs::rename(&temporary, path)?;
    Ok(())
}
pub fn save_settings(settings: &Settings) -> Result<()> {
    atomic_write(&settings_path(), &serde_json::to_vec_pretty(settings)?)
}
pub fn history_path() -> PathBuf {
    data_dir().join("history.enc")
}
pub fn read_history(key: &[u8; 32]) -> Result<Vec<Entry>> {
    let path = history_path();
    if !path.exists() {
        return Ok(vec![]);
    }
    let bytes = std::fs::read(path)?;
    if bytes.len() < 29 || bytes[0] != 1 {
        bail!("Invalid encrypted history; file preserved")
    }
    let cipher =
        Aes256Gcm::new_from_slice(key).map_err(|_| anyhow::anyhow!("Invalid history key"))?;
    let plain = cipher
        .decrypt(Nonce::from_slice(&bytes[1..13]), &bytes[13..])
        .map_err(|_| anyhow::anyhow!("History could not be decrypted; file preserved"))?;
    let mut records: Vec<Entry> = serde_json::from_slice(&plain)?;
    records.retain(|e| e.created >= now().saturating_sub(30 * 86400));
    records.truncate(500);
    Ok(records)
}
pub fn write_history(key: &[u8; 32], entries: &[Entry]) -> Result<()> {
    let cipher =
        Aes256Gcm::new_from_slice(key).map_err(|_| anyhow::anyhow!("Invalid history key"))?;
    let mut nonce = [0u8; 12];
    rand::rngs::OsRng.fill_bytes(&mut nonce);
    let keep: Vec<&Entry> = entries
        .iter()
        .filter(|e| e.created >= now().saturating_sub(30 * 86400))
        .take(500)
        .collect();
    let encrypted = cipher
        .encrypt(
            Nonce::from_slice(&nonce),
            serde_json::to_vec(&keep)?.as_slice(),
        )
        .map_err(|_| anyhow::anyhow!("History encryption failed"))?;
    let mut bytes = vec![1];
    bytes.extend_from_slice(&nonce);
    bytes.extend_from_slice(&encrypted);
    atomic_write(&history_path(), &bytes)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn legacy_settings_do_not_enable_enter() {
        let settings: Settings = serde_json::from_str("{\"language\":\"en\"}").unwrap();
        assert!(!settings.press_enter_after_paste);
        assert_eq!(settings.language, "en");
    }
    #[test]
    fn legacy_model_survives_settings_roundtrip_without_enabling_enter() {
        for model in [
            "ggml-base-q5_1.bin",
            "ggml-small-q5_1.bin",
            "ggml-large-v3-turbo-q5_0.bin",
        ] {
            let legacy = format!(
                r#"{{"model":"{model}","language":"es","microphone":"Existing microphone"}}"#
            );
            let mut settings: Settings = serde_json::from_str(&legacy).unwrap();
            assert_eq!(settings.model, model);
            assert!(settings.download_model.is_empty());
            settings.download_model = "ggml-tiny-q5_1.bin".into();
            for _ in 0..3 {
                settings = serde_json::from_slice(&serde_json::to_vec(&settings).unwrap()).unwrap();
                assert_eq!(settings.model, model);
                assert_eq!(settings.language, "es");
                assert_eq!(settings.microphone, "Existing microphone");
                assert!(!settings.press_enter_after_paste);
            }
        }
    }
    #[test]
    fn history_authentication_rejects_modified_content() {
        let cipher = Aes256Gcm::new_from_slice(&[7; 32]).unwrap();
        let nonce = Nonce::from_slice(&[0; 12]);
        let mut encrypted = cipher
            .encrypt(nonce, b"Private transcript".as_ref())
            .unwrap();
        assert!(!encrypted.windows(7).any(|w| w == b"Private"));
        encrypted[0] ^= 1;
        assert!(cipher.decrypt(nonce, encrypted.as_slice()).is_err());
    }
}
