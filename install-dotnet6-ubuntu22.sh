#!/bin/bash

# .NET 6 Installer for Ubuntu 22.04
# Author: soosho
# Source: https://github.com/soosho/miningcore
# License: MIT

set -e

echo "🛠️ Installing prerequisites..."
sudo apt update
sudo apt install -y wget apt-transport-https software-properties-common

echo "📦 Adding Microsoft package repository..."
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb

echo "🔁 Updating APT sources..."
sudo apt update

echo "📥 Installing .NET 6 SDK..."
sudo apt install -y dotnet-sdk-6.0

echo "📂 Configuring environment variables..."
# Append to ~/.bashrc if not already present
if ! grep -q "DOTNET_ROOT" ~/.bashrc; then
  echo 'export DOTNET_ROOT=$HOME/dotnet' >> ~/.bashrc
  echo 'export PATH=$DOTNET_ROOT:$PATH' >> ~/.bashrc
fi

# Apply changes to current session
export DOTNET_ROOT=$HOME/dotnet
export PATH=$DOTNET_ROOT:$PATH

echo "✅ Verifying installation..."
if dotnet --info > /dev/null 2>&1; then
  dotnet --info
  echo "🎉 .NET 6 SDK installation completed successfully!"
else
  echo "❌ .NET installation failed. Please check your PATH and try again."
fi
