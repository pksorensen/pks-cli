@echo off
REM Docker publish script for PKS CLI (Batch wrapper for PowerShell)

powershell -ExecutionPolicy Bypass -File "%~dp0docker-publish.ps1" %*