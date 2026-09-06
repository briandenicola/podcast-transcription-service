#!/bin/sh
#
# Fixes ownership of the mounted volumes, then drops to an unprivileged user.
#
# A bind mount replaces whatever the image had at that path, so the chown done at build time is
# masked the moment ./data is mounted over /data: the directory arrives owned by whoever created
# it on the host, and the unprivileged user in the container cannot write to it. Correcting it
# here is the only place that can see the mount.
#
# PUID/PGID let the files be owned by a host account instead, which matters when the media
# library lives on a NAS or a shared disk.
set -e

DEFAULT_UID="$(id -u app 2>/dev/null || echo 1654)"
DEFAULT_GID="$(id -g app 2>/dev/null || echo 1654)"

PUID="${PUID:-$DEFAULT_UID}"
PGID="${PGID:-$DEFAULT_GID}"

if [ "$(id -u)" = "0" ]; then
    for dir in /data /media; do
        mkdir -p "$dir"

        # Only recurse when the top level is already wrong. chown -R over a media library of any
        # size is slow, and doing it on every start would delay each restart for no reason.
        owner="$(stat -c '%u:%g' "$dir" 2>/dev/null || echo '')"
        if [ "$owner" != "${PUID}:${PGID}" ]; then
            echo "entrypoint: taking ownership of $dir as ${PUID}:${PGID}"
            chown -R "${PUID}:${PGID}" "$dir" 2>/dev/null \
                || echo "entrypoint: warning: could not chown $dir; it may be read-only or a network mount"
        fi
    done

    exec gosu "${PUID}:${PGID}" "$@"
fi

# Already unprivileged — someone set `user:` in compose, so ownership is their business.
echo "entrypoint: running as $(id -u):$(id -g); skipping ownership fixes"
exec "$@"
