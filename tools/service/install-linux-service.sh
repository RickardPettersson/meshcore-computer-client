#!/bin/sh
# Installs (or updates) MeshCore Computer Client as a systemd service, e.g. on a Raspberry Pi.
#
#   sudo ./service/install-linux-service.sh             run it from the unpacked release folder
#   sudo ./service/install-linux-service.sh --uninstall stop and remove the service (keeps /opt/meshcore and its data)
#
# The app goes to /opt/meshcore and runs as the unprivileged "meshcore" user. On an update, your appsettings.json
# and the data folder (messages, users, keys, certificates) are kept.
set -eu

APP_DIR=/opt/meshcore
UNIT=/etc/systemd/system/meshcore.service
SRC_DIR=$(cd "$(dirname "$0")/.." && pwd)

if [ "$(id -u)" -ne 0 ]; then
    echo "Run this with sudo." >&2
    exit 1
fi

if [ "${1:-}" = "--uninstall" ]; then
    systemctl disable --now meshcore 2>/dev/null || true
    rm -f "$UNIT"
    systemctl daemon-reload
    echo "Service removed. The app and its data are still in $APP_DIR (delete it yourself if you want)."
    exit 0
fi

if [ ! -x "$SRC_DIR/MeshCore.Host" ]; then
    echo "MeshCore.Host not found in $SRC_DIR. Run this script from the unpacked release folder." >&2
    exit 1
fi

id meshcore >/dev/null 2>&1 || useradd --system --home-dir "$APP_DIR" --shell /usr/sbin/nologin meshcore
# USB radios: the dialout group may open serial ports.
getent group dialout >/dev/null 2>&1 && usermod -aG dialout meshcore

systemctl stop meshcore 2>/dev/null || true
mkdir -p "$APP_DIR"
if [ "$SRC_DIR" != "$APP_DIR" ]; then
    keep_settings=false
    [ -f "$APP_DIR/appsettings.json" ] && keep_settings=true
    for item in "$SRC_DIR"/* ; do
        name=$(basename "$item")
        [ "$name" = "data" ] && continue
        [ "$name" = "appsettings.json" ] && [ "$keep_settings" = true ] && continue
        rm -rf "${APP_DIR:?}/$name"
        cp -R "$item" "$APP_DIR/"
    done
    [ "$keep_settings" = true ] && echo "Kept your existing $APP_DIR/appsettings.json."
fi
chown -R meshcore "$APP_DIR"
chmod +x "$APP_DIR/MeshCore.Host" "$APP_DIR/meshcore-cli"

cp "$SRC_DIR/service/meshcore.service" "$UNIT"
systemctl daemon-reload
systemctl enable --now meshcore

echo "Installed. Status: systemctl status meshcore    Logs: journalctl -u meshcore -f  (or $APP_DIR/data/logs)"
echo "Settings: $APP_DIR/appsettings.json, then: sudo systemctl restart meshcore"
