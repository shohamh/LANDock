# Shared by the runtime tests (sourced, not run). They all use one data folder, and it keeps what Windows must not see as new:
# the laptop's id (which names the desktop's Moonlight copies) and the copies themselves. Windows asks the firewall question once
# per program path, so a new path for each run means a new question for each run. Settings and pairings are reset every run.
reset_shared_data() {   # reset_shared_data DIR  (the data folder is DIR/data)
  local root="$1/data"
  local id; id=$(cat "$root/host/host-id.txt" 2>/dev/null || true)
  mkdir -p "$root"
  find "$root" -mindepth 1 -maxdepth 1 ! -name streams -exec rm -rf {} + 2>/dev/null
  mkdir -p "$root/host"
  [ -n "$id" ] && printf '%s' "$id" > "$root/host/host-id.txt"
  find "$root/streams" -name Moonlight.ini -delete 2>/dev/null
  return 0
}
