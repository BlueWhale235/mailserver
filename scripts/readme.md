.\scripts\build.ps1 -Configuration Release


.\scripts\publish-portable.ps1 -Runtime linux-x64

.\scripts\publish-portable.ps1 -Runtime win-x64,linux-x64,linux-arm64

bash scripts/build-linux-release.sh v2.0.0



.\scripts\publish-plugins.ps1 -Configuration Release
