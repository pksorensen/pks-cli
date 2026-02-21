@echo off
REM Docker build script for PKS CLI (Batch wrapper for PowerShell)

powershell -ExecutionPolicy Bypass -File "%~dp0docker-build.ps1" %*