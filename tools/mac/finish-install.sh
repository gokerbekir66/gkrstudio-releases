#!/bin/bash
# GKR Studio — finish an install or update on macOS. Shipped inside the bundle as Contents/Resources/finish-install.sh
# and run by install-mac.sh, "Install GKR Studio.command" and the in-app updater.
#
#   finish-install.sh <"path/GKR Studio.app"> [--open | --tray | --none | --sign-only]
#
# Installers call it twice: first with --sign-only on the unpacked bundle in a temp/staging folder (macOS "App
# Management" may forbid changing a bundle that is already in /Applications), then on the moved bundle, where a valid
# signature is left alone.
#
# 1. removes quarantine, 2. signs the bundle, 3. refreshes LaunchServices (Dock icon),
# 4. (re)loads the login agent and starts GKRStudio.
#
# Signing: a per-Mac self-signed identity "GKR Studio Local Signing" is created once in the login keychain and reused
# for every update. macOS privacy permissions (Accessibility, Automation) are tied to the signature's designated
# requirement; with ad-hoc signing that changes on every update and the permissions silently stop working.
# If the identity can't be created or used, it falls back to ad-hoc signing.

APP="$1"
MODE="${2:---open}"
ID="GKR Studio Local Signing"
KC="$HOME/Library/Keychains/login.keychain-db"
echo "finish-install $(date '+%F %T') app=$APP mode=$MODE"
[ -d "$APP" ] || { echo "no app at $APP"; exit 1; }

# Already signed (signed before it was moved here) → don't touch the bundle in /Applications at all.
already=""
if [ "$MODE" != "--sign-only" ] && codesign --verify --deep --strict "$APP" 2>/dev/null; then
  already=1; echo "signature valid, bundle left as is"
fi
if [ -z "$already" ]; then
  chmod +x "$APP/Contents/MacOS/GKRStudio" 2>/dev/null
  chmod +x "$APP/Contents/MacOS/tools/"* 2>/dev/null
  xattr -cr "$APP" 2>/dev/null
fi

# ---------- signing identity ----------
has_identity() { security find-identity -p codesigning "$KC" 2>/dev/null | grep -q "\"$ID\""; }
if ! has_identity && [ -x /usr/bin/openssl ]; then
  T="$(mktemp -d)"
  cat > "$T/cfg" <<EOF
[req]
distinguished_name=dn
x509_extensions=ext
prompt=no
[dn]
CN=$ID
[ext]
keyUsage=critical,digitalSignature
extendedKeyUsage=critical,codeSigning
basicConstraints=critical,CA:false
EOF
  P="gkrstudio-$RANDOM$RANDOM"
  if /usr/bin/openssl req -x509 -newkey rsa:2048 -nodes -days 7300 -keyout "$T/k.pem" -out "$T/c.pem" -config "$T/cfg" >/dev/null 2>&1 \
     && /usr/bin/openssl pkcs12 -export -inkey "$T/k.pem" -in "$T/c.pem" -out "$T/i.p12" -passout "pass:$P" -name "$ID" >/dev/null 2>&1 \
     && security import "$T/i.p12" -k "$KC" -P "$P" -T /usr/bin/codesign >/dev/null 2>&1; then
    echo "created signing identity"
  else
    echo "could not create signing identity (ad-hoc fallback)"
  fi
  rm -rf "$T"
fi

# codesign may wait for a keychain prompt ("Always Allow"); never hang the update on it
cs() {
  codesign --force --sign "$@" >/dev/null 2>&1 &
  local pid=$! i=0
  while kill -0 "$pid" 2>/dev/null; do
    sleep 0.25; i=$((i + 1))
    if [ "$i" -gt 360 ]; then kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null; return 1; fi
  done
  wait "$pid"
}

# order matters: everything inside Contents/MacOS first, then the main binary, then the bundle
sign_all() {
  local who="$1" f
  while IFS= read -r -d '' f; do cs "$who" "$f" || return 1; done < <(find "$APP/Contents/MacOS" -type f ! -name GKRStudio -print0)
  cs "$who" "$APP/Contents/MacOS/GKRStudio" || return 1
  cs "$who" "$APP" || return 1
}

if [ -n "$already" ]; then :
elif has_identity && sign_all "$ID"; then
  echo "signed with $ID"
else
  echo "signing ad-hoc"
  sign_all - || echo "ad-hoc signing reported errors"
fi
codesign -dr - "$APP" 2>&1 | grep designated
if [ "$MODE" = "--sign-only" ]; then echo "finish-install sign-only done"; exit 0; fi

# ---------- Dock icon / Launch Services ----------
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$APP" 2>/dev/null
touch "$APP" 2>/dev/null

# ---------- former name (up to 1.1.6): remove the old app and its login agent ----------
# (its Quick Actions stay until GKR Studio's first start, which replaces them with its own)
OLD="Lion""Media"; OLDL="lion""media"
pkill -x "$OLD" 2>/dev/null
launchctl bootout "gui/$(id -u)/com.$OLDL.agent" 2>/dev/null
rm -f "$HOME/Library/LaunchAgents/com.$OLDL.agent.plist"
rm -rf "/Applications/$OLD.app" 2>/dev/null
rm -rf "$HOME/Library/Services/$OLD"*.workflow "$HOME/Library/Application Support/$OLD" "$HOME/Library/Caches/$OLD" 2>/dev/null
# its per-Mac signing identity (certificate + key) in the login keychain
while security find-identity -p codesigning "$KC" 2>/dev/null | grep -q "\"$OLD Local Signing\""; do
  security delete-identity -c "$OLD Local Signing" "$KC" >/dev/null 2>&1 || break
done
if [ -f "$HOME/.config/$OLD/settings.json" ] && [ ! -f "$HOME/.config/GKRStudio/settings.json" ]; then
  mkdir -p "$HOME/.config/GKRStudio" && cp "$HOME/.config/$OLD/settings.json" "$HOME/.config/GKRStudio/settings.json"
fi

# ---------- login agent + start ----------
PL="$HOME/Library/LaunchAgents/com.gkrstudio.agent.plist"
U="$(id -u)"
started=""
if [ "$MODE" != "--none" ] && [ -f "$PL" ]; then
  # rewrite the agent for this bundle path (PlistBuddy can't take a value with spaces reliably)
  cat > "$PL" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>Label</key><string>com.gkrstudio.agent</string>
  <key>ProgramArguments</key><array><string>$APP/Contents/MacOS/GKRStudio</string><string>--tray</string></array>
  <key>RunAtLoad</key><true/><key>AbandonProcessGroup</key><true/>
  <key>ProcessType</key><string>Interactive</string>
</dict></plist>
PLIST
  launchctl bootout "gui/$U/com.gkrstudio.agent" 2>/dev/null
  if launchctl bootstrap "gui/$U" "$PL" 2>/dev/null; then echo "login agent loaded (menu-bar instance started)"; started=1
  else echo "login agent could not be loaded"; fi
fi
case "$MODE" in
  --open) open "$APP" ;;
  --tray) [ -n "$started" ] || open "$APP" --args --tray ;;
esac
echo "finish-install done $(date '+%F %T')"
