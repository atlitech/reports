#!/usr/bin/env python3
"""Generates or checks deploy/seccomp/chromium.json, the seccomp profile the server image needs for
Chromium's own sandbox.

The profile is Docker's default seccomp profile from a pinned moby/profiles commit, resolved the
way Docker resolves it for this container, plus three rules for Chromium's namespace sandbox:

  1. Download seccomp/default.json at UPSTREAM_COMMIT and verify its SHA-256.
  2. Resolve its conditional rules for a container with no capabilities (the image runs as a
     non-root user and the examples drop every capability) on an amd64 or arm64 host with Linux
     4.8 or later: rules that need a capability are dropped, rules for other architectures are
     dropped, and archMap becomes the plain architectures list of amd64 and arm64.
  3. Append CHROMIUM_RULES.
  4. Check the invariants the sandbox's narrowing depends on (check_invariants): clone3 answered
     with ENOSYS, clone and unshare allowed only with their flags filtered, and no allowance for
     setns, mount and its relatives, pivot_root or bpf.

Resolving the conditions makes one file that Docker, Podman, containerd and CRI-O all read the same
way. containerd (Kubernetes Localhost profiles) decodes a profile as the OCI runtime-spec's
LinuxSeccomp and silently ignores Docker's includes/excludes, which would turn every
capability-gated rule of the unresolved profile into an unconditional allowance.

  chromium-seccomp-profile.py check   compare deploy/seccomp/chromium.json with a fresh generation
                                      (the default); exit 1 when they differ
  chromium-seccomp-profile.py write   regenerate deploy/seccomp/chromium.json

To move to a newer upstream profile, change UPSTREAM_COMMIT and UPSTREAM_SHA256, run `write`, and
review the diff; the script refuses conditions it does not know how to resolve.
"""
import argparse
import hashlib
import json
import sys
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
PROFILE = REPO / "deploy" / "seccomp" / "chromium.json"

# moby/profiles tag seccomp/v0.2.4, the version moby's go.mod requires as of 2026-10-02.
UPSTREAM_COMMIT = "245180c51918481c0525424b3ee025d2b435d46c"
UPSTREAM_URL = (
    f"https://raw.githubusercontent.com/moby/profiles/{UPSTREAM_COMMIT}/seccomp/default.json"
)
UPSTREAM_SHA256 = "785b2429264afba4d594320337cb17f144f3c7d51585f9805eef72e28f4f9334"

# The architectures the server image is built for, as Docker names them in includes/excludes
# (Go's GOARCH), and the seccomp architecture of each in archMap.
TARGET_ARCHES = {"amd64": "SCMP_ARCH_X86_64", "arm64": "SCMP_ARCH_AARCH64"}

# Kernel versions a rule may require (includes.minKernel) and still be kept: the profile assumes a
# kernel at least this new.
ASSUMED_KERNEL = (4, 8)

# Syscalls of rules that apply on one target architecture only, which do not exist on the other one
# (nor on its compat sub-architectures), so keeping the rule in a profile for both changes nothing
# there. Any other rule that applies to some target architectures but not all is refused.
SINGLE_ARCH_SYSCALLS = {
    "amd64": {"arch_prctl", "modify_ldt"},
    "arm64": {
        "arm_fadvise64_64",
        "arm_sync_file_range",
        "breakpoint",
        "cacheflush",
        "set_tls",
        "sync_file_range2",
    },
}

CLONE_NEWNS = 0x00020000
CLONE_NEWCGROUP = 0x02000000
CLONE_NEWUTS = 0x04000000
CLONE_NEWIPC = 0x08000000
CLONE_NEWUSER = 0x10000000

# The only additions to Docker's default profile. Each was found necessary by removing it and
# starting chrome-headless-shell 154 with its sandbox (deploy/seccomp/README.md).
CHROMIUM_RULES = [
    {
        "names": ["clone"],
        "action": "SCMP_ACT_ALLOW",
        "args": [
            {
                "index": 0,
                "value": CLONE_NEWNS | CLONE_NEWCGROUP | CLONE_NEWUTS | CLONE_NEWIPC,
                "op": "SCMP_CMP_MASKED_EQ",
            }
        ],
        "comment": "Atli Reports: Chromium's namespace sandbox clones processes into new user, PID "
        "and network namespaces. Mount, cgroup, UTS and IPC namespaces stay denied.",
    },
    {
        "names": ["unshare"],
        "action": "SCMP_ACT_ALLOW",
        "args": [
            {
                "index": 0,
                "value": 0xFFFFFFFF & ~CLONE_NEWUSER,
                "op": "SCMP_CMP_MASKED_EQ",
            }
        ],
        "comment": "Atli Reports: Chromium checks for and enters a new user namespace with "
        "unshare(CLONE_NEWUSER). Every other unshare flag stays denied.",
    },
    {
        "names": ["chroot"],
        "action": "SCMP_ACT_ALLOW",
        "comment": "Atli Reports: Chromium confines its sandboxed processes to an empty directory "
        "inside their user namespace. Docker's default allows chroot only with CAP_SYS_CHROOT.",
    },
]


ENOSYS = 38

# Syscalls that create, enter or change namespaces and mounts, which no rule may allow outright.
# clone and unshare are allowed only with their flags filtered. clone3 must be answered with
# ENOSYS: seccomp cannot read its flags (they live in a struct, not a register), so allowing it would
# allow every namespace type, and ENOSYS (unlike EPERM) makes glibc and Chromium fall back to clone.
FILTERED_SYSCALLS = {"clone", "unshare"}
DENIED_SYSCALLS = {
    "bpf",
    "fsconfig",
    "fsmount",
    "fsopen",
    "fspick",
    "mount",
    "mount_setattr",
    "move_mount",
    "open_tree",
    "pivot_root",
    "setns",
    "umount",
    "umount2",
}


def check_invariants(profile):
    """Fails when the generated profile breaks a rule the sandbox's narrowing depends on."""
    clone3_rules = 0
    for rule in profile["syscalls"]:
        names = set(rule["names"])
        allows = rule["action"] != "SCMP_ACT_ERRNO"
        if "clone3" in names:
            clone3_rules += 1
            if allows or rule.get("errnoRet") != ENOSYS:
                fail(
                    f"A rule answers clone3 with {rule['action']} (errno {rule.get('errnoRet')}); it "
                    "must stay SCMP_ACT_ERRNO with ENOSYS, because seccomp cannot filter clone3's "
                    "flags (deploy/seccomp/README.md)."
                )
        if allows and names & FILTERED_SYSCALLS and not rule.get("args"):
            fail(f"A rule allows {sorted(names & FILTERED_SYSCALLS)} without filtering its flags.")
        if allows and names & DENIED_SYSCALLS:
            fail(f"A rule allows {sorted(names & DENIED_SYSCALLS)}, which the profile must deny.")
    if clone3_rules == 0:
        fail(
            "No rule answers clone3 with ENOSYS; the default action (EPERM) would stop glibc and "
            "Chromium from falling back to clone."
        )


def fail(message):
    print(f"::error::{message}", file=sys.stderr)
    sys.exit(1)


def download_upstream():
    with urllib.request.urlopen(UPSTREAM_URL, timeout=60) as response:
        body = response.read()
    digest = hashlib.sha256(body).hexdigest()
    if digest != UPSTREAM_SHA256:
        fail(f"{UPSTREAM_URL} has SHA-256 {digest}, expected {UPSTREAM_SHA256}.")
    return json.loads(body)


def kernel(version):
    return tuple(int(part) for part in version.split("."))


def applies_to(rule):
    """The target architectures a rule applies to, for a container with no capabilities."""
    includes = rule.get("includes") or {}
    excludes = rule.get("excludes") or {}
    unknown = (set(includes) | set(excludes)) - {"arches", "caps", "minKernel"}
    if unknown or "minKernel" in excludes:
        fail(f"Unsupported condition in upstream rule {rule['names']}: {sorted(unknown)}.")
    # A rule that needs a capability never applies; excluding one the container lacks changes
    # nothing.
    if includes.get("caps"):
        return set()
    minimum = includes.get("minKernel")
    if minimum is not None and kernel(minimum) > ASSUMED_KERNEL:
        fail(f"Upstream rule {rule['names']} needs Linux {minimum}; review ASSUMED_KERNEL.")
    arches = set(TARGET_ARCHES)
    if includes.get("arches"):
        arches &= set(includes["arches"])
    return arches - set(excludes.get("arches") or [])


def generate():
    upstream = download_upstream()
    unknown = set(upstream) - {"defaultAction", "defaultErrnoRet", "archMap", "syscalls"}
    if unknown:
        fail(f"Unsupported top-level fields in the upstream profile: {sorted(unknown)}.")

    architectures = []
    for entry in upstream["archMap"]:
        if entry["architecture"] in TARGET_ARCHES.values():
            architectures += [entry["architecture"], *entry.get("subArchitectures", [])]

    syscalls = []
    for rule in upstream["syscalls"]:
        arches = applies_to(rule)
        if not arches:
            continue
        if arches != set(TARGET_ARCHES):
            allowed = set().union(*(SINGLE_ARCH_SYSCALLS[arch] for arch in arches))
            if not set(rule["names"]) <= allowed:
                fail(
                    f"Upstream rule {rule['names']} applies only to {sorted(arches)}; review "
                    "SINGLE_ARCH_SYSCALLS before resolving it into a profile for both."
                )
        syscalls.append(
            {key: value for key, value in rule.items() if key not in ("includes", "excludes")}
        )

    profile = {
        "defaultAction": upstream["defaultAction"],
        "defaultErrnoRet": upstream["defaultErrnoRet"],
        "architectures": architectures,
        "syscalls": syscalls + CHROMIUM_RULES,
    }
    check_invariants(profile)
    return json.dumps(profile, indent=2) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("command", nargs="?", choices=["check", "write"], default="check")
    command = parser.parse_args().command

    generated = generate()
    if command == "write":
        PROFILE.parent.mkdir(parents=True, exist_ok=True)
        PROFILE.write_text(generated)
        print(f"Wrote {PROFILE.relative_to(REPO)} from moby/profiles {UPSTREAM_COMMIT}.")
        return
    current = PROFILE.read_text() if PROFILE.exists() else ""
    if current != generated:
        fail(
            f"{PROFILE.relative_to(REPO)} is not Docker's default profile at moby/profiles "
            f"{UPSTREAM_COMMIT} plus the Chromium rules; run "
            ".github/scripts/chromium-seccomp-profile.py write and review the diff."
        )
    print(f"{PROFILE.relative_to(REPO)} matches moby/profiles {UPSTREAM_COMMIT} plus the Chromium rules.")


if __name__ == "__main__":
    main()
