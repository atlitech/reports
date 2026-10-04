#!/usr/bin/perl
# DNS from inside a renderer, as key=value lines: which resolvers answer, and which record types come
# back. Runs in the server image, which has only perl-base (no dig, no nslookup), so it writes raw
# DNS queries over UDP. A name that does not exist returning NXDOMAIN means the query reached that
# zone's authoritative servers: a lookup of <data>.<a domain an attacker controls> would deliver
# <data> to the attacker, and a TXT answer carries data back in.
#
#   perl dns-probe.pl
use strict;
use warnings;
use IO::Socket::INET;

my %types = (A => 1, TXT => 16);
my ($resolver) = map { /^nameserver\s+(\S+)/ ? $1 : () } do {
  open my $file, '<', '/etc/resolv.conf' or die "resolv.conf: $!\n"; <$file>
};
$resolver //= 'none';

sub query {
  my ($server, $name, $type) = @_;
  my $id = int(rand(65536));
  my $packet = pack('n n n n n n', $id, 0x0100, 1, 0, 0, 0)
    . join('', map { chr(length $_) . $_ } split /\./, $name) . "\0" . pack('n n', $types{$type}, 1);
  my $socket = IO::Socket::INET->new(PeerAddr => $server, PeerPort => 53, Proto => 'udp') or return 'error';
  my $response = '';
  eval {
    local $SIG{ALRM} = sub { die "timeout\n" };
    alarm 5;
    $socket->send($packet);
    $socket->recv($response, 4096);
    alarm 0;
  };
  return 'timeout' if $@ || length($response) < 12;
  my ($rid, $flags, $qd, $an) = unpack('n n n n', $response);
  my %rcodes = (0 => 'NOERROR', 2 => 'SERVFAIL', 3 => 'NXDOMAIN', 5 => 'REFUSED');
  return sprintf '%s/answers=%d', $rcodes{$flags & 0xF} // ($flags & 0xF), $an;
}

my $random = join '', map { ('a' .. 'z')[rand 26] } 1 .. 12;
print "resolver=$resolver\n";
for my $case (
  [$resolver, 'example.com', 'A'],
  [$resolver, 'example.com', 'TXT'],
  [$resolver, "$random.example.com", 'A'],
  [$resolver, 'microsoft.com', 'TXT'],
  ['168.63.129.16', 'example.com', 'A'],
  ['8.8.8.8', 'example.com', 'A'],
  ['1.1.1.1', 'example.com', 'A'],
) {
  my ($server, $name, $type) = @$case;
  my $label = $name eq "$random.example.com" ? 'random.example.com' : $name;
  (my $key = "udp53_${server}_${type}_$label") =~ s/[^A-Za-z0-9]+/_/g;
  print "$key=", query($server, $name, $type), "\n";
}
