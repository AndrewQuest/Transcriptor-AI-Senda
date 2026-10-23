use anyhow::{Context, Result};
use gst::prelude::*;
use gstreamer as gst;
use std::sync::{Arc, Mutex};
use zeroize::Zeroize;

const MAX_PCM: usize = 16000 * 2 * 120;
pub struct Capture {
    pipeline: gst::Pipeline,
    pcm: Arc<Mutex<Vec<u8>>>,
    _watch: gst::bus::BusWatchGuard,
}

pub fn devices() -> Vec<gst::Device> {
    let monitor = gst::DeviceMonitor::new();
    let _ = monitor.add_filter(Some("Audio/Source"), None);
    if monitor.start().is_err() {
        return vec![];
    }
    let devices = monitor.devices();
    monitor.stop();
    devices.into_iter().collect()
}

impl Capture {
    pub fn start(
        device: Option<&gst::Device>,
        events: async_channel::Sender<serde_json::Value>,
    ) -> Result<Self> {
        let pipeline = gst::Pipeline::new();
        let source = match device {
            Some(d) => d.create_element(None)?,
            None => gst::ElementFactory::make("pulsesrc").build()?,
        };
        let convert = gst::ElementFactory::make("audioconvert").build()?;
        let resample = gst::ElementFactory::make("audioresample").build()?;
        let pcm = Arc::new(Mutex::new(Vec::with_capacity(32000 * 5)));
        let data = pcm.clone();
        let level_events = events.clone();
        let caps = gst::Caps::builder("audio/x-raw")
            .field("format", "S16LE")
            .field("rate", 16000i32)
            .field("channels", 1i32)
            .build();
        let mut last_level = std::time::Instant::now();
        let mut limit_reported = false;
        let sink = gstreamer_app::AppSink::builder()
            .caps(&caps)
            .sync(false)
            .max_buffers(32)
            .callbacks(
                gstreamer_app::AppSinkCallbacks::builder()
                    .new_sample(move |sink| {
                        let sample = sink.pull_sample().map_err(|_| gst::FlowError::Eos)?;
                        let buffer = sample.buffer().ok_or(gst::FlowError::Error)?;
                        let mapped = buffer.map_readable().map_err(|_| gst::FlowError::Error)?;
                        let mut data = data.lock().map_err(|_| gst::FlowError::Error)?;
                        let available = MAX_PCM.saturating_sub(data.len());
                        data.extend_from_slice(&mapped.as_slice()[..mapped.size().min(available)]);
                        if data.len() >= MAX_PCM && !limit_reported {
                            // Retry on the next sample if level events filled the queue.
                            limit_reported = level_events
                                .try_send(serde_json::json!({"kind":"audio-limit"}))
                                .is_ok();
                        }
                        if last_level.elapsed().as_millis() >= 80 {
                            last_level = std::time::Instant::now();
                            let peak = mapped
                                .as_slice()
                                .chunks_exact(2)
                                .map(|b| (i16::from_le_bytes([b[0], b[1]]) as f64).abs() / 32768.)
                                .fold(0f64, f64::max);
                            let _ = level_events
                                .try_send(serde_json::json!({"kind":"level","data":peak}));
                        }
                        Ok(gst::FlowSuccess::Ok)
                    })
                    .build(),
            )
            .build();
        pipeline.add_many([&source, &convert, &resample, sink.upcast_ref()])?;
        gst::Element::link_many([&source, &convert, &resample, sink.upcast_ref()])?;
        let watch = pipeline
            .bus()
            .context("Missing audio bus")?
            .add_watch_local(move |_, message| {
                if let gst::MessageView::Error(_) = message.view() {
                    let _ = events.try_send(serde_json::json!({"kind":"audio-error"}));
                }
                gtk::glib::ControlFlow::Continue
            })?;
        pipeline.set_state(gst::State::Playing)?;
        Ok(Self {
            pipeline,
            pcm,
            _watch: watch,
        })
    }

    pub fn finish(self) -> Result<Vec<u8>> {
        self.pipeline.set_state(gst::State::Null)?;
        let mut pcm = self
            .pcm
            .lock()
            .map_err(|_| anyhow::anyhow!("Audio lock failed"))?;
        let mut wave = Vec::with_capacity(pcm.len() + 44);
        wave.extend_from_slice(b"RIFF");
        wave.extend_from_slice(&(pcm.len() as u32 + 36).to_le_bytes());
        wave.extend_from_slice(b"WAVEfmt ");
        wave.extend_from_slice(&16u32.to_le_bytes());
        wave.extend_from_slice(&1u16.to_le_bytes());
        wave.extend_from_slice(&1u16.to_le_bytes());
        wave.extend_from_slice(&16000u32.to_le_bytes());
        wave.extend_from_slice(&32000u32.to_le_bytes());
        wave.extend_from_slice(&2u16.to_le_bytes());
        wave.extend_from_slice(&16u16.to_le_bytes());
        wave.extend_from_slice(b"data");
        wave.extend_from_slice(&(pcm.len() as u32).to_le_bytes());
        wave.extend_from_slice(&pcm);
        pcm.zeroize();
        Ok(wave)
    }
}
impl Drop for Capture {
    fn drop(&mut self) {
        let _ = self.pipeline.set_state(gst::State::Null);
        if let Ok(mut data) = self.pcm.lock() {
            data.zeroize();
        }
    }
}
