use anyhow::{Context, Result, bail};
use futures_util::StreamExt;
use serde::Serialize;
use serde_json::json;
use sha2::{Digest, Sha256};
use std::{
    cell::{Cell, RefCell},
    collections::HashMap,
    io::Read,
    rc::Rc,
    sync::atomic::{AtomicU64, Ordering},
};
use zbus::{
    Connection, Proxy,
    zvariant::{DynamicType, Fd, OwnedObjectPath, OwnedValue, Value},
};

type Options = HashMap<&'static str, Value<'static>>;
type Results = HashMap<String, OwnedValue>;
const DESKTOP: &str = "org.freedesktop.portal.Desktop";
const PATH: &str = "/org/freedesktop/portal/desktop";
static NEXT: AtomicU64 = AtomicU64::new(1);
fn token() -> String {
    format!("senda_{}", NEXT.fetch_add(1, Ordering::Relaxed))
}
fn session_path(value: OwnedValue) -> Result<OwnedObjectPath> {
    // Portal versions encode session_handle as either a path or a string.
    Ok(OwnedObjectPath::try_from(value.try_clone()?)
        .or_else(|_| String::try_from(value).and_then(OwnedObjectPath::try_from))?)
}

#[derive(Clone)]
pub struct Portal {
    pub connection: Connection,
}
impl Portal {
    pub async fn connect() -> Result<Self> {
        Ok(Self {
            connection: Connection::session().await?,
        })
    }
    pub async fn proxy(&self, interface: &'static str) -> Result<Proxy<'static>> {
        Ok(Proxy::new_owned(self.connection.clone(), DESKTOP, PATH, interface).await?)
    }
    async fn request<B: Serialize + DynamicType>(
        &self,
        interface: &'static str,
        method: &'static str,
        mut options: Options,
        body: impl FnOnce(Options) -> B,
    ) -> Result<Results> {
        let handle = token();
        let sender = self
            .connection
            .unique_name()
            .context("No D-Bus name")?
            .as_str()
            .trim_start_matches(':')
            .replace('.', "_");
        let expected = format!("{PATH}/request/{sender}/{handle}");
        let request = Proxy::new_owned(
            self.connection.clone(),
            DESKTOP,
            expected,
            "org.freedesktop.portal.Request",
        )
        .await?;
        let mut responses = request.receive_signal("Response").await?;
        options.insert("handle_token", Value::from(handle));
        let _: OwnedObjectPath = self
            .proxy(interface)
            .await?
            .call(method, &body(options))
            .await?;
        let response = responses.next().await.context("Portal request closed")?;
        let (code, results): (u32, Results) = response.body().deserialize()?;
        if code != 0 {
            bail!("Desktop permission was cancelled or denied ({code})")
        }
        Ok(results)
    }

    pub async fn shortcuts(&self, events: async_channel::Sender<serde_json::Value>) -> Result<()> {
        const IFACE: &str = "org.freedesktop.portal.GlobalShortcuts";
        let mut options = Options::new();
        options.insert("session_handle_token", Value::from(token()));
        let mut created = self
            .request(IFACE, "CreateSession", options, |o| (o,))
            .await?;
        let value = created
            .remove("session_handle")
            .context("No shortcut session")?;
        let session = session_path(value)?;
        let proxy = self.proxy(IFACE).await?;
        let mut activated = proxy.receive_signal("Activated").await?;
        let mut deactivated = proxy.receive_signal("Deactivated").await?;
        let session_proxy = Proxy::new_owned(
            self.connection.clone(),
            DESKTOP,
            session.clone(),
            "org.freedesktop.portal.Session",
        )
        .await?;
        let mut closed = session_proxy.receive_signal("Closed").await?;
        let mut descriptor = Options::new();
        descriptor.insert("description", Value::from("Senda — dictation"));
        descriptor.insert("preferred_trigger", Value::from("CTRL+ALT+space"));
        self.request(IFACE, "BindShortcuts", Options::new(), |o| {
            (session.clone(), vec![("dictate", descriptor)], "", o)
        })
        .await?;
        let _ = events
            .send(json!({"kind":"shortcut-ready","session":session.as_str()}))
            .await;
        loop {
            futures_util::select_biased! {
                _ = closed.next().fuse() => { break; },
                signal = activated.next().fuse() => {
                    let Some(signal)=signal else { break };
                    let (path,id,_,_options):(OwnedObjectPath,String,u64,Results)=signal.body().deserialize()?;
                    if path==session && id=="dictate" {
                        // GlobalShortcuts has no current modifier-state contract. Never infer it
                        // from the shortcut or an undocumented event option.
                        let _=events.send(json!({"kind":"shortcut-pressed","modifiersVerifiedClear":false})).await;
                    }
                },
                signal = deactivated.next().fuse() => {
                    let Some(signal)=signal else { break };
                    let (path,id,_,_):(OwnedObjectPath,String,u64,Results)=signal.body().deserialize()?;
                    if path==session && id=="dictate" {let _=events.send(json!({"kind":"shortcut-released"})).await;}
                }
            }
        }
        let _ = events.send(json!({"kind":"shortcut-closed"})).await;
        Ok(())
    }

    pub async fn background(&self, autostart: bool) -> Result<(bool, bool)> {
        let mut options = Options::new();
        options.insert(
            "reason",
            Value::from("Local voice dictation using your chosen shortcut"),
        );
        options.insert("autostart", Value::from(autostart));
        options.insert("commandline", Value::from(vec!["senda", "--background"]));
        let result = self
            .request(
                "org.freedesktop.portal.Background",
                "RequestBackground",
                options,
                |o| ("", o),
            )
            .await?;
        Ok((
            result
                .get("background")
                .and_then(|v| bool::try_from(v).ok())
                .unwrap_or(false),
            result
                .get("autostart")
                .and_then(|v| bool::try_from(v).ok())
                .unwrap_or(false),
        ))
    }

    pub async fn history_key(&self) -> Result<[u8; 32]> {
        let (mut read, write) = std::os::unix::net::UnixStream::pair()?;
        read.set_read_timeout(Some(std::time::Duration::from_secs(2)))?;
        let token_path = crate::storage::data_dir().join("keyring-token");
        let mut options = Options::new();
        if let Ok(value) = std::fs::read_to_string(&token_path) {
            options.insert("token", Value::from(value));
        }
        let result = self
            .request(
                "org.freedesktop.portal.Secret",
                "RetrieveSecret",
                options,
                |o| (Fd::from(&write), o),
            )
            .await?;
        drop(write);
        let mut secret = zeroize::Zeroizing::new(Vec::new());
        read.by_ref().take(4097).read_to_end(&mut secret)?;
        if secret.is_empty() || secret.len() > 4096 {
            bail!("Invalid keyring secret")
        }
        if let Some(value) = result.get("token").and_then(|v| <&str>::try_from(v).ok()) {
            crate::storage::atomic_write(&token_path, value.as_bytes())?;
        }
        Ok(Sha256::digest(secret.as_slice()).into())
    }

    pub async fn remote_keyboard(&self) -> Result<RemoteKeyboard> {
        const IFACE: &str = "org.freedesktop.portal.RemoteDesktop";
        let mut options = Options::new();
        options.insert("session_handle_token", Value::from(token()));
        let mut result = self
            .request(IFACE, "CreateSession", options, |o| (o,))
            .await?;
        let session = session_path(
            result
                .remove("session_handle")
                .context("No remote session")?,
        )?;
        let mut options = Options::new();
        options.insert("types", Value::from(1u32));
        self.request(IFACE, "SelectDevices", options, |o| (session.clone(), o))
            .await?;
        let result = self
            .request(IFACE, "Start", Options::new(), |o| (session.clone(), "", o))
            .await?;
        if result
            .get("devices")
            .and_then(|v| u32::try_from(v).ok())
            .unwrap_or(0)
            & 1
            == 0
        {
            bail!("Keyboard permission was not granted")
        }
        let valid = Rc::new(Cell::new(true));
        let proxy = Proxy::new_owned(
            self.connection.clone(),
            DESKTOP,
            session.clone(),
            "org.freedesktop.portal.Session",
        )
        .await?;
        let mut closed = proxy.receive_signal("Closed").await?;
        let state = valid.clone();
        gtk::glib::MainContext::default().spawn_local(async move {
            let _ = closed.next().await;
            state.set(false);
        });
        Ok(RemoteKeyboard {
            portal: self.clone(),
            session,
            valid,
        })
    }
}

#[derive(Clone)]
pub struct RemoteKeyboard {
    portal: Portal,
    session: OwnedObjectPath,
    pub valid: Rc<Cell<bool>>,
}
impl RemoteKeyboard {
    pub async fn enter(&self) -> Result<()> {
        if !self.valid.get() {
            bail!("Keyboard permission was withdrawn")
        }
        let proxy = self
            .portal
            .proxy("org.freedesktop.portal.RemoteDesktop")
            .await?;
        let result: zbus::Result<()> = proxy
            .call(
                "NotifyKeyboardKeysym",
                &(self.session.clone(), Options::new(), 0xff0di32, 1u32),
            )
            .await;
        // Key-up cleanup is allowed even when the key-down reply was lost. Never retry key-down.
        let released: zbus::Result<()> = proxy
            .call(
                "NotifyKeyboardKeysym",
                &(self.session.clone(), Options::new(), 0xff0di32, 0u32),
            )
            .await;
        result?;
        released?;
        Ok(())
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct FocusId {
    pub bus: String,
    pub path: OwnedObjectPath,
}
#[derive(Clone)]
pub struct FocusTracker {
    connection: Connection,
    pub current: Rc<RefCell<Option<FocusId>>>,
}
impl FocusTracker {
    pub async fn connect() -> Result<Self> {
        // Flatpak supplies a filtered accessibility socket. GetAddress can
        // return a host path that does not exist inside the sandbox.
        let address = match std::env::var("AT_SPI_BUS_ADDRESS") {
            Ok(address) if !address.is_empty() => address,
            _ => {
                let session = Connection::session().await?;
                let bus =
                    Proxy::new(&session, "org.a11y.Bus", "/org/a11y/bus", "org.a11y.Bus").await?;
                bus.call::<_, _, String>("GetAddress", &()).await?
            }
        };
        let connection = zbus::connection::Builder::address(address.as_str())?
            .build()
            .await?;
        let registry = Proxy::new(
            &connection,
            "org.a11y.atspi.Registry",
            "/org/a11y/atspi/registry",
            "org.a11y.atspi.Registry",
        )
        .await?;
        let _: () = registry
            .call(
                "RegisterEvent",
                &("object:state-changed:focused", Vec::<String>::new(), ""),
            )
            .await?;
        let rule = zbus::MatchRule::builder()
            .msg_type(zbus::message::Type::Signal)
            .interface("org.a11y.atspi.Event.Object")?
            .member("StateChanged")?
            .build();
        let mut stream = zbus::MessageStream::for_match_rule(rule, &connection, Some(32)).await?;
        let current = Rc::new(RefCell::new(None));
        let state = current.clone();
        gtk::glib::MainContext::default().spawn_local(async move {
            while let Some(Ok(message)) = stream.next().await {
                if let Ok((detail, enabled, _, _, _)) =
                    message
                        .body()
                        .deserialize::<(String, i32, i32, OwnedValue, Results)>()
                {
                    if detail != "focused" {
                        continue;
                    }
                    let header = message.header();
                    if let (Some(sender), Some(path)) = (header.sender(), header.path()) {
                        let id = FocusId {
                            bus: sender.to_string(),
                            path: path.to_owned().into(),
                        };
                        if enabled != 0 {
                            *state.borrow_mut() = Some(id);
                        } else if state.borrow().as_ref() == Some(&id) {
                            *state.borrow_mut() = None;
                        }
                    }
                }
            }
            *state.borrow_mut() = None;
        });
        Ok(Self {
            connection,
            current,
        })
    }
    pub async fn validate(&self, target: &FocusId) -> Result<()> {
        if self.current.borrow().as_ref() != Some(target) {
            bail!("The destination field changed")
        }
        let accessible = Proxy::new(
            &self.connection,
            target.bus.clone(),
            target.path.clone(),
            "org.a11y.atspi.Accessible",
        )
        .await?;
        let states: Vec<u32> = accessible.call("GetState", &()).await?;
        let flags = states.first().copied().unwrap_or(0);
        if flags & (1 << 12) == 0 || flags & (1 << 7) == 0 {
            bail!("Destination is not a focused editable field")
        }
        let role: u32 = accessible.call("GetRole", &()).await?;
        if role == 40 {
            // ATSPI_ROLE_PASSWORD_TEXT
            bail!("Dictation is disabled in password fields")
        }
        if self.current.borrow().as_ref() != Some(target) {
            bail!("The destination field changed")
        }
        Ok(())
    }
    pub async fn insert(
        &self,
        target: &FocusId,
        text: &str,
        allowed: impl Fn() -> bool,
    ) -> Result<()> {
        if !allowed() {
            bail!("Delivery cancelled")
        }
        self.validate(target).await?;
        let field = Proxy::new(
            &self.connection,
            target.bus.clone(),
            target.path.clone(),
            "org.a11y.atspi.Text",
        )
        .await?;
        let selections: i32 = field.call("GetNSelections", &()).await?;
        if selections != 0 {
            bail!("Selected text requires manual paste")
        }
        let caret: i32 = field.get_property("CaretOffset").await?;
        self.validate(target).await?;
        let editable = Proxy::new(
            &self.connection,
            target.bus.clone(),
            target.path.clone(),
            "org.a11y.atspi.EditableText",
        )
        .await?;
        if !allowed() || self.current.borrow().as_ref() != Some(target) {
            bail!("Delivery cancelled or destination changed")
        }
        let accepted: bool = editable
            .call("InsertText", &(caret, text, text.len() as i32))
            .await?;
        if !accepted {
            bail!("The app did not accept text insertion")
        }
        if self.validate(target).await.is_ok() {
            let _: zbus::Result<bool> = field
                .call("SetCaretOffset", &(caret + text.chars().count() as i32,))
                .await;
        }
        Ok(())
    }
}

use futures_util::FutureExt;

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn accepts_both_portal_session_encodings() {
        let path = "/org/freedesktop/portal/desktop/session/test/senda";
        let object = Value::from(OwnedObjectPath::try_from(path.to_string()).unwrap());
        assert_eq!(
            session_path(OwnedValue::try_from(object).unwrap())
                .unwrap()
                .as_str(),
            path
        );
        assert_eq!(
            session_path(OwnedValue::try_from(Value::from(path.to_string())).unwrap())
                .unwrap()
                .as_str(),
            path
        );
        assert!(
            session_path(OwnedValue::try_from(Value::from("not a path".to_string())).unwrap())
                .is_err()
        );
    }
}
