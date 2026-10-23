mod audio;
mod i18n;
mod ipc;
mod portals;
mod storage;

use adw::prelude::*;
use anyhow::{Context, Result};
use gstreamer::prelude::DeviceExt;
use gtk::{gio, glib};
use serde_json::{Value, json};
use std::{cell::RefCell, rc::Rc};

struct State {
    settings: storage::Settings,
    worker: Option<ipc::Worker>,
    capture: Option<audio::Capture>,
    active: Option<String>,
    cancelled: bool,
    generation: u64,
    auto_enter: bool,
    modifiers_verified: bool,
    target: Option<portals::FocusId>,
    focus: Option<portals::FocusTracker>,
    portal: Option<portals::Portal>,
    remote: Option<portals::RemoteKeyboard>,
    shortcut_session: Option<String>,
    shortcut_connecting: bool,
    history_key: Option<zeroize::Zeroizing<[u8; 32]>>,
    history: Vec<storage::Entry>,
    history_healthy: bool,
    _hold: gio::ApplicationHoldGuard,
}
#[derive(Clone)]
struct Ui {
    app: adw::Application,
    window: adw::ApplicationWindow,
    pages: adw::ViewStack,
    status: gtk::Label,
    last: gtk::TextView,
    level: gtk::ProgressBar,
    record: gtk::Button,
    cancel: gtk::Button,
    copy: gtk::Button,
    correct: gtk::Button,
    quit: gtk::Button,
    interface: adw::ComboRow,
    language: adw::ComboRow,
    model: adw::ComboRow,
    microphone: adw::ComboRow,
    threads: adw::SpinRow,
    unload: adw::SpinRow,
    enter: adw::SwitchRow,
    history: adw::SwitchRow,
    login: adw::SwitchRow,
    save: gtk::Button,
    download: gtk::Button,
    shortcut: gtk::Button,
    keyboard: gtk::Button,
    clear: gtk::Button,
    history_text: gtk::TextView,
    privacy: gtk::Label,
    capabilities: gtk::Label,
    enter_limit: gtk::Label,
    shortcut_help: gtk::Label,
}
impl Ui {
    fn english(state: &State) -> bool {
        state.settings.interface_language == "en"
    }
    fn status(&self, state: &State, source: &str) {
        self.status
            .set_text(&i18n::text(Self::english(state), source));
    }
    fn refresh(&self, state: &State) {
        let tr = |s| i18n::text(Self::english(state), s);
        self.window
            .set_title(Some(&format!("Senda — {}", tr("Dictado local para Linux"))));
        for (button, key) in [
            (&self.record, "Grabar / terminar"),
            (&self.cancel, "Cancelar"),
            (&self.copy, "Copiar"),
            (&self.correct, "Copiar corrección"),
            (&self.quit, "Salir"),
            (&self.save, "Guardar ajustes"),
            (&self.download, "Descargar modelo"),
            (&self.shortcut, "Activar / configurar atajo"),
            (&self.keyboard, "Autorizar teclado"),
            (&self.clear, "Borrar historial"),
        ] {
            button.set_label(&tr(key));
        }
        for (row, key) in [
            (&self.interface, "Idioma de la interfaz"),
            (&self.language, "Idioma"),
            (&self.model, "Modelo"),
            (&self.microphone, "Micrófono"),
        ] {
            row.set_title(&tr(key));
        }
        self.threads.set_title(&tr("Hilos de CPU"));
        if let Some(model) = self
            .microphone
            .model()
            .and_then(|model| model.downcast::<gtk::StringList>().ok())
        {
            let label = tr("Micrófono predeterminado");
            if model.string(0).as_deref() != Some(label.as_str()) {
                let selected = self.microphone.selected();
                model.splice(0, 1, &[label.as_str()]);
                self.microphone.set_selected(selected);
            }
        }
        self.unload
            .set_title(&tr("Liberar modelo tras inactividad"));
        self.unload.set_subtitle(&tr("Minutos sin dictar"));
        self.enter
            .set_title(&tr("Pulsar Enter automáticamente después de pegar"));
        self.enter.set_subtitle(&tr("Al activar esta opción, Senda pulsará Enter después de pegar cada dictado. Esto puede enviar mensajes, confirmar acciones o ejecutar comandos según la aplicación."));
        self.history.set_title(&tr("Guardar historial cifrado"));
        self.login.set_title(&tr("Iniciar al entrar en Linux"));
        self.privacy.set_text(&tr(
            "El audio solo permanece en RAM. Hasta dos minutos por dictado.",
        ));
        self.capabilities.set_text(&tr("La posición de la cápsula depende del escritorio. Esta versión usa notificaciones sin quitar el foco."));
        self.enter_limit.set_text(&tr("Enter se omite si el escritorio no permite verificar el foco y los modificadores. Puedes copiar el texto siempre."));
        self.shortcut_help.set_text(&tr(
            "Conecta el atajo desde Ajustes y elige la combinación en el diálogo del escritorio.",
        ));
        self.pages
            .page(&self.pages.child_by_name("home").unwrap())
            .set_title(Some(&tr("Inicio")));
        self.pages
            .page(&self.pages.child_by_name("settings").unwrap())
            .set_title(Some(&tr("Ajustes")));
        self.pages
            .page(&self.pages.child_by_name("history").unwrap())
            .set_title(Some(&tr("Historial")));
        self.history_text
            .buffer()
            .set_text(&if state.history.is_empty() {
                tr("Sin historial guardado.")
            } else {
                state
                    .history
                    .iter()
                    .map(|e| {
                        let created = glib::DateTime::from_unix_local(e.created as i64)
                            .and_then(|date| date.format("%x %X"))
                            .map(|date| date.to_string())
                            .unwrap_or_default();
                        format!(
                            "{} · {} · {} · {:.2}s\n{}\n\n",
                            created, e.model, e.engine, e.processing_seconds, e.text
                        )
                    })
                    .collect::<String>()
            });
        let busy = state.capture.is_some() || state.active.is_some();
        for widget in [
            self.save.upcast_ref::<gtk::Widget>(),
            self.download.upcast_ref(),
            self.shortcut.upcast_ref(),
            self.keyboard.upcast_ref(),
        ] {
            widget.set_sensitive(!busy);
        }
        self.cancel.set_sensitive(busy);
        self.level.set_fraction(if state.capture.is_some() {
            self.level.fraction()
        } else {
            0.
        });
    }
    fn notify(&self, state: &State, source: &str) {
        let notice = gio::Notification::new("Senda");
        notice.set_body(Some(&i18n::text(Self::english(state), source)));
        self.app.send_notification(Some("dictation"), &notice);
    }
}
fn combo(values: &[&str]) -> adw::ComboRow {
    adw::ComboRow::builder()
        .model(&gtk::StringList::new(values))
        .build()
}
fn note() -> gtk::Label {
    gtk::Label::builder()
        .wrap(true)
        .xalign(0.)
        .css_classes(["dim-label"])
        .build()
}
fn panel() -> gtk::Box {
    gtk::Box::builder()
        .orientation(gtk::Orientation::Vertical)
        .spacing(16)
        .margin_top(24)
        .margin_bottom(24)
        .margin_start(24)
        .margin_end(24)
        .build()
}
fn scroll(child: &impl IsA<gtk::Widget>) -> gtk::ScrolledWindow {
    gtk::ScrolledWindow::builder()
        .child(child)
        .hscrollbar_policy(gtk::PolicyType::Never)
        .vexpand(true)
        .build()
}
fn editor() -> gtk::TextView {
    gtk::TextView::builder()
        .editable(false)
        .cursor_visible(false)
        .wrap_mode(gtk::WrapMode::WordChar)
        .top_margin(14)
        .bottom_margin(14)
        .left_margin(14)
        .right_margin(14)
        .vexpand(true)
        .build()
}

fn configure(state: &mut State) -> Result<()> {
    let s = &state.settings;
    let data = json!({"model":s.model,"settings":{"language":s.language,"interfaceLanguage":s.interface_language,"cpuThreads":s.cpu_threads,"unloadAfterMinutes":s.unload_minutes,"acceleration":"cpu"}});
    state
        .worker
        .as_mut()
        .context("Worker unavailable")?
        .send("configure", data, vec![])?;
    Ok(())
}
fn cancel(state: &Rc<RefCell<State>>, ui: &Ui) {
    let mut s = state.borrow_mut();
    s.generation += 1;
    s.cancelled = true;
    s.capture.take();
    if let Some(worker) = s.worker.as_mut() {
        let _ = worker.send("cancel", json!({}), vec![]);
    }
    ui.status(
        &s,
        if s.active.is_some() {
            "Cancelando…"
        } else {
            "Dictado cancelado"
        },
    );
    ui.refresh(&s);
    ui.app.withdraw_notification("dictation");
}
async fn begin(
    state: Rc<RefCell<State>>,
    ui: Ui,
    events: async_channel::Sender<Value>,
    external: bool,
    modifiers_verified: bool,
) -> Result<()> {
    if state.borrow().active.is_some() {
        cancel(&state, &ui);
        return Ok(());
    }
    if state.borrow().capture.is_some() {
        return Ok(());
    }
    let focus = state.borrow().focus.clone();
    let target = if external {
        focus.as_ref().and_then(|f| f.current.borrow().clone())
    } else {
        None
    };
    if let (Some(focus), Some(target)) = (&focus, &target) {
        if let Err(error) = focus.validate(target).await {
            ui.status(
                &state.borrow(),
                "No se puede comprobar el destino. El resultado quedará disponible para copiar.",
            );
            let _ = error;
        }
    }
    let devices = audio::devices();
    let microphone = state.borrow().settings.microphone.clone();
    let device = devices
        .iter()
        .find(|d| d.display_name().as_str() == microphone);
    let capture = audio::Capture::start(device, events.clone())?;
    let mut s = state.borrow_mut();
    if s.worker.is_none() {
        s.worker = Some(ipc::Worker::start(&storage::data_dir(), events)?);
    }
    configure(&mut s)?;
    s.generation += 1;
    s.cancelled = false;
    s.target = target;
    s.auto_enter = s.settings.press_enter_after_paste;
    s.modifiers_verified = modifiers_verified;
    s.capture = Some(capture);
    ui.status(
        &s,
        if s.auto_enter {
            "Al terminar: pegar + Enter"
        } else {
            "Te escucho…"
        },
    );
    ui.notify(
        &s,
        if s.auto_enter {
            "Al terminar: pegar + Enter"
        } else {
            "Te escucho…"
        },
    );
    ui.refresh(&s);
    Ok(())
}
fn finish(state: &Rc<RefCell<State>>, ui: &Ui) -> Result<()> {
    let mut s = state.borrow_mut();
    let Some(capture) = s.capture.take() else {
        return Ok(());
    };
    let audio = capture.finish()?;
    let id =
        s.worker
            .as_mut()
            .context("Worker unavailable")?
            .send("transcribe", json!({}), audio)?;
    s.active = Some(id);
    ui.status(&s, "Transcribiendo…");
    ui.notify(
        &s,
        "Pulsa otra vez el atajo para cancelar el procesamiento. Esc cancela dentro de Senda.",
    );
    ui.refresh(&s);
    Ok(())
}
async fn accept(state: Rc<RefCell<State>>, ui: Ui, data: Value) -> Result<()> {
    let text = data["text"].as_str().unwrap_or_default().to_string();
    ui.last.buffer().set_text(&text);
    let (generation, target, focus, auto_enter, modifiers, remote) = {
        let s = state.borrow();
        (
            s.generation,
            s.target.clone(),
            s.focus.clone(),
            s.auto_enter,
            s.modifiers_verified,
            s.remote.clone(),
        )
    };
    let mut status = "Texto listo para copiar.";
    if let (Some(target), Some(focus)) = (&target, &focus) {
        if focus
            .insert(target, &text, || {
                let s = state.borrow();
                s.generation == generation && !s.cancelled
            })
            .await
            .is_ok()
        {
            status = "Entrega de texto aceptada por el campo de destino.";
            if auto_enter {
                glib::timeout_future(std::time::Duration::from_millis(350)).await;
                let live = {
                    let s = state.borrow();
                    s.generation == generation && !s.cancelled
                };
                if live && focus.validate(target).await.is_ok() {
                    status = "Enter omitido: no se pueden verificar los modificadores.";
                    let still_live = {
                        let s = state.borrow();
                        s.generation == generation && !s.cancelled
                    };
                    if modifiers && still_live {
                        if let Some(remote) = remote {
                            if remote.enter().await.is_ok() {
                                status = "Enter despachado a la aplicación.";
                            }
                        }
                    }
                }
            }
        }
    }
    let mut s = state.borrow_mut();
    if s.generation != generation || s.cancelled {
        s.active = None;
        ui.refresh(&s);
        return Ok(());
    }
    ui.status(&s, status);
    s.active = None;
    let entry = storage::Entry {
        created: storage::now(),
        text,
        model: data["model"].as_str().unwrap_or("").into(),
        engine: data["engine"].as_str().unwrap_or("").into(),
        audio_seconds: data["audioSeconds"].as_f64().unwrap_or(0.),
        processing_seconds: data["processingSeconds"].as_f64().unwrap_or(0.),
    };
    if s.settings.save_history && s.history_healthy {
        if s.history_key.is_some() {
            s.history.insert(0, entry);
            s.history.truncate(500);
            if storage::write_history(s.history_key.as_ref().unwrap(), &s.history).is_err() {
                ui.status(&s, "No se pudo guardar el historial cifrado.");
            }
        }
    }
    ui.refresh(&s);
    ui.app.withdraw_notification("dictation");
    Ok(())
}

fn build(app: &adw::Application, background: bool) -> Result<()> {
    let settings = storage::load_settings()?;
    let (events, receiver) = async_channel::bounded::<Value>(64);
    let worker = ipc::Worker::start(&storage::data_dir(), events.clone())?;
    let state = Rc::new(RefCell::new(State {
        settings: settings.clone(),
        worker: Some(worker),
        capture: None,
        active: None,
        cancelled: false,
        generation: 0,
        auto_enter: false,
        modifiers_verified: false,
        target: None,
        focus: None,
        portal: None,
        remote: None,
        shortcut_session: None,
        shortcut_connecting: false,
        history_key: None,
        history: vec![],
        history_healthy: false,
        _hold: app.hold(),
    }));
    let window = adw::ApplicationWindow::builder()
        .application(app)
        .default_width(860)
        .default_height(740)
        .build();
    let toolbar = adw::ToolbarView::new();
    let header = adw::HeaderBar::new();
    let pages = adw::ViewStack::new();
    let switcher = adw::ViewSwitcher::builder()
        .stack(&pages)
        .policy(adw::ViewSwitcherPolicy::Wide)
        .build();
    header.set_title_widget(Some(&switcher));
    let quit = gtk::Button::new();
    header.pack_end(&quit);
    toolbar.add_top_bar(&header);
    toolbar.set_content(Some(&pages));
    window.set_content(Some(&toolbar));
    let home = panel();
    let title = gtk::Label::builder()
        .label("senda")
        .xalign(0.)
        .css_classes(["title-1"])
        .build();
    home.append(&title);
    let status = note();
    let privacy = note();
    let capabilities = note();
    let level = gtk::ProgressBar::new();
    let last = editor();
    home.append(&status);
    home.append(&privacy);
    home.append(&level);
    home.append(&scroll(&last));
    let buttons = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    let record = gtk::Button::builder()
        .css_classes(["suggested-action"])
        .build();
    let cancel_button = gtk::Button::new();
    let copy = gtk::Button::new();
    let correct = gtk::Button::new();
    for b in [&record, &cancel_button, &copy, &correct] {
        buttons.append(b);
    }
    home.append(&buttons);
    home.append(&capabilities);
    pages
        .add_titled(&home, Some("home"), "Inicio")
        .set_icon_name(Some("audio-input-microphone-symbolic"));
    let preferences = adw::PreferencesPage::new();
    let group = adw::PreferencesGroup::new();
    let interface = combo(&["Español", "English"]);
    interface.set_selected(u32::from(settings.interface_language == "en"));
    let language = combo(&["Auto", "Español", "English"]);
    language.set_selected(match settings.language.as_str() {
        "es" => 1,
        "en" => 2,
        _ => 0,
    });
    let model = combo(&["Whisper Base · q5_1", "Whisper Small · q5_1"]);
    model.set_selected(u32::from(settings.model.contains("small")));
    let devices = audio::devices();
    let mut names = vec![i18n::text(
        settings.interface_language == "en",
        "Micrófono predeterminado",
    )];
    names.extend(devices.iter().map(|d| d.display_name().to_string()));
    let microphone = combo(&names.iter().map(String::as_str).collect::<Vec<_>>());
    microphone.set_selected(
        names
            .iter()
            .position(|s| s == &settings.microphone)
            .unwrap_or(0) as u32,
    );
    let threads = adw::SpinRow::with_range(
        1.,
        std::thread::available_parallelism()
            .map(|n| n.get() as f64)
            .unwrap_or(4.),
        1.,
    );
    threads.set_value(settings.cpu_threads as f64);
    let unload = adw::SpinRow::with_range(1., 60., 1.);
    unload.set_value(settings.unload_minutes as f64);
    let enter = adw::SwitchRow::builder()
        .active(settings.press_enter_after_paste)
        .build();
    enter.set_title_lines(0);
    enter.set_subtitle_lines(0);
    let history = adw::SwitchRow::builder()
        .active(settings.save_history)
        .build();
    let login = adw::SwitchRow::builder()
        .active(settings.launch_at_login)
        .build();
    for row in [
        interface.upcast_ref::<gtk::Widget>(),
        language.upcast_ref(),
        model.upcast_ref(),
        microphone.upcast_ref(),
        threads.upcast_ref(),
        unload.upcast_ref(),
        enter.upcast_ref(),
        history.upcast_ref(),
        login.upcast_ref(),
    ] {
        group.add(row);
    }
    preferences.add(&group);
    let actions = adw::PreferencesGroup::new();
    let action_box = panel();
    let save = gtk::Button::builder()
        .css_classes(["suggested-action"])
        .build();
    let download = gtk::Button::new();
    let shortcut = gtk::Button::new();
    let keyboard = gtk::Button::new();
    let enter_limit = note();
    let shortcut_help = note();
    for child in [
        save.upcast_ref::<gtk::Widget>(),
        download.upcast_ref(),
        shortcut.upcast_ref(),
        keyboard.upcast_ref(),
        enter_limit.upcast_ref(),
        shortcut_help.upcast_ref(),
    ] {
        action_box.append(child);
    }
    actions.add(&action_box);
    preferences.add(&actions);
    pages
        .add_titled(&preferences, Some("settings"), "Ajustes")
        .set_icon_name(Some("emblem-system-symbolic"));
    let history_page = panel();
    let history_text = editor();
    let clear = gtk::Button::builder()
        .css_classes(["destructive-action"])
        .build();
    history_page.append(&scroll(&history_text));
    history_page.append(&clear);
    pages
        .add_titled(&history_page, Some("history"), "Historial")
        .set_icon_name(Some("document-open-recent-symbolic"));
    let ui = Ui {
        app: app.clone(),
        window: window.clone(),
        pages,
        status,
        last,
        level,
        record,
        cancel: cancel_button,
        copy,
        correct,
        quit,
        interface,
        language,
        model,
        microphone,
        threads,
        unload,
        enter,
        history,
        login,
        save,
        download,
        shortcut,
        keyboard,
        clear,
        history_text,
        privacy,
        capabilities,
        enter_limit,
        shortcut_help,
    };
    ui.refresh(&state.borrow());
    ui.status(&state.borrow(), "Motor local preparado.");
    let s = state.clone();
    let u = ui.clone();
    let e = events.clone();
    ui.record.connect_clicked(move |_| {
        let s = s.clone();
        let u = u.clone();
        let e = e.clone();
        glib::MainContext::default().spawn_local(async move {
            let recording = s.borrow().capture.is_some();
            let result = if recording {
                finish(&s, &u)
            } else {
                begin(s.clone(), u.clone(), e, false, false).await
            };
            if let Err(error) = result {
                u.status(&s.borrow(), "No se pudo abrir el micrófono.");
                eprintln!("Audio setup: {error}");
            }
        });
    });
    let s = state.clone();
    let u = ui.clone();
    ui.cancel.connect_clicked(move |_| cancel(&s, &u));
    let u = ui.clone();
    ui.copy.connect_clicked(move |_| {
        let buffer = u.last.buffer();
        let text = buffer.text(&buffer.start_iter(), &buffer.end_iter(), false);
        u.window.clipboard().set_text(&text);
    });
    let u = ui.clone();
    let s = state.clone();
    ui.correct.connect_clicked(move |_| {
        let english = Ui::english(&s.borrow());
        let dialog = adw::Dialog::builder()
            .title(i18n::text(english, "Texto corregido"))
            .content_width(600)
            .content_height(400)
            .build();
        let content = panel();
        let edit = editor();
        edit.set_editable(true);
        edit.set_cursor_visible(true);
        let buffer = u.last.buffer();
        edit.buffer()
            .set_text(&buffer.text(&buffer.start_iter(), &buffer.end_iter(), false));
        content.append(&scroll(&edit));
        let help = note();
        help.set_text(&i18n::text(
            english,
            "La corrección se copia para pegarla manualmente; no reemplaza mensajes ya enviados.",
        ));
        content.append(&help);
        let copy = gtk::Button::with_label(&i18n::text(english, "Copiar corrección"));
        content.append(&copy);
        dialog.set_child(Some(&content));
        let d = dialog.clone();
        copy.connect_clicked(move |_| {
            let buffer = edit.buffer();
            edit.clipboard().set_text(&buffer.text(
                &buffer.start_iter(),
                &buffer.end_iter(),
                false,
            ));
            d.close();
        });
        dialog.present(Some(&u.window));
    });
    let s = state.clone();
    let u = ui.clone();
    ui.clear.connect_clicked(move |_| {
        let mut state = s.borrow_mut();
        if std::fs::remove_file(storage::history_path()).is_ok()
            || !storage::history_path().exists()
        {
            state.history.clear();
            state.history_healthy = true;
            u.refresh(&state);
            u.status(&state, "Historial borrado.");
        }
    });
    let a = app.clone();
    ui.quit.connect_clicked(move |_| a.quit());
    let s = state.clone();
    app.connect_shutdown(move |_| {
        let mut state = s.borrow_mut();
        state.capture.take();
        state.worker.take();
        state.history_key.take();
    });
    let u = ui.clone();
    let s = state.clone();
    window.connect_close_request(move |_| {
        let u = u.clone();
        let s = s.clone();
        glib::MainContext::default().spawn_local(async move {
            let portal = s.borrow().portal.clone();
            let login = s.borrow().settings.launch_at_login;
            if let Some(portal) = portal {
                if matches!(portal.background(login).await, Ok((true, _))) {
                    u.window.set_visible(false);
                } else {
                    u.status(&s.borrow(), "Permiso de segundo plano no disponible.");
                }
            }
        });
        glib::Propagation::Stop
    });
    let controller = gtk::EventControllerKey::new();
    let s = state.clone();
    let u = ui.clone();
    controller.connect_key_pressed(move |_, key, _, _| {
        if key == gtk::gdk::Key::Escape {
            cancel(&s, &u);
            glib::Propagation::Stop
        } else {
            glib::Propagation::Proceed
        }
    });
    window.add_controller(controller);
    let s = state.clone();
    let u = ui.clone();
    ui.save.connect_clicked(move |_| {
        let s = s.clone();
        let u = u.clone();
        let names = names.clone();
        glib::MainContext::default().spawn_local(async move {
            let mut next = s.borrow().settings.clone();
            next.interface_language = if u.interface.selected() == 1 {
                "en"
            } else {
                "es"
            }
            .into();
            next.language = match u.language.selected() {
                1 => "es",
                2 => "en",
                _ => "auto",
            }
            .into();
            next.model = if u.model.selected() == 1 {
                "ggml-small-q5_1.bin"
            } else {
                "ggml-base-q5_1.bin"
            }
            .into();
            next.cpu_threads = u.threads.value() as usize;
            next.unload_minutes = u.unload.value() as usize;
            next.press_enter_after_paste = u.enter.is_active();
            next.save_history = u.history.is_active();
            next.launch_at_login = u.login.is_active();
            next.microphone = names
                .get(u.microphone.selected() as usize)
                .filter(|_| u.microphone.selected() != 0)
                .cloned()
                .unwrap_or_default();
            let portal = s.borrow().portal.clone();
            let previous_login = s.borrow().settings.launch_at_login;
            if next.launch_at_login != previous_login {
                next.launch_at_login = if let Some(portal) = portal {
                    portal
                        .background(next.launch_at_login)
                        .await
                        .map(|(_, login)| login)
                        .unwrap_or(false)
                } else {
                    false
                };
                u.login.set_active(next.launch_at_login);
            }
            if storage::save_settings(&next).is_ok() {
                let mut state = s.borrow_mut();
                state.settings = next;
                if !state.settings.save_history {
                    let _ = std::fs::remove_file(storage::history_path());
                    state.history.clear();
                }
                let _ = configure(&mut state);
                u.refresh(&state);
                u.status(&state, "Ajustes guardados.");
            } else {
                u.status(&s.borrow(), "No se pudieron guardar los ajustes.");
            }
        });
    });
    let s = state.clone();
    let u = ui.clone();
    ui.download.connect_clicked(move |_| {
        let mut state = s.borrow_mut();
        let model = if u.model.selected() == 1 {
            "ggml-small-q5_1.bin"
        } else {
            "ggml-base-q5_1.bin"
        };
        if let Some(worker) = state.worker.as_mut() {
            match worker.send("download", json!({"model":model}), vec![]) {
                Ok(id) => {
                    state.active = Some(id);
                    state.cancelled = false;
                    u.status(&state, "Descargando modelo…");
                    u.refresh(&state);
                }
                Err(_) => u.status(&state, "No se pudo completar el dictado."),
            }
        }
    });
    let s = state.clone();
    let u = ui.clone();
    let e = events.clone();
    ui.shortcut.connect_clicked(move |_| {
        let s = s.clone();
        let u = u.clone();
        let e = e.clone();
        glib::MainContext::default().spawn_local(async move {
            let portal = s.borrow().portal.clone();
            let Some(portal) = portal else { return };
            let shortcut_session = s.borrow().shortcut_session.clone();
            if let Some(path) = shortcut_session {
                if let Ok(proxy) = portal.proxy("org.freedesktop.portal.GlobalShortcuts").await {
                    let _: zbus::Result<()> = proxy
                        .call(
                            "ConfigureShortcuts",
                            &(
                                zbus::zvariant::OwnedObjectPath::try_from(path).unwrap(),
                                "",
                                std::collections::HashMap::<&str, zbus::zvariant::Value>::new(),
                            ),
                        )
                        .await;
                }
                return;
            }
            if s.borrow().shortcut_connecting {
                return;
            }
            s.borrow_mut().shortcut_connecting = true;
            if portal.shortcuts(e).await.is_err() {
                u.status(
                    &s.borrow(),
                    "No se pudo conectar el atajo. Puedes grabar desde Senda.",
                );
            }
            s.borrow_mut().shortcut_connecting = false;
        });
    });
    let s = state.clone();
    let u = ui.clone();
    ui.keyboard.connect_clicked(move |_| {
        let s = s.clone();
        let u = u.clone();
        glib::MainContext::default().spawn_local(async move {
            let portal = s.borrow().portal.clone();
            if let Some(portal) = portal {
                match portal.remote_keyboard().await {
                    Ok(remote) => {
                        s.borrow_mut().remote = Some(remote);
                        u.status(&s.borrow(), "Permiso de teclado concedido.");
                    }
                    Err(_) => u.status(&s.borrow(), "Permiso de teclado no disponible."),
                }
            }
        });
    });
    let s = state.clone();
    let u = ui.clone();
    let events_for_startup = events.clone();
    glib::MainContext::default().spawn_local(async move {
        while let Ok(message) = receiver.recv().await {
            let kind = message["kind"].as_str().unwrap_or_default();
            let result: Result<()> = match kind {
                "ready" => configure(&mut s.borrow_mut()),
                "level" => {
                    u.level
                        .set_fraction((message["data"].as_f64().unwrap_or(0.) * 5.).clamp(0., 1.));
                    Ok(())
                }
                "shortcut-ready" => {
                    s.borrow_mut().shortcut_session =
                        message["session"].as_str().map(str::to_string);
                    s.borrow_mut().settings.enable_shortcut = true;
                    let _ = storage::save_settings(&s.borrow().settings);
                    u.status(&s.borrow(), "Atajo autorizado por el escritorio.");
                    Ok(())
                }
                "shortcut-pressed" => {
                    begin(
                        s.clone(),
                        u.clone(),
                        events.clone(),
                        true,
                        message["modifiersVerifiedClear"].as_bool().unwrap_or(false),
                    )
                    .await
                }
                "shortcut-released" | "audio-limit" => finish(&s, &u),
                "audio-error" => {
                    cancel(&s, &u);
                    u.status(&s.borrow(), "No se pudo abrir el micrófono.");
                    Ok(())
                }
                "transcript" => {
                    let live = {
                        let state = s.borrow();
                        state.active.as_deref() == message["id"].as_str() && !state.cancelled
                    };
                    if live {
                        let delivery_state = s.clone();
                        let delivery_ui = u.clone();
                        let data = message["data"].clone();
                        glib::MainContext::default().spawn_local(async move {
                            if let Err(error) =
                                accept(delivery_state.clone(), delivery_ui.clone(), data).await
                            {
                                delivery_state.borrow_mut().active = None;
                                delivery_ui.status(
                                    &delivery_state.borrow(),
                                    "No se pudo completar el dictado.",
                                );
                                delivery_ui.refresh(&delivery_state.borrow());
                                eprintln!("Local delivery: {error}");
                            }
                        });
                        Ok(())
                    } else {
                        s.borrow_mut().active = None;
                        u.refresh(&s.borrow());
                        Ok(())
                    }
                }
                "downloaded" => (|| -> Result<()> {
                    let mut state = s.borrow_mut();
                    if state.active.as_deref() == message["id"].as_str() {
                        state.active = None;
                        if !state.cancelled {
                            if let Some(model) = message["data"]["model"].as_str().filter(|m| {
                                ["ggml-base-q5_1.bin", "ggml-small-q5_1.bin"].contains(m)
                            }) {
                                state.settings.model = model.into();
                                storage::save_settings(&state.settings)?;
                                configure(&mut state)?;
                            }
                            u.status(&state, "Modelo preparado.");
                        }
                        u.refresh(&state);
                    }
                    Ok(())
                })(),
                "cancelled" | "error" => {
                    let mut state = s.borrow_mut();
                    if state.active.as_deref() == message["id"].as_str() {
                        state.active = None;
                        u.status(
                            &state,
                            if kind == "cancelled" {
                                "Dictado cancelado"
                            } else {
                                "No se pudo completar el dictado."
                            },
                        );
                        u.refresh(&state);
                    }
                    Ok(())
                }
                "disconnected" => {
                    let mut state = s.borrow_mut();
                    state.worker.take();
                    state.capture.take();
                    state.active = None;
                    u.status(
                        &state,
                        "El motor local se cerró. Vuelve a pulsar Grabar para reiniciarlo.",
                    );
                    u.refresh(&state);
                    Ok(())
                }
                "shortcut-closed" => {
                    cancel(&s, &u);
                    s.borrow_mut().shortcut_session = None;
                    Ok(())
                }
                _ => Ok(()),
            };
            if let Err(error) = result {
                u.status(&s.borrow(), "No se pudo completar el dictado.");
                eprintln!("Local operation: {error}");
            }
        }
    });
    let s = state.clone();
    let u = ui.clone();
    let startup_events = events_for_startup;
    glib::MainContext::default().spawn_local(async move{
        if let Ok(portal)=portals::Portal::connect().await{
            s.borrow_mut().portal=Some(portal.clone());
            match portals::FocusTracker::connect().await {
                Ok(focus) => s.borrow_mut().focus=Some(focus),
                Err(error) => {
                    eprintln!("Destination verification unavailable: {error:#}");
                    u.status(&s.borrow(),"No se puede comprobar el destino. El resultado quedará disponible para copiar.");
                }
            }
            let enable_shortcut=s.borrow().settings.enable_shortcut;
            if enable_shortcut {
                s.borrow_mut().shortcut_connecting=true;
                let shortcut_portal=portal.clone();let shortcut_state=s.clone();let shortcut_ui=u.clone();
                glib::MainContext::default().spawn_local(async move {
                    if shortcut_portal.shortcuts(startup_events).await.is_err(){
                        shortcut_ui.status(&shortcut_state.borrow(),"No se pudo conectar el atajo. Puedes grabar desde Senda.");
                        shortcut_ui.window.present();
                    }
                    shortcut_state.borrow_mut().shortcut_connecting=false;
                });
            }else if background{u.window.present();}
            if s.borrow().settings.save_history{match portal.history_key().await{Ok(key)=>{let mut state=s.borrow_mut();match storage::read_history(&key){Ok(entries)=>{state.history=entries;state.history_healthy=true;},Err(_)=>u.status(&state,"El historial no se pudo leer y se conservó intacto.")};state.history_key=Some(zeroize::Zeroizing::new(key));u.refresh(&state);},Err(_)=>u.status(&s.borrow(),"Sin acceso al almacén de claves: el historial no se guardará.")}}
        }else{u.window.present();}
    });
    if !background {
        window.present();
    }
    Ok(())
}

fn main() -> glib::ExitCode {
    glib::set_application_name("Senda");
    if gstreamer::init().is_err() {
        eprintln!("GStreamer audio initialization failed");
        return glib::ExitCode::FAILURE;
    }
    let app = adw::Application::builder()
        .application_id("io.github.AndrewQuest.Senda")
        .build();
    app.connect_startup(|_| {
        adw::StyleManager::default().set_color_scheme(adw::ColorScheme::PreferDark);
        let css = gtk::CssProvider::new();
        css.load_from_data(
            "@define-color accent_color #efa989; @define-color accent_bg_color #c66d45;",
        );
        if let Some(display) = gtk::gdk::Display::default() {
            gtk::style_context_add_provider_for_display(
                &display,
                &css,
                gtk::STYLE_PROVIDER_PRIORITY_APPLICATION,
            );
        }
    });
    let background = std::env::args().any(|arg| arg == "--background");
    app.connect_activate(move |app| {
        if let Some(window) = app.windows().first() {
            window.present();
            return;
        }
        if let Err(error) = build(app, background) {
            eprintln!("Senda startup failed: {error}");
            app.quit();
        }
    });
    // --background is an application mode, not a GTK command-line option.
    app.run_with_args(&["senda"])
}
