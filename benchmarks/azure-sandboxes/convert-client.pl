#!/usr/bin/perl
# Converts one request body with the server in the same sandbox and prints
# "<status> <seconds> <pdf bytes> pdf|not-pdf". It runs inside the server image, which has only
# perl-base: no HTTP::Tiny and no Time::HiRes, so it speaks HTTP/1.1 over a socket and reads
# CLOCK_MONOTONIC with the raw clock_gettime syscall.
#
#   perl convert-client.pl <request.json> <api-key-file>
use strict;
use warnings;
use IO::Socket::INET;
use POSIX qw(uname);

my %clock_gettime = (x86_64 => 228, aarch64 => 113);
my $clock_gettime = $clock_gettime{(uname())[4]} or die "Unsupported architecture\n";
sub now {
  my $timespec = "\0" x 16;
  syscall($clock_gettime, 1, $timespec) == 0 or die "clock_gettime: $!\n";
  my ($seconds, $nanoseconds) = unpack 'q q', $timespec;
  return $seconds + $nanoseconds / 1e9;
}

my ($body_file, $key_file) = @ARGV;
die "usage: $0 <request.json> <api-key-file>\n" unless defined $key_file;
my $body = do { local $/; open my $file, '<', $body_file or die "$body_file: $!\n"; <$file> };
my $key = do { local $/; open my $file, '<', $key_file or die "$key_file: $!\n"; <$file> };
$key =~ s/\s+\z//;

my $started = now();
my $socket = IO::Socket::INET->new(PeerAddr => '127.0.0.1', PeerPort => 8080, Proto => 'tcp')
  or die "connect: $!\n";
print {$socket} "POST /convert HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\n"
  . "X-Reports-Api-Key: $key\r\nContent-Length: " . length($body) . "\r\nConnection: close\r\n\r\n"
  . $body;
my $response = '';
while (sysread($socket, my $chunk, 1 << 20)) { $response .= $chunk; }
my $elapsed = now() - $started;

my ($head, $rest) = split /\r\n\r\n/, $response, 2;
my ($status) = $head =~ m{\AHTTP/1\.1 (\d+)};
my $payload = '';
if ($head =~ /^Transfer-Encoding:\s*chunked/mi) {
  while ($rest =~ s/\A([0-9a-fA-F]+)\r\n//) {
    my $length = hex $1;
    last if $length == 0;
    $payload .= substr($rest, 0, $length, '');
    $rest =~ s/\A\r\n//;
  }
} else {
  $payload = $rest // '';
}
printf "%s %.4f %d %s\n", $status // 0, $elapsed, length($payload),
  substr($payload, 0, 5) eq '%PDF-' ? 'pdf' : 'not-pdf';
