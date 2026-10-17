#!/usr/bin/env bash
# Operate the three local Compose projects while retaining their individual Docker groups.
set -euo pipefail

usage() {
    printf 'Usage: bash manage.sh [start|stop|restart|status|pull|down] [--build] [--dry-run]\n'
}

action="${1:-start}"
if (($#)); then shift; fi
case "$action" in
    start|stop|restart|status|pull|down) ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
esac

build=false
dry_run=false
for option in "$@"; do
    case "$option" in
        --build) build=true ;;
        --dry-run) dry_run=true ;;
        *) usage >&2; exit 2 ;;
    esac
done
if [[ "$build" == true && "$action" != start && "$action" != restart ]]; then
    printf '%s\n' '--build is supported only with start or restart.' >&2
    exit 2
fi

# BASH_SOURCE keeps paths independent of the caller's working directory.
deploy_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
startup_order=(infrastructure telemetry app)
shutdown_order=(app telemetry infrastructure)

run_compose() {
    local stack="$1"
    shift
    local directory="$deploy_root/$stack"
    printf '[%s] docker compose' "$stack"
    printf ' %s' "$@"
    printf '\n'
    if [[ "$dry_run" == true ]]; then return; fi
    docker compose --project-directory "$directory" \
        --env-file "$directory/.env" --file "$directory/compose.yaml" \
        --project-name "automate-$stack" "$@"
}

if [[ "$action" == stop || "$action" == restart || "$action" == down ]]; then
    stop_command=stop
    if [[ "$action" == down ]]; then stop_command=down; fi
    for stack in "${shutdown_order[@]}"; do
        run_compose "$stack" "$stop_command"
    done
fi

if [[ "$action" == start || "$action" == restart ]]; then
    for stack in "${startup_order[@]}"; do
        if [[ "$build" == true && "$stack" == app ]]; then
            run_compose "$stack" up -d --build
        else
            run_compose "$stack" up -d
        fi
    done
elif [[ "$action" == status || "$action" == pull ]]; then
    for stack in "${startup_order[@]}"; do
        if [[ "$action" == status ]]; then
            run_compose "$stack" ps --all
        else
            run_compose "$stack" pull --ignore-buildable
        fi
    done
fi
