@echo off
REM Docker run script for PKS CLI (Batch wrapper for PowerShell)

powershell -ExecutionPolicy Bypass -File "%~dp0docker-run.ps1" %*