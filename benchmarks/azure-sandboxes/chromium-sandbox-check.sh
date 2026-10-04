#!/bin/sh
# Chromium's sandbox inside one sandbox, the check validate-kubernetes-security.sh runs in every pod:
# no browser process runs with --no-sandbox, and the sandboxed zygote, which forks the renderers, is
# outside the browser's user namespace. Exits 1 when the sandbox is not active.
browser=""; separate=0
for task in /proc/[0-9]*; do
  # A process that exits meanwhile (a closing renderer) is skipped.
  comm=$(cat "$task/comm" 2>/dev/null) || continue
  case "$comm" in chrome*|headless*) ;; *) continue ;; esac
  command_line=" $(tr "\000" " " < "$task/cmdline" 2>/dev/null) " || continue
  userns=$(readlink "$task/ns/user" 2>/dev/null) || continue
  case "$command_line" in *" --no-sandbox "*) echo "${task#/proc/} runs with --no-sandbox"; exit 1 ;; esac
  case "$command_line" in
    "  ") continue ;;
    *" --type="*) [ "$userns" = "$(readlink /proc/self/ns/user)" ] || separate=$((separate + 1)) ;;
    *) browser="$userns" ;;
  esac
done
echo "browser_userns=$browser self_userns=$(readlink /proc/self/ns/user) separate=$separate"
[ -n "$browser" ] && [ "$browser" = "$(readlink /proc/self/ns/user)" ] && [ "$separate" -gt 0 ]
