#!/bin/sh
# Run only in the disposable guest. Generates an in-memory virtual microphone
# fed from a public/synthetic WAV; never enables the Windows host microphone.
set -eu
test -f /var/lib/senda-lab-provisioned || { printf 'Run only in a provisioned Senda test guest.\n' >&2; exit 1; }
umask 077
case "${1:-}" in
  create)
    test ! -e "$XDG_RUNTIME_DIR/senda-lab-audio.modules" || { printf 'Remove the previous test source first.\n' >&2; exit 1; }
    sink=$(pactl load-module module-null-sink sink_name=senda_lab sink_properties=device.description=SendaLabAudio)
    if ! source=$(pactl load-module module-remap-source master=senda_lab.monitor source_name=senda_lab_mic source_properties=device.description=SendaLabMicrophone); then
      pactl unload-module "$sink"
      exit 1
    fi
    printf '%s\n%s\n' "$sink" "$source" > "$XDG_RUNTIME_DIR/senda-lab-audio.modules"
    printf 'Virtual source created; select SendaLabMicrophone in Senda. Host audio input stays disabled.\n'
    ;;
  play)
    test $# -eq 2
    test -f "$2"
    paplay --device=senda_lab "$2"
    ;;
  remove)
    record="$XDG_RUNTIME_DIR/senda-lab-audio.modules"
    test -f "$record" || exit 0
    # Remove source before sink, using only the IDs created by this helper.
    source_id=$(sed -n '2p' "$record")
    sink_id=$(sed -n '1p' "$record")
    for module in "$source_id" "$sink_id"; do
      case "$module" in ''|*[!0-9]*) exit 1 ;; esac
      pactl unload-module "$module"
    done
    rm -- "$record"
    ;;
  *) printf 'Usage: %s create | play FIXTURE.wav | remove\n' "$0" >&2; exit 2 ;;
esac
