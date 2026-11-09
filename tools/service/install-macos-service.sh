#!/bin/sh
# Starts MeshCore Computer Client automatically when you log in to your Mac (a launchd LaunchAgent, no sudo needed).
#
#   ./service/install-macos-service.sh              run it from the folder you unpacked (and keep the app there)
#   ./service/install-macos-service.sh --uninstall  stop it and remove the LaunchAgent (keeps the app and its data)
#
# It also removes the "downloaded from the internet" quarantine flag from the app folder, so macOS lets it start.
set -eu

LABEL=meshcore-computer-client
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
APP_DIR=$(cd "$(dirname "$0")/.." && pwd)
DOMAIN="gui/$(id -u)"

if [ "${1:-}" = "--uninstall" ]; then
    launchctl bootout "$DOMAIN/$LABEL" 2>/dev/null || true
    rm -f "$PLIST"
    echo "Removed. The app and its data are still in $APP_DIR."
    exit 0
fi

if [ ! -f "$APP_DIR/MeshCore.Host" ]; then
    echo "MeshCore.Host not found in $APP_DIR. Run this script from the unpacked release folder." >&2
    exit 1
fi

xattr -dr com.apple.quarantine "$APP_DIR" 2>/dev/null || true
chmod +x "$APP_DIR/MeshCore.Host" "$APP_DIR/meshcore-cli"

mkdir -p "$HOME/Library/LaunchAgents"
cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key><string>$LABEL</string>
    <key>ProgramArguments</key><array><string>$APP_DIR/MeshCore.Host</string></array>
    <key>WorkingDirectory</key><string>$APP_DIR</string>
    <key>EnvironmentVariables</key><dict><key>DOTNET_ENVIRONMENT</key><string>Production</string></dict>
    <key>RunAtLoad</key><true/>
    <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
    <key>ThrottleInterval</key><integer>10</integer>
    <key>StandardOutPath</key><string>/dev/null</string>
    <key>StandardErrorPath</key><string>$APP_DIR/data/launchd-errors.log</string>
</dict>
</plist>
EOF

mkdir -p "$APP_DIR/data"
launchctl bootout "$DOMAIN/$LABEL" 2>/dev/null || true
launchctl bootstrap "$DOMAIN" "$PLIST"

echo "Installed: it runs now and every time you log in. Open http://localhost:5080"
echo "Logs: $APP_DIR/data/logs    Restart after changing appsettings.json: launchctl kickstart -k $DOMAIN/$LABEL"
