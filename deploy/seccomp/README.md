# Seccomp profile for Chromium's sandbox

The server image runs Chromium with its own sandbox: renderers run in user, PID, and network
namespaces apart from the browser's, confined to an empty directory, under Chromium's seccomp-bpf
filter. Creating those namespaces needs syscalls that Docker's default seccomp profile, and
containerd's `RuntimeDefault`, deny to a container without `CAP_SYS_ADMIN`. Under them the browser
cannot start ("No usable sandbox!"), and the server stays unready and says why.

[`chromium.json`](chromium.json) is Docker's default profile plus the smallest allowance the sandbox
needs, resolved into a form that Docker, Podman, containerd, and CRI-O all read the same way. See
[Chromium's sandbox](../../docs/security.md#chromiums-sandbox) for what the sandbox protects and
where it is supported.

```bash
docker run --security-opt seccomp=deploy/seccomp/chromium.json ... atli-reports-server
```

- **Docker Compose:** `security_opt: ["seccomp=../../deploy/seccomp/chromium.json"]`, relative to
  the Compose file, as in [`src/Atli.Reports.Server/docker-compose.yml`](../../src/Atli.Reports.Server/docker-compose.yml).
- **Kubernetes:** a `Localhost` profile installed on each node; see
  [`deploy/kubernetes/reports.yaml`](../kubernetes/reports.yaml).
- **Aspire:** `AddReportsServer` passes the profile to Docker or Podman in local runs; deployment
  targets need it configured separately (see the [Aspire guide](../../docs/aspire.md#deploy)).

## Provenance

| | |
| --- | --- |
| Upstream | [moby/profiles](https://github.com/moby/profiles) `seccomp/default.json`, tag `seccomp/v0.2.4` |
| Commit | [`245180c51918481c0525424b3ee025d2b435d46c`](https://github.com/moby/profiles/blob/245180c51918481c0525424b3ee025d2b435d46c/seccomp/default.json) |
| File SHA-256 | `785b2429264afba4d594320337cb17f144f3c7d51585f9805eef72e28f4f9334` |
| Why this version | The version moby's `go.mod` required on 2026-10-02. `main` already restricts more socket address families; those changes were unreleased. |

[`.github/scripts/chromium-seccomp-profile.py`](../../.github/scripts/chromium-seccomp-profile.py)
downloads that file, checks its hash, and writes `chromium.json`. `check` (the default) regenerates
it and fails if the committed file differs; the server image workflow runs it. To move to a newer
upstream, change the commit and hash in the script, run it with `write`, and review the diff. The
script refuses conditions it does not know how to resolve.

```bash
.github/scripts/chromium-seccomp-profile.py check
.github/scripts/chromium-seccomp-profile.py write
```

## The exact delta from Docker's default

**1. Conditions resolved.** Docker's profile carries conditional rules (`includes` and `excludes` on
capabilities, architectures, and kernel versions) that Docker resolves when it starts a container.
containerd does not: it decodes a `Localhost` profile as the OCI runtime spec's `LinuxSeccomp`,
silently ignores those fields, and so applies every conditional rule. As a `Localhost` profile,
Docker's file would let `mount`, `setns`, `bpf`, `ptrace`, and every `unshare` and `clone` past
seccomp in a container without capabilities. Tested with kind v0.31.0 (Kubernetes 1.35.0, containerd
2.2.0, runc 1.2.9): under Docker's unmodified file, `unshare --user --mount true` succeeded in a pod
without capabilities, which Docker itself denies; under `chromium.json` it fails.

The script therefore resolves the conditions the way Docker does for this image: a container with no
capabilities (the image runs as a non-root user and the examples drop every capability) on an
`amd64` or `arm64` host with Linux 4.8 or later.

- Rules that need a capability are dropped: `CAP_SYS_ADMIN` (`bpf`, `clone`, `clone3`, `mount`,
  `setns`, `unshare`, and the rest of that group), `CAP_SYS_CHROOT` (`chroot`), `CAP_SYS_PTRACE`,
  `CAP_SYS_MODULE`, `CAP_SYS_BOOT`, `CAP_SYS_PACCT`, `CAP_SYS_RAWIO`, `CAP_SYS_TIME`,
  `CAP_SYS_TTY_CONFIG`, `CAP_SYS_NICE`, `CAP_SYSLOG`, `CAP_BPF`, `CAP_PERFMON`, and
  `CAP_DAC_READ_SEARCH`.
- Rules excluded only with `CAP_SYS_ADMIN` are kept: `clone` without namespace flags, and `clone3`
  answered with `ENOSYS`.
- Rules for `ppc64le`, `s390`/`s390x`, and `riscv64` are dropped. The `amd64`-only rules
  (`arch_prctl`, `modify_ldt`) and the `arm`/`arm64`-only rules (`arm_fadvise64_64`,
  `arm_sync_file_range`, `sync_file_range2`, `breakpoint`, `cacheflush`, `set_tls`) are both kept:
  those syscalls do not exist on the other architecture, so one file serves both.
- `ptrace`, `process_vm_readv`, and `process_vm_writev` (Linux 4.8 or later) are kept.
- `archMap` becomes `architectures`: `SCMP_ARCH_X86_64`, `SCMP_ARCH_X86`, `SCMP_ARCH_X32`,
  `SCMP_ARCH_AARCH64`, `SCMP_ARCH_ARM`.

**2. Three rules added**, each with a `comment` in the file:

| Syscall | Allowed when | Why |
| --- | --- | --- |
| `clone` | No `CLONE_NEWNS`, `CLONE_NEWCGROUP`, `CLONE_NEWUTS`, or `CLONE_NEWIPC` (`args[0] & 0x0E020000 == 0`) | Chromium clones its zygote into new user, PID, and network namespaces and its renderers into new PID namespaces. Docker's default allows `clone` only without any namespace flag. |
| `unshare` | Only `CLONE_NEWUSER` (`args[0] & 0xEFFFFFFF == 0`) | Chromium checks for, and enters, a new user namespace with `unshare(CLONE_NEWUSER)`. |
| `chroot` | Always | Chromium confines sandboxed processes to an empty directory (`/proc/self/fdinfo/`) inside their user namespace. Docker's default allows `chroot` only with `CAP_SYS_CHROOT`, which `--cap-drop ALL` removes. |

Nothing else changes. Inside the new user namespace the process holds capabilities over it, so the
argument filters matter: mount, cgroup, UTS, and IPC namespaces stay denied there too, which keeps
`mount` and its relatives out of reach.

### What the profile allows

Seccomp filters every process in the container, so the allowance is not Chromium's alone. Any
process there can `clone` itself into a new user namespace together with new PID and network
namespaces, and holds every capability inside them, `CAP_NET_ADMIN` and `CAP_NET_RAW` included.
That covers the .NET server, Chromium's browser process (which runs outside the sandbox), and
whatever code an attacker runs through a bug in either or through an escape from Chromium's sandbox.
It opens kernel code that Docker's default profile keeps away from a container without
capabilities: netfilter (`nf_tables`), packet sockets (`AF_PACKET`), and the rest of the networking
code that checks those capabilities against the network namespace's owner. Unprivileged user
namespaces have been a common route to kernel privilege escalations through that code, and they
are why Docker's default denies these calls. On the host described below, a probe in a container
with `--cap-drop ALL`, `no-new-privileges`, and a non-root user called
`clone(CLONE_NEWUSER | CLONE_NEWNET)`: under `chromium.json` the child had a full effective
capability set in a new network namespace and opened an `AF_PACKET` socket; under Docker's default
profile the call failed with `EPERM`.

The trade is still the recommended one for HTML that is not fully trusted. The renderer, which
parses the HTML and runs its JavaScript, is where an exploit is likeliest to land, and Chromium's
own seccomp filter denies these calls to renderers: to reach the wider kernel surface, an exploit
there needs a second bug that escapes the sandbox. Without the sandbox (`NoSandbox` under Docker's
default profile), the same renderer bug runs code as the server's user in the container. What the
profile widens is what a compromise of the server or of the browser process can reach. Keep the rest
of the baseline: `--cap-drop ALL`, `no-new-privileges`, a non-root user, and a host kernel with
current security fixes.

## How the allowance was established

On 2026-10-02, with chrome-headless-shell 154.0.8037.92 in this image, on Docker 29.4.0 (OrbStack,
Linux 7.0.14, arm64), each container started with `--cap-drop ALL`, `--read-only`,
`--security-opt no-new-privileges:true`, and the image's non-root user:

- Docker's default profile alone: "No usable sandbox!".
- Without the `clone` or the `unshare` rule: "No usable sandbox!". Without the `chroot` rule:
  `Check failed: sys_chroot("/proc/self/fdinfo/") == 0`.
- With `clone` also denying `CLONE_NEWPID`, or also denying `CLONE_NEWNET`, or allowing only
  `CLONE_NEWUSER`: the zygote exits (`zygote_linux.cc ... write: Broken pipe`).
- With the three rules as above: the server converted the four
  [benchmark fixtures](../../benchmarks/fixtures) (invoice, inlined assets, 49-page table, and the
  chart with `waitForSignal`), no browser process had `--no-sandbox`, and renderers ran in their
  own PID namespaces and outside the browser's user namespace, with a second seccomp filter
  (Chromium's).
- In a container with the same settings, `unshare --user` succeeded and `unshare --user` with
  `--mount`, `--uts`, `--ipc`, `--cgroup`, `--pid`, or `--net` failed: the `unshare` rule allows a
  user namespace alone. `clone` with `CLONE_NEWUSER` and `CLONE_NEWPID`, `CLONE_NEWNET`, or both
  succeeded, by design (see [What the profile allows](#what-the-profile-allows)); with
  `CLONE_NEWNS`, `CLONE_NEWUTS`, `CLONE_NEWIPC`, or `CLONE_NEWCGROUP` it failed.

The same profile passed [the Kubernetes validation](../../.github/scripts/validate-kubernetes-security.sh)
in kind as a `Localhost` profile. CI runs the server image's smoke test, which checks the sandbox,
on `amd64` and `arm64` GitHub runners.

## Not verified

- Ubuntu 23.10+ and 24.04 hosts restrict unprivileged user namespaces through AppArmor
  (`kernel.apparmor_restrict_unprivileged_userns=1`). The kernel applies that restriction to
  processes AppArmor does not confine. Verified for Docker only: the smoke test passes on GitHub's
  `ubuntu-24.04` runners (amd64 and arm64) with the sysctl at `1`, under Docker Engine 28.0.4 and
  its `docker-default` profile, which declares no AppArmor ABI. A missing ABI declaration therefore
  does not by itself stop the sandbox. Docker Engine 29.4.3 and later declare ABI 3.0 (moby/profiles
  `apparmor/v0.2.1`, when the host has `/etc/apparmor.d/abi/3.0`), which does not mediate user
  namespaces. Pods under containerd's default AppArmor profile on Ubuntu nodes were not tested.
  Containers that run AppArmor-unconfined (`--security-opt apparmor=unconfined`, privileged
  containers, pods in kind) are affected: they need the sysctl set to `0`, an AppArmor profile that
  allows user namespaces, or the opt-out.
- Podman, CRI-O, and Kubernetes nodes other than kind were not tested.
- Under gVisor, Chromium's own seccomp-bpf filter crashes on arm64 (`seccomp-bpf failure in syscall
  nr=0x7b`) and the browser then hangs, so the
  [isolated worker experiment](../../docs/isolated-workers.md) keeps its browser unsandboxed inside
  the gVisor boundary. That was seen with the worker image; the server image under gVisor, and
  gVisor on amd64, were not tested.
