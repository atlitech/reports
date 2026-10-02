#!/bin/sh
# Keep the browser tree in its own session. .NET Kill(entireProcessTree: true) stops
# processes while enumerating descendants; an orphaned shared process group can receive
# SIGHUP during that cleanup. Do not let browser shutdown signal the worker or its init.
exec /usr/bin/setsid --wait /opt/chrome-headless-shell/chrome-headless-shell "$@"
