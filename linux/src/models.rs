use anyhow::{Result, bail};
use std::io::Read;
use std::path::Path;

pub const CATALOG: [(&str, &str); 5] = [
    ("ggml-tiny-q5_1.bin", "Whisper Tiny · OpenAI · 31 MiB"),
    ("ggml-base-q5_1.bin", "Whisper Base · OpenAI · 57 MiB"),
    ("ggml-small-q5_1.bin", "Whisper Small · OpenAI · 181 MiB"),
    ("ggml-medium-q5_0.bin", "Whisper Medium · OpenAI · 514 MiB"),
    (
        "ggml-large-v3-turbo-q5_0.bin",
        "Whisper Large v3 Turbo · OpenAI · 548 MiB",
    ),
];
pub fn known(file: &str) -> bool {
    CATALOG.iter().any(|(name, _)| *name == file)
}
pub fn label(file: &str) -> &str {
    CATALOG
        .iter()
        .find(|(name, _)| *name == file)
        .map(|(_, label)| *label)
        .unwrap_or(file)
}
pub fn available(directory: &Path, file: &str) -> bool {
    if !known(file)
        || directory.ancestors().any(|p| {
            p.symlink_metadata()
                .map(|m| m.file_type().is_symlink())
                .unwrap_or(true)
        })
    {
        return false;
    }
    let path = directory.join(file);
    let Ok(metadata) = path.symlink_metadata() else {
        return false;
    };
    if !metadata.is_file() || metadata.len() < 1_000_000 {
        return false;
    }
    let mut magic = [0; 4];
    std::fs::File::open(path)
        .and_then(|mut f| f.read_exact(&mut magic))
        .is_ok()
        && magic == *b"lmgg"
}
pub fn installed(directory: &Path, current: &str) -> Vec<String> {
    let mut items: Vec<String> = CATALOG
        .iter()
        .filter(|(file, _)| available(directory, file))
        .map(|(file, _)| file.to_string())
        .collect();
    if !items.iter().any(|file| file == current) {
        items.push(current.into());
    }
    items
}
pub fn delete(directory: &Path, file: &str, current: &str) -> Result<()> {
    if file == current {
        bail!("Elige otro modelo predeterminado antes de borrar este.");
    }
    if !available(directory, file) {
        bail!("El modelo ya no está disponible.");
    }
    std::fs::remove_file(directory.join(file))?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    #[test]
    fn inventory_preserves_default_and_rejects_partial_or_linked_models() {
        let directory =
            std::env::temp_dir().join(format!("senda-models-{}", rand::random::<u64>()));
        std::fs::create_dir(&directory).unwrap();
        let small = CATALOG[2].0;
        let turbo = CATALOG[4].0;
        let mut file = std::fs::File::create(directory.join(small)).unwrap();
        file.write_all(b"lmgg").unwrap();
        file.set_len(1_000_000).unwrap();
        std::fs::write(directory.join(turbo), b"lmgg").unwrap();
        assert_eq!(installed(&directory, turbo), vec![small, turbo]);
        assert!(!available(&directory, turbo));
        assert!(delete(&directory, small, small).is_err());
        assert!(delete(&directory, "../outside.bin", turbo).is_err());
        std::os::unix::fs::symlink(directory.join(small), directory.join(CATALOG[0].0)).unwrap();
        assert!(!available(&directory, CATALOG[0].0));
        delete(&directory, small, turbo).unwrap();
        assert!(!available(&directory, small));
        assert!(known(small));
        std::fs::remove_dir_all(directory).unwrap();
    }
}
