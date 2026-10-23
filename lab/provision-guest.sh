#!/bin/sh
# Bootstrap only the owned, fresh VirtualBox test guest. Never run on a host OS.
set -eu
test "$(id -u)" -eq 0 || { printf 'Run with sudo inside the test guest.\n' >&2; exit 1; }
test "$(cat /sys/class/dmi/id/product_name)" = VirtualBox || exit 1
case "$(hostname -s)" in senda-kde|senda-gnome) ;; *) printf 'Unknown test guest.\n' >&2; exit 1 ;; esac
test "$(id -u sendatest)" -eq 1000 || exit 1
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get -o Dpkg::Use-Pty=0 install -y --no-install-recommends virtualbox-guest-utils flatpak pulseaudio-utils dbus-x11 wl-clipboard python3-gi python3-pyatspi gnome-text-editor </dev/null
systemctl enable --now virtualbox-guest-utils.service
dpkg-query -W virtualbox-guest-utils flatpak pulseaudio-utils dbus-x11 wl-clipboard python3-gi python3-pyatspi gnome-text-editor > /var/log/senda-lab-provisioning-packages.txt
touch /var/lib/senda-lab-provisioned
printf 'Test guest provisioned. Shared folders, shared clipboard, USB and remote access remain disabled.\n'
