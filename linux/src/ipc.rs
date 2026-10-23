use anyhow::{Context, Result, bail};
use serde_json::{Value, json};
use std::io::{Read, Write};
use std::process::{Child, ChildStdin, Command, Stdio};
use zeroize::Zeroize;

pub struct Worker {
    child: Child,
    input: ChildStdin,
    sequence: u64,
}

impl Worker {
    pub fn start(
        directory: &std::path::Path,
        events: async_channel::Sender<Value>,
    ) -> Result<Self> {
        let executable = std::env::var("SENDA_WORKER")
            .unwrap_or_else(|_| "/app/lib/senda/worker/Senda.Worker".into());
        let mut child = Command::new(executable)
            .arg("--data-directory")
            .arg(directory)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()
            .context("Could not start the local transcription worker")?;
        let input = child.stdin.take().context("Missing worker input")?;
        let mut output = child.stdout.take().context("Missing worker output")?;
        std::thread::spawn(move || {
            let result = (|| -> Result<()> {
                loop {
                    let mut prefix = [0u8; 4];
                    output.read_exact(&mut prefix)?;
                    let length = u32::from_le_bytes(prefix) as usize;
                    if !(2..=65536).contains(&length) {
                        bail!("Invalid worker response size")
                    }
                    let mut bytes = vec![0; length];
                    output.read_exact(&mut bytes)?;
                    let message: Value = serde_json::from_slice(&bytes)?;
                    if message["version"] != 1 {
                        bail!("Unsupported worker protocol")
                    }
                    if events.send_blocking(message).is_err() {
                        return Ok(());
                    }
                }
            })();
            if result.is_err() {
                let _ = events.send_blocking(json!({"kind":"disconnected"}));
            }
        });
        Ok(Self {
            child,
            input,
            sequence: 0,
        })
    }

    pub fn send(
        &mut self,
        operation: &str,
        mut options: Value,
        mut audio: Vec<u8>,
    ) -> Result<String> {
        if audio.len() > 3_840_044 {
            audio.zeroize();
            bail!("Audio exceeds two minutes")
        }
        self.sequence += 1;
        let id = self.sequence.to_string();
        options["version"] = json!(1);
        options["id"] = json!(id);
        options["operation"] = json!(operation);
        options["audioBytes"] = json!(audio.len());
        let header = serde_json::to_vec(&options)?;
        if header.len() > 65536 {
            audio.zeroize();
            bail!("Worker request too large")
        }
        let result = (|| -> Result<()> {
            self.input.write_all(&(header.len() as u32).to_le_bytes())?;
            self.input.write_all(&header)?;
            self.input.write_all(&audio)?;
            self.input.flush()?;
            Ok(())
        })();
        audio.zeroize();
        result?;
        Ok(id)
    }
}

impl Drop for Worker {
    fn drop(&mut self) {
        let _ = self.send("shutdown", json!({}), vec![]);
        // Parent exit must not leave a model-owning process behind.
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}
