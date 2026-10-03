#!/usr/bin/perl
# Probes the seccomp profile a container or pod runs under, from inside it, with the raw syscalls
# Chromium's namespace sandbox makes. Perl is used because the server image already has it (Ubuntu's
# perl-base), so the probe runs in the image under test without adding anything to it.
#
#   perl seccomp-probe.pl chromium   expect deploy/seccomp/chromium.json
#   perl seccomp-probe.pl default    expect Docker's default profile (a control: the probe itself works)
#
# Each case runs in a child process, so a namespace one case enters never affects the next. It prints
# one line per case and exits 1 when any result differs from the expectation.
#
# Without seccomp, the kernel lets an unprivileged process create a user namespace together with any
# other namespace, so EPERM from a combination the kernel would allow can only come from the
# profile (its defaultErrnoRet). clone3's flags live in a struct that seccomp cannot read, so the
# profile must answer clone3 with ENOSYS, which makes glibc and Chromium fall back to clone, whose
# flags it can filter. Allowing clone3 would allow every namespace type; see deploy/seccomp/README.md.
use strict;
use warnings;
use POSIX qw(_exit uname);

my $mode = shift // '';
die "usage: $0 chromium|default\n" unless $mode eq 'chromium' || $mode eq 'default';

my %syscalls = (
  x86_64  => { clone => 56,  unshare => 272, chroot => 161, clone3 => 435 },
  aarch64 => { clone => 220, unshare => 97,  chroot => 51,  clone3 => 435 },
);
my $machine = (uname())[4];
my $nr = $syscalls{$machine} or die "Unsupported architecture: $machine\n";

my $SIGCHLD         = 17;
my $CLONE_NEWNS     = 0x00020000;
my $CLONE_NEWCGROUP = 0x02000000;
my $CLONE_NEWUTS    = 0x04000000;
my $CLONE_NEWIPC    = 0x08000000;
my $CLONE_NEWUSER   = 0x10000000;
my $CLONE_NEWPID    = 0x20000000;
my $CLONE_NEWNET    = 0x40000000;

# clone with a null stack forks; the clone's own child exits at once.
sub raw_clone {
  my ($flags) = @_;
  my $pid = syscall($nr->{clone}, $flags | $SIGCHLD, 0, 0, 0, 0);
  return $! + 0 if $pid < 0;
  _exit(0) if $pid == 0;
  waitpid($pid, 0);
  return 0;
}

sub raw_unshare { syscall($nr->{unshare}, $_[0]) < 0 ? $! + 0 : 0 }

# The kernel rejects a null clone_args with EINVAL, so ENOSYS can only come from the profile.
sub raw_clone3 { syscall($nr->{clone3}, 0, 0) < 0 ? $! + 0 : 0 }

# chroot needs CAP_SYS_CHROOT, which a process holds only inside a user namespace it created.
sub chroot_in_user_namespace {
  my $error = raw_unshare($CLONE_NEWUSER);
  return "unshare: $error" if $error;
  my $path = "/\0";
  return syscall($nr->{chroot}, $path) < 0 ? $! + 0 : 0;
}

my $EPERM  = 1;
my $ENOSYS = 38;
my $chromium = $mode eq 'chromium';
my @cases = (
  [ 'clone3 (must stay ENOSYS)', sub { raw_clone3() }, $ENOSYS ],
  [ 'clone without namespaces', sub { raw_clone(0) }, 0 ],
  [ 'clone user', sub { raw_clone($CLONE_NEWUSER) }, $chromium ? 0 : $EPERM ],
  [ 'clone user+pid+net (Chromium\'s zygote)',
    sub { raw_clone($CLONE_NEWUSER | $CLONE_NEWPID | $CLONE_NEWNET) }, $chromium ? 0 : $EPERM ],
  [ 'clone user+mount', sub { raw_clone($CLONE_NEWUSER | $CLONE_NEWNS) }, $EPERM ],
  [ 'clone user+cgroup', sub { raw_clone($CLONE_NEWUSER | $CLONE_NEWCGROUP) }, $EPERM ],
  [ 'clone user+uts', sub { raw_clone($CLONE_NEWUSER | $CLONE_NEWUTS) }, $EPERM ],
  [ 'clone user+ipc', sub { raw_clone($CLONE_NEWUSER | $CLONE_NEWIPC) }, $EPERM ],
  [ 'unshare user', sub { raw_unshare($CLONE_NEWUSER) }, $chromium ? 0 : $EPERM ],
  [ 'unshare user+mount', sub { raw_unshare($CLONE_NEWUSER | $CLONE_NEWNS) }, $EPERM ],
  [ 'unshare user+net', sub { raw_unshare($CLONE_NEWUSER | $CLONE_NEWNET) }, $EPERM ],
  [ 'unshare user+pid', sub { raw_unshare($CLONE_NEWUSER | $CLONE_NEWPID) }, $EPERM ],
  [ 'chroot in a user namespace', \&chroot_in_user_namespace, $chromium ? 0 : 'unshare: 1' ],
);

my $failed = 0;
for my $case (@cases) {
  my ($name, $probe, $expected) = @$case;
  # The result travels through a pipe, so it can be a number or a short message.
  pipe(my $reader, my $writer) or die "pipe: $!\n";
  my $pid = fork() // die "fork: $!\n";
  if ($pid == 0) {
    close $reader;
    print {$writer} $probe->();
    close $writer;
    _exit(0);
  }
  close $writer;
  my $actual = do { local $/; <$reader> } // '';
  close $reader;
  waitpid($pid, 0);
  my $describe = sub { $_[0] eq '0' ? 'allowed' : $_[0] =~ /^\d+$/ ? 'errno ' . $_[0] : $_[0] };
  my $ok = $actual eq "$expected";
  $failed ||= !$ok;
  printf "  %-4s %-42s %s%s\n", $ok ? 'ok' : 'FAIL', $name, $describe->($actual),
    $ok ? '' : ' (expected ' . $describe->("$expected") . ')';
}
exit($failed ? 1 : 0);
