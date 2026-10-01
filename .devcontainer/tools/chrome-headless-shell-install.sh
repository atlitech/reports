#!/bin/bash

apt-get update
apt-get install -y wget gnupg lsb-release fonts-liberation fonts-roboto
wget --quiet --output-document=- https://dl-ssl.google.com/linux/linux_signing_key.pub | gpg --dearmor > /etc/apt/trusted.gpg.d/google-archive.gpg
sh -c 'echo "deb http://dl.google.com/linux/chrome/deb/ stable main" >> /etc/apt/sources.list.d/google.list'
apt-get update
apt-get install chromium -y --no-install-recommends
rm -rf /var/lib/apt/lists/*
