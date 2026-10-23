use std::{collections::HashMap, sync::LazyLock};
static CATALOG: LazyLock<HashMap<String, String>> = LazyLock::new(|| {
    let mut map: HashMap<String, String> = serde_json::from_str(include_str!(
        "../../src/Transcriptor.Core/Localization/en.json"
    ))
    .expect("Embedded core catalog");
    map.extend(
        serde_json::from_str::<HashMap<String, String>>(include_str!("../messages.en.json"))
            .expect("Embedded Linux catalog"),
    );
    map
});
pub fn text(english: bool, source: &str) -> String {
    if english {
        CATALOG
            .get(source)
            .cloned()
            .unwrap_or_else(|| source.into())
    } else {
        source.into()
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn permanent_enter_warning_matches_both_requested_languages() {
        let spanish = "Al activar esta opción, Senda pulsará Enter después de pegar cada dictado. Esto puede enviar mensajes, confirmar acciones o ejecutar comandos según la aplicación.";
        assert_eq!(super::text(false, spanish), spanish);
        assert_eq!(
            super::text(true, spanish),
            "When enabled, Senda presses Enter after pasting each dictation. This may send messages, confirm actions, or execute commands depending on the app."
        );
    }
}
