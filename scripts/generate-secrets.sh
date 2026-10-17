#!/usr/bin/env bash
# Fills the generated secrets in .env (docs/deployment.md section 2): the PostgreSQL role passwords, the master key and the
# API Key pepper. A value is generated only when it is empty or CHANGE_ME, so running the script again changes nothing.
# Creates .env from .env.example when it does not exist. Works with the bash 3.2 and BSD tools of macOS and with GNU/Linux.
#
#   ./scripts/generate-secrets.sh [path/to/.env]
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
env_file="${1:-$root/.env}"

if [ ! -f "$env_file" ]; then
  cp "$root/.env.example" "$env_file"
  echo "Created $env_file from .env.example"
fi
chmod 600 "$env_file"

random_hex() {
  if command -v openssl >/dev/null 2>&1; then openssl rand -hex 24; else od -An -N24 -tx1 /dev/urandom | tr -d ' \n'; fi
}

random_base64() {
  if command -v openssl >/dev/null 2>&1; then openssl rand -base64 32; else head -c 32 /dev/urandom | base64 | tr -d '\n'; fi
}

current_value() {
  # The value of the last uncommented KEY=… line, or nothing.
  awk -v key="$1" 'index($0, key "=") == 1 { value = substr($0, length(key) + 2) } END { print value }' "$env_file"
}

set_value() {
  local key="$1" value="$2" tmp
  tmp="$(mktemp "${env_file}.XXXXXX")"
  if grep -q "^$key=" "$env_file"; then
    awk -v key="$key" -v value="$value" 'index($0, key "=") == 1 { print key "=" value; next } { print }' "$env_file" > "$tmp"
  else
    cat "$env_file" > "$tmp"
    printf '%s=%s\n' "$key" "$value" >> "$tmp"
  fi
  cat "$tmp" > "$env_file"
  rm -f "$tmp"
}

fill() {
  local key="$1" generator="$2" value
  value="$(current_value "$key")"
  if [ -z "$value" ] || [ "$value" = "CHANGE_ME" ]; then
    set_value "$key" "$("$generator")"
    echo "generated $key"
  else
    echo "kept      $key"
  fi
}

# Hex passwords: no characters that need escaping inside a connection string.
fill POSTGRES_MIGRATOR_PASSWORD random_hex
fill POSTGRES_APP_PASSWORD random_hex
fill POSTGRES_RETENTION_PASSWORD random_hex
fill MODELSWITCH__Security__MasterKey random_base64
fill MODELSWITCH__Security__ApiKeyPepper random_base64

if [ -z "$(current_value MODELSWITCH__Bootstrap__OwnerEmail)" ] || [ -z "$(current_value MODELSWITCH__Bootstrap__OwnerPassword)" ]; then
  echo
  echo "Next: set MODELSWITCH__Bootstrap__OwnerEmail and MODELSWITCH__Bootstrap__OwnerPassword (12+ characters) in $env_file."
fi
